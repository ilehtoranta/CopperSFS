using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    public struct TrimResult
    {
        public AmigaFileSystemDiscardLimits Limits;
        /// <summary>Successfully completed ranges (or proposed ranges in dry run).
        /// A failed command may already have discarded additional free blocks.</summary>
        public uint Blocks, Ranges;
    }

    /// <summary>Explicit maintenance only. The caller must serialize this whole
    /// call with ALL operations on this mount, including reads, other trim calls
    /// and allocation. Do not yield ownership between scanning and completion.
    /// control must not alias filesystem scratch or overlay storage.
    /// QueryOnly performs read-only discovery; DryRun checkpoints and validates
    /// the free map but sends no discard. No trim list survives the call.</summary>
    public static bool Trim<T>(ref T p, ref Work w, APTR control, uint flags, out TrimResult result)
        where T : struct, IAmigaDiscardFileSystemPlatform
    {
        result = default; w.Error = DOS.Error.None;
        if (flags > AmigaSmartFileSystemTrimPacket.QueryOnly)
            return Fail(ref w, DOS.Error.BadNumber);
        if (!p.QueryDiscard(control, out result.Limits)) return Fail(ref w, DOS.Error.ActionNotKnown);
        if (!result.Limits.Valid) return Fail(ref w, DOS.Error.BadNumber);
        if (flags == AmigaSmartFileSystemTrimPacket.QueryOnly) return true;
        if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);

        // Undo recovery must precede committing acknowledged RAM changes. Only
        // after the clean marker's final barrier may the free bitmap authorize
        // irreversible discard. Both durability policies use this same gate.
        if (!Recover(ref p, ref w) || !Flush(ref p, ref w) || !Prepare(ref p, ref w)) return false;
        if (!p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (w.BlockBytes < 512 || w.BlockBytes > uint.MaxValue / 8 ||
            w.BlockCount <= w.ReservedBlocks + 1) return Fail(ref w, DOS.Error.DiskNotValidated);
        var bits = (w.BlockBytes - 12) * 8;
        var maps = (w.BlockCount - 1) / bits + 1;
        if (w.BitmapBase < w.ReservedBlocks || maps > w.BlockCount - 1 ||
            w.BitmapBase >= w.BlockCount - maps || w.RootContainer < w.ReservedBlocks ||
            w.RootContainer >= w.BlockCount - 3 || w.AdminContainer < w.ReservedBlocks ||
            w.AdminContainer >= w.BlockCount - 1 || w.NodeRoot < w.ReservedBlocks ||
            w.NodeRoot >= w.BlockCount - 1 || w.ExtentRoot < w.ReservedBlocks || w.ExtentRoot >= w.BlockCount - 1)
            return Fail(ref w, DOS.Error.DiskNotValidated);

        // Preflight every bitmap before the first destructive command. Check
        // known permanent metadata as well as checksums and own-block numbers.
        // The mounted allocator and this scan share the bitmap's allocation
        // semantics; neither treats a checksum as a full filesystem fsck.
        for (var map = 0u; map < maps; map++)
        {
            if (!Read(ref p, ref w, w.BitmapBase + map, BitmapId, w.Bitmap)) return false;
            var first = map * bits;
            var remaining = w.BlockCount - first;
            var count = remaining < bits ? remaining : bits;
            if (!TrimCheckAllocated(ref p, ref w, first, count, w.RootContainer) ||
                !TrimCheckAllocated(ref p, ref w, first, count, w.RootContainer + 2) ||
                !TrimCheckAllocated(ref p, ref w, first, count, w.AdminContainer) ||
                !TrimCheckAllocated(ref p, ref w, first, count, w.NodeRoot) ||
                !TrimCheckAllocated(ref p, ref w, first, count, w.ExtentRoot)) return false;
            var from = first > w.BitmapBase ? first : w.BitmapBase;
            var end = first + count < w.BitmapBase + maps ? first + count : w.BitmapBase + maps;
            for (var block = from; block < end; block++)
                if (TrimFree(ref p, w.Bitmap, block - first))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
        }

        uint run = 0, length = 0;
        for (var map = 0u; map < maps; map++)
        {
            if (!Read(ref p, ref w, w.BitmapBase + map, BitmapId, w.Bitmap)) return false;
            var first = map * bits;
            var remaining = w.BlockCount - first;
            var count = remaining < bits ? remaining : bits;
            for (var wordIndex = 0u; wordIndex < count; wordIndex += 32)
            {
                var word = p.ReadUInt32(w.Bitmap, 12 + (int)(wordIndex / 32) * 4);
                var wordBits = count - wordIndex < 32 ? count - wordIndex : 32;
                var wordBlock = first + wordIndex;
                if (word == uint.MaxValue && wordBits == 32 && wordBlock >= w.ReservedBlocks &&
                    wordBlock < w.BlockCount - 32)
                {
                    if (length == 0) run = wordBlock;
                    length += 32;
                    continue;
                }
                for (var bit = 0u; bit < wordBits; bit++)
                {
                    var block = wordBlock + bit;
                    if (block >= w.ReservedBlocks && block < w.BlockCount - 1 &&
                        !TrimPermanent(ref w, block, maps) && (word & (1u << (int)(31 - bit))) != 0)
                    { if (length == 0) run = block; length++; }
                    else if (length != 0)
                    {
                        if (!TrimRange(ref p, ref w, control, run, length, flags, ref result)) return false;
                        length = 0;
                    }
                    if (word == 0) break;
                }
            }
        }
        return length == 0 || TrimRange(ref p, ref w, control, run, length, flags, ref result);
    }

    private static bool TrimPermanent(ref Work w, uint block, uint maps) =>
        block == 0 || block == w.BlockCount - 1 ||
        (block >= w.BitmapBase && block - w.BitmapBase < maps) ||
        block == w.RootContainer || block == w.RootContainer + 2 ||
        block == w.AdminContainer || block == w.NodeRoot || block == w.ExtentRoot;

    private static bool TrimFree<T>(ref T p, APTR bitmap, uint index)
        where T : struct, IAmigaGuestMemory =>
        (p.ReadUInt32(bitmap, 12 + (int)(index / 32) * 4) & (1u << (int)(31 - index % 32))) != 0;

    private static bool TrimCheckAllocated<T>(ref T p, ref Work w, uint first, uint count, uint block)
        where T : struct, IAmigaWritableFileSystemPlatform => block < first || block - first >= count ||
        !TrimFree(ref p, w.Bitmap, block - first) || Fail(ref w, DOS.Error.DiskNotValidated);

    private static bool TrimRange<T>(ref T p, ref Work w, APTR control, uint block, uint count,
        uint flags, ref TrimResult result) where T : struct, IAmigaDiscardFileSystemPlatform
    {
        var granularity = result.Limits.Granularity;
        var remainder = block % granularity;
        var alignment = result.Limits.Alignment;
        var skip = remainder <= alignment ? alignment - remainder : granularity - (remainder - alignment);
        if (skip >= count) return true;
        block += skip; count -= skip; count -= count % granularity;
        var maximum = result.Limits.MaximumBlocks - result.Limits.MaximumBlocks % granularity;
        while (count != 0)
        {
            var take = count < maximum ? count : maximum;
            if (flags != AmigaSmartFileSystemTrimPacket.DryRun && !p.DiscardBlocks(control, block, take))
                return Fail(ref w, DOS.Error.SeekError);
            result.Blocks += take; result.Ranges++;
            block += take; count -= take;
        }
        return true;
    }
}
