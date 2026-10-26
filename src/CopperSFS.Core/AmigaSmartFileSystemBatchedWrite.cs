using Amiga;
using BufferHeader = CopperSFS.AmigaSmartFileSystemWriteBufferLayout.Header;
using BufferEntry = CopperSFS.AmigaSmartFileSystemWriteBufferLayout.Entry;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    /// <summary>
    /// Writes one atomic request using bounded contiguous transfers. batchBuffer
    /// is caller-owned, separate from the request and four Work buffers. Uses at
    /// most 64 KiB, rounded down to whole filesystem blocks and MaxTransfer.
    /// Full-block ranges may move to a new run: 16 KiB when fragmented,
    /// 64 KiB when contiguous. Reserved streams also relocate a partial EOF block.
    /// With no batch buffer, uses the scalar implementation. actual is zero on
    /// failure; recover before accessing a failed transaction. No heap allocation.
    /// </summary>
    public static bool WriteBatched<T>(ref T p, ref Work w, ref Request r,
        APTR batchBuffer, uint batchBytes, out uint actual)
        where T : struct, IAmigaWritableFileSystemPlatform
        => WriteBatched(ref p, ref w, ref r, batchBuffer, batchBytes, out actual, true);

    /// <summary>The legacy relocateFragmented flag controls all relocation; false keeps existing data blocks in place.</summary>
    public static bool WriteBatched<T>(ref T p, ref Work w, ref Request r,
        APTR batchBuffer, uint batchBytes, out uint actual, bool relocateFragmented)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        // Small writes keep the existing buffered policy. Large requests first
        // checkpoint acknowledged operations, then use one strict CSUN transaction.
        if ((r.Length < 4096 && !HasOverwriteReservation(ref p, ref w, r.Node)) || (batchBuffer.IsNull && batchBytes == 0))
            return Write(ref p, ref w, ref r, out actual);
        // The native writer already batches newly allocated contiguous appends
        // while retaining its write-back policy. Keep that path for one run.
        var blockBytes = p.BlockBytes;
        if (blockBytes != 0 && r.Length <= 65536 && r.Position % blockBytes == 0 && r.Length % blockBytes == 0 &&
            AmigaSmartFileSystemCore.Describe(ref p, r.Node, w.Meta, out var size, out var directory, out _) &&
            !directory && r.Position == size)
            return Write(ref p, ref w, ref r, out actual);
        actual = 0;
        if (!Flush(ref p, ref w) || !Recover(ref p, ref w)) return false;
        var buffered = w.Buffered; var staged = w.Batch;
        if (buffered.IsNotNull)
        {
            p.WriteUInt32(buffered, BufferHeader.Hidden, 1);
            p.WriteUInt32(buffered, BufferHeader.GeometryValid, 0);
            p.WriteUInt32(buffered, BufferHeader.CachedNode, 0);
        }
        w.Buffered = APTR.Null; w.Batch = APTR.Null;
        var success = WriteBatchedOperation(ref p, ref w, ref r, batchBuffer, batchBytes, out actual, relocateFragmented);
        if (!success) ReleaseOverwriteReservation(ref p, ref w, r.Node);
        w.Buffered = buffered; w.Batch = staged;
        if (buffered.IsNotNull) p.WriteUInt32(buffered, BufferHeader.Hidden, 0);
        return success;
    }

    private static bool WriteBatchedOperation<T>(ref T p, ref Work w, ref Request r,
        APTR batchBuffer, uint batchBytes, out uint actual, bool relocateFragmented)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        actual = 0;
        if (batchBuffer.IsNull && batchBytes == 0) return Write(ref p, ref w, ref r, out actual);
        var bytes = p.BlockBytes;
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
        if (!Object(ref p, ref w, r.Node, w.Meta, out var at))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if ((p.ReadUInt8(w.Meta, at + 24) & (128 | 64)) != 0) return Fail(ref w, DOS.Error.ObjectWrongType);
        if (((p.ReadUInt32(w.Meta, at + 8) ^ 15) & 4) != 0) return Fail(ref w, DOS.Error.WriteProtected);
        var size = p.ReadUInt32(w.Meta, at + 16); var first = p.ReadUInt32(w.Meta, at + 12);
        if (r.Position > size) return Fail(ref w, DOS.Error.SeekError);
        if (r.Length > uint.MaxValue - r.Position) return Fail(ref w, DOS.Error.ObjectTooLarge);
        if (r.Length == 0) return true;
        var end = r.Position + r.Length;
        if (!RecycledSizeChange(ref p, ref w, p.ReadUInt32(w.Meta, 12), size, end > size ? end : size)) return false;
        var oldBlocks = size / bytes + (size % bytes == 0 ? 0u : 1u);
        var newBlocks = end / bytes + (end % bytes == 0 ? 0u : 1u);
        if (newBlocks > oldBlocks)
        {
            if (!Read(ref p, ref w, w.RootContainer, ObjectId, w.Meta)) return false;
            if (newBlocks - oldBlocks > p.ReadUInt32(w.Meta, (int)bytes - 28)) return Fail(ref w, DOS.Error.DiskFull);
        }
        if (!PlanOverwrite(ref p, ref w, ref r, size, first, capacity, relocateFragmented, out var reservation)) return false;
        if (!Begin(ref p, ref w)) return false;
        uint written = 0;
        while (written < r.Length)
        {
            var position = r.Position + written;
            var index = position / bytes;
            var within = position % bytes;
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
                if (lastSize < ushort.MaxValue && wanted > ushort.MaxValue - lastSize)
                    wanted = ushort.MaxValue - lastSize;
                // A run never crosses a bitmap boundary. The modified bitmap
                // passed to Put excludes the whole run from journal allocation.
                if (!FindFreeRun(ref p, ref w, preferred, 1, wanted, true, out block, out count)) return false;
                if (count == 0) return Fail(ref w, DOS.Error.DiskFull);
                if (!MarkDataRun(ref p, ref w, block, count)) return false;
                if (last != 0 && block == preferred && lastSize < ushort.MaxValue)
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
                var candidate = wanted < oldBlocks - index ? wanted : oldBlocks - index;
                if (!ReservedOverwriteRun(ref p, ref w, reservation, r.Node, index, candidate, out var reserved)) return false;
                // Reserved streams also move the final short packet/block.
                if (relocateFragmented && within == 0 &&
                    (reserved != 0 || candidate * bytes >= 65536 || candidate > available && candidate * bytes >= 16384))
                {
                    var source = APTR.FromPointer(r.Buffer.Raw + written);
                    var movedBytes = candidate * bytes;
                    if (remaining < bytes)
                    {
                        if (!p.ReadBlock(block, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
                        p.Copy(source, w.Data, remaining); source = w.Data; movedBytes = remaining;
                    }
                    if (!RelocateOverwrite(ref p, ref w, ref first, r.Node, last, block - last, candidate,
                        source, batchBuffer, reserved, out var moved)) return false;
                    if (moved)
                    {
                        AdvanceOverwriteReservation(ref p, ref w, reservation, r.Node, candidate);
                        written += movedBytes; continue;
                    }
                }
                count = available < wanted ? available : wanted;
                if (count > oldBlocks - index) count = oldBlocks - index;
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
                if (!p.WriteDataBlocks(block, count, batchBuffer)) return Fail(ref w, DOS.Error.DiskNotValidated);
                written += take;
            }
        }
        if (!Object(ref p, ref w, r.Node, w.Meta, out at)) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (first != p.ReadUInt32(w.Meta, at + 12)) p.WriteUInt32(w.Meta, at + 12, first);
        if (end > size) p.WriteUInt32(w.Meta, at + 16, end);
        p.WriteUInt32(w.Meta, at + 8, p.ReadUInt32(w.Meta, at + 8) & ~16u);
        if (!Put(ref p, ref w, p.ReadUInt32(w.Meta, 8), w.Meta) || !Finish(ref p, ref w)) return false;
        actual = written;
        return true;
    }

    private static bool FailUnlessSet(ref Work w) => w.Error != DOS.Error.None ? false : Fail(ref w, DOS.Error.DiskNotValidated);
    private static bool Overlap(APTR a, uint aBytes, APTR b, uint bBytes) =>
        aBytes != 0 && bBytes != 0 &&
        (a.Raw <= b.Raw ? b.Raw - a.Raw < aBytes : a.Raw - b.Raw < bBytes);

    private static bool FileRun<T>(ref T p, ref Work w, uint first, uint index,
        out uint block, out uint available, out uint last, out uint lastSize)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        block = available = last = lastSize = 0;
        uint key = first; ulong start = 0;
        for (var guard = 0u; key != 0 && guard < w.BlockCount; guard++)
        {
            if (!AmigaSmartFileSystemCore.Extent(ref p, w.ExtentRoot, key, w.Meta, w.BlockCount, w.BlockBytes, out var count, out var next) ||
                key < w.ReservedBlocks || (ulong)key + count > w.BlockCount - 1)
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
    private static bool FreeSearchMap<T>(ref T p, ref Work w, uint mapIndex, APTR except = default)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var map = w.BitmapBase + mapIndex;
        if (map < w.BitmapBase || !Read(ref p, ref w, map, BitmapId, w.Bitmap)) return false;
        var bits = (w.BlockBytes - 12) * 8;
        if (w.Buffered.IsNotNull)
        {
            // A buffered transaction's checkpoint and journal reservations have
            // the same exclusion rules as the published strict undo chain.
            var count = AmigaSmartFileSystemWriteBuffer.Count(ref p, w.Buffered);
            var slot = AmigaSmartFileSystemWriteBuffer.First(w.Buffered);
            for (var i = 0u; i < count; i++, slot = AmigaSmartFileSystemWriteBuffer.Next(slot, w.BlockBytes))
            {
                var target = p.ReadUInt32(slot, BufferEntry.Block);
                ClearSearchBit(ref p, w.Bitmap, mapIndex, bits, target);
                ClearSearchBit(ref p, w.Bitmap, mapIndex, bits, p.ReadUInt32(slot, BufferEntry.UndoImage));
                ClearSearchBit(ref p, w.Bitmap, mapIndex, bits, p.ReadUInt32(slot, BufferEntry.UndoEntry));
                if (target != map) continue;
                var checkpoint = AmigaSmartFileSystemWriteBuffer.Checkpoint(slot, w.BlockBytes);
                for (var at = 12; at < w.BlockBytes; at += 4)
                    p.WriteUInt32(w.Bitmap, at, p.ReadUInt32(w.Bitmap, at) & p.ReadUInt32(checkpoint, at));
            }
        }
        uint entry = w.JournalHead;
        for (var guard = 0u; entry != 0 && guard < w.BlockCount; guard++)
        {
            if (!Read(ref p, ref w, entry, UndoId, w.Journal)) return false;
            var image = p.ReadUInt32(w.Journal, 20);
            if (image >= w.BlockCount) return Fail(ref w, DOS.Error.DiskNotValidated);
            ClearSearchBit(ref p, w.Bitmap, mapIndex, bits, entry);
            ClearSearchBit(ref p, w.Bitmap, mapIndex, bits, image);
            if (p.ReadUInt32(w.Journal, 16) == map)
            {
                if (!p.ReadBlock(image, w.Meta) || BlockSum(ref p, w.Meta, w.BlockBytes) != p.ReadUInt32(w.Journal, 24))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                // Intersect every saved version if a caller ever logs this map
                // more than once; a block must have been free in all of them.
                for (var at = 12; at < w.BlockBytes; at += 4)
                    p.WriteUInt32(w.Bitmap, at, p.ReadUInt32(w.Bitmap, at) & p.ReadUInt32(w.Meta, at));
            }
            entry = p.ReadUInt32(w.Journal, 12);
        }
        if (entry != 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        ClearSearchBit(ref p, w.Bitmap, mapIndex, bits, w.RootContainer + 2);
        if (w.AdminReservation != 0)
            for (var i = 0u; i < 32; i++)
                ClearSearchBit(ref p, w.Bitmap, mapIndex, bits, w.AdminReservation + i);
        MaskOverwriteReservations(ref p, ref w, mapIndex, except);
        return true;
    }
    // A scanner consumes one map before its next block access. When no
    // checkpoint or reservation mask is needed, borrow the verified image and
    // avoid materializing a whole scratch block. Never edit a borrowed view.
    private static APTR FreeSearchMapView<T>(ref T p, ref Work w, uint mapIndex)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Buffered.IsNull && w.JournalHead == 0 && w.AdminReservation == 0 &&
            !HasOverwriteReservations(ref p, ref w))
        {
            var map = w.BitmapBase + mapIndex;
            if (map < w.BitmapBase) { Fail(ref w, DOS.Error.DiskNotValidated); return APTR.Null; }
            var image = View(ref p, ref w, map, BitmapId, w.Bitmap);
            if (image.IsNull) return APTR.Null;
            var bits = (w.BlockBytes - 12) * 8;
            var marker = w.RootContainer + 2;
            if (marker / bits == mapIndex)
            {
                var bit = marker % bits; var offset = 12 + (int)(bit >> 5) * 4;
                var mask = 1u << (int)(31 - (bit & 31));
                var word = p.ReadUInt32(image, offset);
                if ((word & mask) != 0)
                {
                    // Expire the read-only view with a new block access,
                    // even when the platform returned our supplied scratch.
                    // ReadMetadataBlock then provides mutable search scratch.
                    return FreeSearchMap(ref p, ref w, mapIndex) ? w.Bitmap : APTR.Null;
                }
            }
            return image;
        }
        return FreeSearchMap(ref p, ref w, mapIndex) ? w.Bitmap : APTR.Null;
    }

    private static void ClearSearchBit<T>(ref T p, APTR map, uint mapIndex, uint bits, uint block)
        where T : struct, IAmigaFileSystemPlatform
    {
        // mapIndex comes from a valid block's quotient by bits, so the
        // map start fits uint. Subtraction keeps the same half-open membership
        // test without dividing each reserved block twice. A wrapped difference
        // from a block below this map is rejected by the explicit lower bound.
        var first = mapIndex * bits;
        if (block < first) return;
        var index = block - first;
        if (index >= bits) return;
        var offset = 12 + (int)(index >> 5) * 4;
        p.WriteUInt32(map, offset, p.ReadUInt32(map, offset) & ~(1u << (int)(31 - index % 32)));
    }
    private static uint RunLeadingZeros(uint word)
    {
        if (word == 0) return 32;
        uint count = 0;
        if ((word & 0xFFFF0000) == 0) { count += 16; word <<= 16; }
        if ((word & 0xFF000000) == 0) { count += 8; word <<= 8; }
        if ((word & 0xF0000000) == 0) { count += 4; word <<= 4; }
        if ((word & 0xC0000000) == 0) { count += 2; word <<= 2; }
        if ((word & 0x80000000) == 0) count++;
        return count;
    }

    // The search map has already intersected old/current allocation and
    // excluded reservations. Skip occupied words without repeating per-run
    // bookkeeping, bounded to this bitmap and the volume's final data sector.
    private static uint SkipUsedBitmapWords<T>(ref T p, APTR source, uint words)
        where T : struct, IAmigaGuestMemory
    {
        var skipped = 0u; var pointer = source.Raw; var remaining = words;
        while (remaining >= 4 && (p.ReadUInt32(APTR.FromPointer(pointer)) | p.ReadUInt32(APTR.FromPointer(pointer), 4) |
            p.ReadUInt32(APTR.FromPointer(pointer), 8) | p.ReadUInt32(APTR.FromPointer(pointer), 12)) == 0)
        {
            pointer += 16; remaining -= 4; skipped += 128;
        }
        while (remaining != 0 && p.ReadUInt32(APTR.FromPointer(pointer)) == 0)
        {
            pointer += 4; remaining--; skipped += 32;
        }
        return skipped;
    }

    // Consume verified fully-free words within the caller's bitmap,
    // volume and requested-length bounds. No image access can expire this view.
    private static uint SkipFreeBitmapWords<T>(ref T p, APTR source, uint words)
        where T : struct, IAmigaGuestMemory
    {
        var skipped = 0u; var pointer = source.Raw; var remaining = words;
        while (remaining >= 4 && (p.ReadUInt32(APTR.FromPointer(pointer)) & p.ReadUInt32(APTR.FromPointer(pointer), 4) &
            p.ReadUInt32(APTR.FromPointer(pointer), 8) & p.ReadUInt32(APTR.FromPointer(pointer), 12)) == uint.MaxValue)
        {
            pointer += 16; remaining -= 4; skipped += 128;
        }
        while (remaining != 0 && p.ReadUInt32(APTR.FromPointer(pointer)) == uint.MaxValue)
        {
            pointer += 4; remaining--; skipped += 32;
        }
        return skipped;
    }

    private static bool ScanFreeRun<T>(ref T p, ref Work w, uint start, uint minimum, uint maximum,
        bool oneMap, bool onlyPrefix, out uint first, out uint count, bool durable = false)
        where T : struct, IAmigaWritableFileSystemPlatform
        => ScanFreeRunBounded(ref p, ref w, start, minimum, maximum, oneMap, onlyPrefix,
            out first, out count, durable, 0);

    private static bool ScanFreeRunBounded<T>(ref T p, ref Work w, uint start, uint minimum, uint maximum,
        bool oneMap, bool onlyPrefix, out uint first, out uint count, bool durable, uint searchEnd)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        first = count = 0;
        var bits = (w.BlockBytes - 12) * 8;
        var loaded = uint.MaxValue;
        var bitmap = APTR.Null;
        var block = start;
        var map = start / bits;
        var index = start - map * bits;
        var high = w.BlockCount - 1;
        if (searchEnd != 0 && searchEnd < high) high = searchEnd;
        while (block < high)
        {
            // A one-map result cannot consume space in the next bitmap.
            // If even an entirely free remainder cannot reach minimum, no
            // valid candidate can start or finish here. Skip it without
            // acquiring a metadata view; selected maps retain every check.
            if (oneMap && count < minimum && bits - index < minimum - count)
            {
                first = count = 0;
                var skip = bits - index;
                if (onlyPrefix || skip >= high - block) return true;
                block += skip; map++; index = 0;
                continue;
            }
            if (map != loaded)
            {
                if (oneMap && count != 0)
                {
                    if (count >= minimum) return true;
                    first = count = 0;
                    if (onlyPrefix) return true;
                }
                bitmap = durable ? TrstFreeSearchMap(ref p, ref w, map) : FreeSearchMapView(ref p, ref w, map);
                if (bitmap.IsNull) return false;
                loaded = map;
            }
            var bit = index & 31;
            var word = p.ReadUInt32(bitmap, 12 + (int)(index / 32) * 4) << (int)bit;
            if (word == 0 && bit == 0 && !onlyPrefix && count < minimum && high - block >= 32)
            {
                first = count = 0;
                var words = (bits - index) >> 5;
                if (words > (high - block) >> 5) words = (high - block) >> 5;
                var skipped = SkipUsedBitmapWords(ref p,
                    APTR.FromPointer(bitmap.Raw + 12 + (index >> 5) * 4), words);
                block += skipped; index += skipped;
                if (index == bits) { index = 0; map++; }
                continue;
            }
            if (word == uint.MaxValue && bit == 0 && maximum - count >= 32 && high - block >= 32)
            {
                if (count == 0) first = block;
                var words = (bits - index) >> 5;
                if (words > (high - block) >> 5) words = (high - block) >> 5;
                if (words > (maximum - count) >> 5) words = (maximum - count) >> 5;
                var skipped = SkipFreeBitmapWords(ref p,
                    APTR.FromPointer(bitmap.Raw + 12 + (index >> 5) * 4), words);
                count += skipped; block += skipped; index += skipped;
                if (count == maximum) return true;
                if (index == bits) { index = 0; map++; }
                continue;
            }
            var free = (word & 0x80000000) != 0;
            // Consume a run of equal bits, bounded to this word and the volume.
            // The search bitmap already excludes all live reservations.
            var span = RunLeadingZeros(free ? ~word : word);
            if (span > 32 - bit) span = 32 - bit;
            if (span > high - block) span = high - block;
            if (free)
            {
                if (count == 0) first = block;
                if (span >= maximum - count) { count = maximum; return true; }
                count += span;
            }
            else
            {
                if (count >= minimum) return true;
                first = count = 0;
                if (onlyPrefix) return true;
            }
            block += span;
            index += span;
            if (index == bits) { index = 0; map++; }
        }
        if (count < minimum) first = count = 0;
        return true;
    }
    private static bool FindFreeRun<T>(ref T p, ref Work w, uint preferred, uint minimum, uint maximum,
        bool oneMap, out uint first, out uint count, bool evict = true) where T : struct, IAmigaWritableFileSystemPlatform
    {
        first = count = 0;
        if (preferred >= w.ReservedBlocks && preferred < w.BlockCount - 1)
        {
            if (!ScanFreeRun(ref p, ref w, preferred, minimum, maximum, oneMap, true, out first, out count)) return false;
            if (count != 0) return true;
        }
        if (!ScanFreeRun(ref p, ref w, w.ReservedBlocks, minimum, maximum, oneMap, false, out first, out count)) return false;
        if (count == 0 && evict && HasOverwriteReservations(ref p, ref w))
        {
            ReleaseOverwriteReservation(ref p, ref w);
            return ScanFreeRun(ref p, ref w, w.ReservedBlocks, minimum, maximum, oneMap, false, out first, out count);
        }
        return true;
    }
    private static bool MarkDataRun<T>(ref T p, ref Work w, uint first, uint count)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (count == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        while (count != 0)
        {
            if (!BitmapPosition(ref p, ref w, first, out var map, out var offset, out _)) return false;
            // CSUN reserves its undo sectors only after the allocated bits are
            // cleared in the supplied bitmap. Editing a new buffered image
            // earlier could reserve an undo sector inside this very data run.
            var bitmap = w.RedoStaging ? Edit(ref p, ref w, map, BitmapId, w.Bitmap) :
                Read(ref p, ref w, map, BitmapId, w.Bitmap) ? w.Bitmap : APTR.Null;
            if (bitmap.IsNull) return false;
            var withinWord = first & 31;
            var take = (w.BlockBytes - (uint)offset) * 8 - withinWord;
            if (take > count) take = count;
            var remaining = take;
            while (remaining != 0)
            {
                var span = 32u - withinWord;
                if (span > remaining) span = remaining;
                var runMask = (uint.MaxValue >> (int)withinWord) & (uint.MaxValue << (int)(32 - withinWord - span));
                var word = p.ReadUInt32(bitmap, offset);
                if ((word & runMask) != runMask) return Fail(ref w, DOS.Error.DiskNotValidated);
                ReplaceWord(ref p, bitmap, offset, word & ~runMask);
                remaining -= span; offset += 4; withinWord = 0;
            }
            // Cross-bitmap runs are protected by the still-live reservation.
            if (!PutSealed(ref p, ref w, map, bitmap)) return false;
            w.FreeDelta -= (int)take; first += take; count -= take;
        }
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
            var length = p.ReadUInt16(w.Meta, at + 12);
            if (added == 0 || added > ushort.MaxValue - length || (ulong)key + length != nextBlock)
                return Fail(ref w, DOS.Error.DiskNotValidated);
            p.WriteUInt16(w.Meta, at + 12, (ushort)(length + added));
            return Put(ref p, ref w, leaf, w.Meta);
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    // Publish a chain of ordinary CSUN records in one step. Recovery retains the
    // original format and restores the entire request, including earlier batches.
    private static bool JournalRun<T>(ref T p, ref Work w, uint target, ref uint count, APTR buffer)
        where T : struct, IAmigaWritableFileSystemPlatform
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
        if (p.ReadDataBlocks(target, count, buffer) != count || !p.WriteDataBlocks(images, count, buffer))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var bytes = p.BlockBytes;
        for (var i = 0u; i < count; i++)
        {
            var record = APTR.FromPointer(buffer.Raw + i * bytes);
            var sum = BlockSum(ref p, record, bytes);
            p.Clear(record, bytes);
            p.WriteUInt32(record, 0, UndoId); p.WriteUInt32(record, 8, entries + i);
            p.WriteUInt32(record, 12, i == 0 ? w.JournalHead : entries + i - 1);
            p.WriteUInt32(record, 16, target + i); p.WriteUInt32(record, 20, images + i);
            p.WriteUInt32(record, 24, sum); Seal(ref p, record, bytes);
        }
        if (!p.WriteDataBlocks(entries, count, buffer) || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        var head = entries + count - 1;
        p.Clear(w.Journal, bytes);
        p.WriteUInt32(w.Journal, 0, TransactionFailed); p.WriteUInt32(w.Journal, 8, w.RootContainer + 2);
        p.WriteUInt32(w.Journal, 12, head); Seal(ref p, w.Journal, bytes);
        if (!p.WriteBlock(w.RootContainer + 2, w.Journal) || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        w.JournalHead = head; w.PublishedHead = head;
        w.JournalCursor = entries + count;
        return true;
    }
}
