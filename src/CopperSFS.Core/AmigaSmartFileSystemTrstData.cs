using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    // A complete new data run need not be copied into the redo arena or journal.
    // Verify that it is unreachable in the ORIGINAL allocation bitmap, then
    // issue its data writes outside the metadata overlay. CommitTrst's journal
    // flush makes these writes durable before TRFA can publish the new mapping.
    // Before publication a failed operation can leave bytes only in free space.
    // The ordinary staged path remains available when a run was not originally
    // free (for example a block retired earlier in the same transaction).
    private static bool WriteNewDataRun<T>(ref T p, ref Work w, uint first, uint count, APTR source)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var overlay = p.RedoOverlay;
        if (!w.RedoStaging || overlay.IsNull)
            return p.WriteDataBlocks(first, count, source) || Fail(ref w, DOS.Error.DiskNotValidated);
        p.RedoOverlay = APTR.Null;
        var checkedRange = TrstRangeOriginallyFree(ref p, ref w, first, count, out var free);
        // Dispatch the fallback on the original ref T. A default interface
        // implementation forwarding to a mutating struct method would box/copy
        // that platform and lose its counters or newly created pending state.
        var success = checkedRange && (!free || (p.SupportsUnpublishedDataWrites
            ? p.WriteUnpublishedDataBlocks(first, count, source) : p.WriteDataBlocks(first, count, source)));
        p.RedoOverlay = overlay;
        if (!checkedRange) return false;
        if (!success) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (free) return true;
        return p.WriteDataBlocks(first, count, source) || Fail(ref w, DOS.Error.NoFreeStore);
    }

    private static bool TrstRangeOriginallyFree<T>(ref T p, ref Work w, uint first, uint count, out bool free)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        free = false;
        if (count == 0 || first < w.ReservedBlocks || first >= w.BlockCount - 1 ||
            count > w.BlockCount - 1 - first) return Fail(ref w, DOS.Error.DiskNotValidated);
        while (count != 0)
        {
            if (!BitmapPosition(ref w, first, out var map, out var offset, out _)) return false;
            // No other block access occurs while inspecting this bitmap. Use
            // its verified view rather than copying an entire block to scratch.
            var bitmap = View(ref p, ref w, map, BitmapId, w.Journal);
            if (bitmap.IsNull) return false;
            // A group may own unpublished data from earlier accepted Writes.
            // A failing later Write must not overwrite those checkpoint bytes,
            // even when the durable bitmap still calls the blocks free.
            var checkpoint = w.RedoCheckpoint.IsNull ? APTR.Null :
                AmigaSmartFileSystemRedoArena.Find(ref p, w.RedoCheckpoint, map);
            var withinWord = first & 31;
            var remaining = (w.BlockBytes - (uint)offset) * 8 - withinWord;
            if (remaining > count) remaining = count;
            first += remaining; count -= remaining;
            while (remaining != 0)
            {
                var bits = 32u - withinWord;
                if (bits > remaining) bits = remaining;
                var runMask = (uint.MaxValue >> (int)withinWord) & (uint.MaxValue << (int)(32 - withinWord - bits));
                if ((p.ReadUInt32(bitmap, offset) & runMask) != runMask) return true;
                if (checkpoint.IsNotNull && (p.ReadUInt32(checkpoint, offset) & runMask) != runMask) return true;
                remaining -= bits; offset += 4; withinWord = 0;
            }
        }
        free = true;
        return true;
    }
}
