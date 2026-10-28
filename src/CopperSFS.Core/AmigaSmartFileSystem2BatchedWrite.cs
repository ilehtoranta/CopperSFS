using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystem2WriteCore
{
    /// <summary>
    /// Writes one atomic request using bounded contiguous transfers. batchBuffer
    /// is caller-owned, separate from the request and four Work buffers. Uses at
    /// most 64 KiB, rounded down to whole filesystem blocks and MaxTransfer.
    /// Fragmented full-block ranges of at least 16 KiB may move to a new run.
    /// With no batch buffer, uses the scalar implementation. actual is zero on
    /// failure; recover before accessing a failed transaction. No heap allocation.
    /// </summary>
    public static bool WriteBatched<T>(ref T p, ref Work w, ref Request r,
        APTR batchBuffer, uint batchBytes, out uint actual)
        where T : struct, IAmigaBlockRangeFileSystemPlatform
        => WriteBatched(ref p, ref w, ref r, batchBuffer, batchBytes, out actual, true);

    /// <summary>Set relocateFragmented to false to keep existing data blocks in place.</summary>
    public static bool WriteBatched<T>(ref T p, ref Work w, ref Request r,
        APTR batchBuffer, uint batchBytes, out uint actual, bool relocateFragmented)
        where T : struct, IAmigaBlockRangeFileSystemPlatform
    {
        actual = 0;
        if (batchBuffer.IsNull && batchBytes == 0) return Write(ref p, ref w, ref r, out actual);
        var bytes = p.Geometry.BlockBytes;
        if (bytes < 512 || r.Length > int.MaxValue || !p.IsMapped(r.Buffer, r.Length))
            return Fail(ref w, DOS.Error.BadNumber);
        var limit = batchBytes < 65536 ? batchBytes : 65536;
        if (limit > p.Geometry.MaxTransfer) limit = p.Geometry.MaxTransfer;
        var capacity = limit / bytes;
        var scratchBytes = capacity * bytes;
        if (batchBuffer.IsNull || capacity == 0 || !p.IsMapped(batchBuffer, scratchBytes) ||
            Overlap(batchBuffer, scratchBytes, r.Buffer, r.Length) ||
            Overlap(batchBuffer, scratchBytes, w.Meta, bytes) || Overlap(batchBuffer, scratchBytes, w.Data, bytes) ||
            Overlap(batchBuffer, scratchBytes, w.Bitmap, bytes) || Overlap(batchBuffer, scratchBytes, w.Journal, bytes))
            return Fail(ref w, DOS.Error.NoFreeStore);
        if (!Prepare(ref p, ref w)) return false;
        if (!Sfs2Layout.Object(ref p, r.Node, w.Meta, out var at))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if ((p.ReadUInt8(w.Meta, at + 26) & (128 | 64)) != 0) return Fail(ref w, DOS.Error.ObjectWrongType);
        if (((p.ReadUInt32(w.Meta, at + 8) ^ 15) & 4) != 0) return Fail(ref w, DOS.Error.WriteProtected);
        var size = FileSize(ref p, w.Meta, at); var first = p.ReadUInt32(w.Meta, at + 12);
        if (r.Position > size) return Fail(ref w, DOS.Error.SeekError);
        if (r.Position > MaxFileSize || r.Length > MaxFileSize - r.Position) return Fail(ref w, DOS.Error.ObjectTooLarge);
        if (r.Length == 0) return true;
        var end = r.Position + r.Length;
        var oldBlocks = size / bytes + (size % bytes == 0 ? 0u : 1u);
        var newBlocks = end / bytes + (end % bytes == 0 ? 0u : 1u);
        if (newBlocks > oldBlocks)
        {
            if (!Read(ref p, ref w, w.RootContainer, ObjectId, w.Meta)) return false;
            if (newBlocks - oldBlocks > p.ReadUInt32(w.Meta, (int)bytes - 28)) return Fail(ref w, DOS.Error.DiskFull);
        }
        if (!Begin(ref p, ref w)) return false;
        uint written = 0;
        while (written < r.Length)
        {
            var position = r.Position + written;
            var index = position / bytes;
            var within = (uint)(position % bytes);
            var remaining = r.Length - written;
            var wanted = within != 0 || remaining < bytes ? 1 : remaining / bytes;
            if (wanted > capacity) wanted = capacity;
            if (!FileRun(ref p, ref w, first, index, out var block, out var available, out var last, out var lastSize)) return false;
            var allocated = block == 0;
            uint count;
            if (allocated)
            {
                if (index < oldBlocks) return Fail(ref w, DOS.Error.DiskNotValidated);
                var preferred = last == 0 ? 0u : last + lastSize;
                if (lastSize < uint.MaxValue && wanted > uint.MaxValue - lastSize)
                    wanted = uint.MaxValue - lastSize;
                // A run never crosses a bitmap boundary. The modified bitmap
                // passed to Put excludes the whole run from journal allocation.
                if (!FindFreeRun(ref p, ref w, preferred, 1, wanted, true, out block, out count)) return false;
                if (count == 0) return Fail(ref w, DOS.Error.DiskFull);
                if (!MarkDataRun(ref p, ref w, block, count)) return false;
                if (last != 0 && block == preferred && lastSize < uint.MaxValue)
                {
                    if (!GrowExtentRun(ref p, ref w, last, block, count)) return false;
                }
                else
                {
                    if (!InsertExtent(ref p, ref w, block, last == 0 ? r.Node | 0x80000000 : last)) return false;
                    if (count > 1 && !GrowExtentRun(ref p, ref w, block, block + 1, count - 1)) return false;
                }
                if (first == 0) first = block;
            }
            else
            {
                if (index >= oldBlocks) return Fail(ref w, DOS.Error.DiskNotValidated);
                var candidate = (uint)(wanted < oldBlocks - index ? wanted : oldBlocks - index);
                // Keep partial blocks and small writes in place. Relocate only
                // full-block ranges of at least 16 KiB crossing physical gaps.
                if (relocateFragmented && within == 0 && candidate > available && candidate * bytes >= 16384)
                {
                    if (!RelocateOverwrite(ref p, ref w, ref first, r.Node, last, block - last, candidate,
                        APTR.FromPointer(r.Buffer.Raw + written), batchBuffer, out var moved)) return false;
                    if (moved) { written += candidate * bytes; continue; }
                }
                count = available < wanted ? available : wanted;
                if (count > oldBlocks - index) count = (uint)(oldBlocks - index);
            }
            if (within != 0 || remaining < bytes)
            {
                if (allocated) p.Clear(w.Data, bytes);
                else if (!p.ReadBlock(block, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
                var take = bytes - within;
                if (take > remaining) take = remaining;
                p.Copy(APTR.FromPointer(r.Buffer.Raw + written), APTR.FromPointer(w.Data.Raw + within), take);
                // New data was free before this transaction: restoring the
                // allocation/extent metadata makes it unreachable on rollback.
                if (!(allocated ? p.WriteBlock(block, w.Data) : JournalWrite(ref p, ref w, block, w.Data)))
                    return FailUnlessSet(ref w);
                written += take;
            }
            else
            {
                if (!allocated && !JournalRun(ref p, ref w, block, ref count, batchBuffer)) return false;
                var take = count * bytes;
                p.Copy(APTR.FromPointer(r.Buffer.Raw + written), batchBuffer, take);
                if (!p.WriteBlocks(block, count, batchBuffer)) return Fail(ref w, DOS.Error.DiskNotValidated);
                written += take;
            }
        }
        if (!Sfs2Layout.Object(ref p, r.Node, w.Meta, out at)) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (first != p.ReadUInt32(w.Meta, at + 12)) p.WriteUInt32(w.Meta, at + 12, first);
        if (end > size) SetFileSize(ref p, w.Meta, at, end);
        p.WriteUInt32(w.Meta, at + 8, p.ReadUInt32(w.Meta, at + 8) & ~16u);
        if (!Put(ref p, ref w, p.ReadUInt32(w.Meta, 8), w.Meta) || !Finish(ref p, ref w)) return false;
        actual = written;
        return true;
    }

    private static bool FailUnlessSet(ref Work w) => w.Error != DOS.Error.None ? false : Fail(ref w, DOS.Error.DiskNotValidated);
    private static bool Overlap(APTR a, uint aBytes, APTR b, uint bBytes) =>
        aBytes != 0 && bBytes != 0 && (ulong)a.Raw < (ulong)b.Raw + bBytes && (ulong)b.Raw < (ulong)a.Raw + aBytes;

    private static bool FileRun<T>(ref T p, ref Work w, uint first, ulong index,
        out uint block, out uint available, out uint last, out uint lastSize)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        block = available = last = lastSize = 0;
        uint key = first; ulong start = 0;
        for (var guard = 0u; key != 0 && guard < p.Geometry.BlockCount; guard++)
        {
            if (!Sfs2Layout.Extent(ref p, w.ExtentRoot, key, w.Meta, out var count, out var next) ||
                key < p.Geometry.ReservedBlocks || (ulong)key + count > p.Geometry.BlockCount - 1)
                return Fail(ref w, DOS.Error.DiskNotValidated);
            last = key; lastSize = count;
            if (index < start + count)
            {
                block = key + (uint)(index - start); available = count - (uint)(index - start);
                return true;
            }
            start += count; key = next;
        }
        return key == 0 && index == start || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    // Builds a search bitmap from current AND original free bits, excluding
    // journal blocks. Scans the journal once per bitmap, not once per candidate.
    // Only Bitmap, Meta and Journal scratch are changed; Data is preserved.
    private static bool FreeSearchMap<T>(ref T p, ref Work w, uint mapIndex)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var map = w.BitmapBase + mapIndex;
        if (map < w.BitmapBase || !Read(ref p, ref w, map, BitmapId, w.Bitmap)) return false;
        var bits = (p.Geometry.BlockBytes - 12) * 8;
        uint entry = w.JournalHead;
        for (var guard = 0u; entry != 0 && guard < p.Geometry.BlockCount; guard++)
        {
            if (!Read(ref p, ref w, entry, UndoId, w.Journal)) return false;
            var image = p.ReadUInt32(w.Journal, 20);
            if (image >= p.Geometry.BlockCount) return Fail(ref w, DOS.Error.DiskNotValidated);
            ClearSearchBit(ref p, w.Bitmap, mapIndex, bits, entry);
            ClearSearchBit(ref p, w.Bitmap, mapIndex, bits, image);
            if (p.ReadUInt32(w.Journal, 16) == map)
            {
                if (!p.ReadBlock(image, w.Meta) || BlockSum(ref p, w.Meta) != p.ReadUInt32(w.Journal, 24))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                // Intersect every saved version if a caller ever logs this map
                // more than once; a block must have been free in all of them.
                for (var at = 12; at < p.Geometry.BlockBytes; at += 4)
                    p.WriteUInt32(w.Bitmap, at, p.ReadUInt32(w.Bitmap, at) & p.ReadUInt32(w.Meta, at));
            }
            entry = p.ReadUInt32(w.Journal, 12);
        }
        return entry == 0 || Fail(ref w, DOS.Error.DiskNotValidated);
    }
    private static void ClearSearchBit<T>(ref T p, APTR map, uint mapIndex, uint bits, uint block)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (block / bits != mapIndex) return;
        var index = block % bits; var offset = 12 + (int)(index / 32) * 4;
        p.WriteUInt32(map, offset, p.ReadUInt32(map, offset) & ~(1u << (int)(31 - index % 32)));
    }
    private static bool ScanFreeRun<T>(ref T p, ref Work w, uint start, uint minimum, uint maximum,
        bool oneMap, bool onlyPrefix, out uint first, out uint count)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        first = count = 0;
        var bits = (p.Geometry.BlockBytes - 12) * 8;
        var loaded = uint.MaxValue;
        for (var block = start; block < p.Geometry.BlockCount - 1; block++)
        {
            var map = block / bits;
            if (map != loaded)
            {
                if (oneMap && count != 0)
                {
                    if (count >= minimum) return true;
                    first = count = 0;
                    if (onlyPrefix) return true;
                }
                if (!FreeSearchMap(ref p, ref w, map)) return false;
                loaded = map;
            }
            var index = block % bits;
            var free = (p.ReadUInt32(w.Bitmap, 12 + (int)(index / 32) * 4) & (1u << (int)(31 - index % 32))) != 0;
            if (w.AdminReservation != 0 && block >= w.AdminReservation && block - w.AdminReservation < 32) free = false;
            if (block == w.RootContainer + 2) free = false;
            if (free)
            {
                if (count == 0) first = block;
                if (++count == maximum) return true;
            }
            else
            {
                if (count >= minimum) return true;
                first = count = 0;
                if (onlyPrefix) return true;
            }
        }
        if (count < minimum) first = count = 0;
        return true;
    }
    private static bool FindFreeRun<T>(ref T p, ref Work w, uint preferred, uint minimum, uint maximum,
        bool oneMap, out uint first, out uint count) where T : struct, IAmigaWritableFileSystemPlatform
    {
        first = count = 0;
        if (preferred >= p.Geometry.ReservedBlocks && preferred < p.Geometry.BlockCount - 1)
        {
            if (!ScanFreeRun(ref p, ref w, preferred, minimum, maximum, oneMap, true, out first, out count)) return false;
            if (count != 0) return true;
        }
        return ScanFreeRun(ref p, ref w, p.Geometry.ReservedBlocks, minimum, maximum, oneMap, false, out first, out count);
    }
    private static bool MarkDataRun<T>(ref T p, ref Work w, uint first, uint count)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!BitmapPosition(ref p, ref w, first, out var map, out _, out _) ||
            !Read(ref p, ref w, map, BitmapId, w.Bitmap)) return false;
        var bits = (p.Geometry.BlockBytes - 12) * 8;
        if (count == 0 || count > bits - first % bits) return Fail(ref w, DOS.Error.DiskNotValidated);
        for (var i = 0u; i < count; i++)
        {
            var bit = (first + i) % bits; var offset = 12 + (int)(bit / 32) * 4; var mask = 1u << (int)(31 - bit % 32);
            var word = p.ReadUInt32(w.Bitmap, offset);
            if ((word & mask) == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
            p.WriteUInt32(w.Bitmap, offset, word & ~mask);
        }
        if (!Put(ref p, ref w, map, w.Bitmap)) return false;
        w.FreeDelta -= (int)count;
        return true;
    }
    private static bool GrowExtentRun<T>(ref T p, ref Work w, uint key, uint nextBlock, uint added)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!ExtentLeaf(ref p, ref w, key, out var leaf)) return false;
        var count = p.ReadUInt16(w.Meta, 12); var stride = p.ReadUInt8(w.Meta, 15);
        for (var i = 0; i < count; i++)
        {
            var at = 16 + i * stride;
            if (p.ReadUInt32(w.Meta, at) != key) continue;
            var length = p.ReadUInt32(w.Meta, at + 12);
            if (added == 0 || added > uint.MaxValue - length || (ulong)key + length != nextBlock)
                return Fail(ref w, DOS.Error.DiskNotValidated);
            p.WriteUInt32(w.Meta, at + 12, (length + added));
            return Put(ref p, ref w, leaf, w.Meta);
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    // Publish a chain of ordinary CSUN records in one step. Recovery retains the
    // original format and restores the entire request, including earlier batches.
    private static bool JournalRun<T>(ref T p, ref Work w, uint target, ref uint count, APTR buffer)
        where T : struct, IAmigaBlockRangeFileSystemPlatform
    {
        if (!FindFreeRun(ref p, ref w, w.JournalCursor, 2, count * 2, false, out var images, out var free)) return false;
        if (free == 0)
        {
            // Fragmented free space may have no adjacent pair. Retain the scalar
            // allocator, which can place the image and entry in separate holes.
            count = 1;
            if (!p.ReadBlock(target, buffer)) return Fail(ref w, DOS.Error.DiskNotValidated);
            return JournalWrite(ref p, ref w, target, buffer);
        }
        count = free / 2;
        var entries = images + count;
        if (!p.ReadBlocks(target, count, buffer) || !p.WriteBlocks(images, count, buffer))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var bytes = p.Geometry.BlockBytes;
        for (var i = 0u; i < count; i++)
        {
            var record = APTR.FromPointer(buffer.Raw + i * bytes);
            var sum = BlockSum(ref p, record);
            p.Clear(record, bytes);
            p.WriteUInt32(record, 0, UndoId); p.WriteUInt32(record, 8, entries + i);
            p.WriteUInt32(record, 12, i == 0 ? w.JournalHead : entries + i - 1);
            p.WriteUInt32(record, 16, target + i); p.WriteUInt32(record, 20, images + i);
            p.WriteUInt32(record, 24, sum); Seal(ref p, record);
        }
        if (!p.WriteBlocks(entries, count, buffer) || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        var head = entries + count - 1;
        p.Clear(w.Journal, bytes);
        p.WriteUInt32(w.Journal, 0, TransactionFailed); p.WriteUInt32(w.Journal, 8, w.RootContainer + 2);
        p.WriteUInt32(w.Journal, 12, head); Seal(ref p, w.Journal);
        if (!p.WriteBlock(w.RootContainer + 2, w.Journal) || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        w.JournalHead = head;
        w.JournalCursor = entries + count;
        return true;
    }
}
