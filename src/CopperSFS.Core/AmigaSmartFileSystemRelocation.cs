using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    // A root leaf indexes every data extent. Its occupied ranges can rule
    // out impossible placement gaps before loading their allocation bitmaps.
    // Candidate gaps still use the ordinary verified, reservation-aware bitmap
    // scanner. This is optional first-Write look-ahead, not an allocation proof.
    private static bool ScanTrstRootPlacement<T>(ref T p, ref Work w, uint start,
        uint minimum, uint maximum, uint searchEnd, out uint first, out uint count)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        first = count = 0;
        var leaf = View(ref p, ref w, w.ExtentRoot, TreeId, w.Meta);
        if (leaf.IsNull) return false;
        TrstLeafLayout(ref p, leaf, out var stride, out var records);
        if (p.ReadUInt8(leaf, 14) != 1 || stride < 14 || (stride & 1) != 0 ||
            records > (w.BlockBytes - 16) / stride)
            return ScanFreeRunBounded(ref p, ref w, start, minimum, maximum,
                true, false, out first, out count, false, searchEnd);
        var high = w.BlockCount - 1;
        var previousEnd = w.ReservedBlocks;
        // Validate the complete range index before using it to skip anything.
        // No bitmap access or metadata mutation occurs during this pass.
        for (var i = 0u; i < records; i++)
        {
            var at = 16 + (int)(i * stride);
            var key = p.ReadUInt32(leaf, at);
            var length = (uint)p.ReadUInt16(leaf, at + 12);
            if (key < previousEnd || key >= high || length == 0 || length > high - key)
                return ScanFreeRunBounded(ref p, ref w, start, minimum, maximum,
                    true, false, out first, out count, false, searchEnd);
            previousEnd = key + length;
        }
        if (searchEnd < high) high = searchEnd;
        var bits = (w.BlockBytes - 12) * 8;
        var cursor = start;
        for (var i = 0u; i <= records && cursor < high; i++)
        {
            var stop = high; var next = high;
            if (i < records)
            {
                var at = 16 + (int)(i * stride);
                var key = p.ReadUInt32(leaf, at);
                next = key + p.ReadUInt16(leaf, at + 12);
                if (key < stop) stop = key;
            }
            if (stop > cursor && stop - cursor >= minimum)
            {
                var candidate = cursor;
                var remainder = bits - cursor % bits;
                if (remainder < minimum)
                {
                    if (remainder > high - cursor) return true;
                    candidate += remainder;
                }
                if (candidate < stop && stop - candidate >= minimum)
                {
                    if (!ScanFreeRunBounded(ref p, ref w, candidate, minimum, maximum,
                        true, false, out first, out count, false, stop)) return false;
                    if (count != 0) return true;
                    // A bitmap lookup expires the borrowed leaf. Reacquire it
                    // before reading another record; only scalars cross I/O.
                    leaf = View(ref p, ref w, w.ExtentRoot, TreeId, w.Meta);
                    if (leaf.IsNull) return false;
                }
            }
            if (next > cursor) cursor = next;
        }
        return true;
    }

    // Only replaces full blocks. Leading/trailing partial blocks are handled by
    // WriteBatched, and all replacements remain inside that request's transaction.
    private static bool RelocateOverwrite<T>(ref T p, ref Work w, ref uint first, uint node,
        uint start, uint offset, uint count, APTR source, APTR buffer, uint replacement, out bool moved,
        uint sourceLeaf = 0, int sourceAt = 0)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        moved = false;
        uint length, next, previous;
        if (sourceLeaf == 0)
        {
            if (!LocateExtentRecord(ref p, ref w, start, out length, out next, out previous,
                out sourceLeaf, out sourceAt)) return false;
        }
        else if (!ReadLocatedExtentRecord(ref p, ref w, start, sourceLeaf, sourceAt,
            out length, out next, out previous)) return false;
        var sourceLength = length; var sourceNext = next;
        var advanceTail = false;
        if (w.RedoStaging && offset == 0 && count < sourceLength)
        {
            advanceTail = sourceLeaf == w.ExtentRoot;
            if (!advanceTail)
            {
                // Internal keys are lower bounds and can outlive deleted
                // records. Such a separator can lie inside a data extent.
                // An in-leaf key edit is safe only while lookup still selects
                // this leaf. Check the whole tail so retained hints are safe
                // for later Writes too; otherwise use split/remove/insert.
                if (!ExtentLeaf(ref p, ref w, start + sourceLength - 1, out var endLeaf)) return false;
                advanceTail = endLeaf == sourceLeaf;
            }
        }
        var remaining = count; var key = start; var skip = offset; var fragmented = false;
        // Validate the whole range before reserving space. For smaller writes,
        // adjacent extent records alone do not justify moving contiguous data.
        while (remaining != 0)
        {
            if (skip >= length) return Fail(ref w, DOS.Error.DiskNotValidated);
            var take = length - skip;
            if (remaining <= take) break;
            remaining -= take;
            if (next == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
            if ((ulong)key + length != next) fragmented = true;
            key = next; skip = 0;
            if (!ExtentRecord(ref p, ref w, key, out length, out next, out _)) return false;
        }
        if (!w.RedoStaging && replacement == 0 && !fragmented && count * w.BlockBytes < 65536) return true;

        uint preferred = 0, beforeLength = 0, beforeNext = 0, beforePrevious = 0, beforeLeaf = 0;
        var beforeAt = 0;
        if (offset == 0 && previous != 0 && (previous & 0x80000000) == 0)
        {
            if (!LocateExtentRecord(ref p, ref w, previous, out beforeLength, out beforeNext,
                out beforePrevious, out beforeLeaf, out beforeAt)) return false;
            preferred = previous + beforeLength;
        }
        // Without a reservation, retain the bounded first-fit fallback.
        if (replacement == 0)
        {
            var found = 0u;
            var placedLong = false;
            if (preferred == 0 && w.RedoStaging && w.TrstContinuation.IsNotNull &&
                w.RedoCheckpoint.IsNull && offset == 0 && sourceLeaf == w.ExtentRoot)
            {
                // A checked root index can reject short occupied gaps without
                // reading their bitmaps. Seek the longer placement directly;
                // otherwise first-fit would read a hole we immediately discard.
                // This is only placement advice: MarkDataRun still allocates
                // exactly this Write, and later Writes retain their own proofs.
                var bits = (w.BlockBytes - 12) * 8;
                var high = w.BlockCount - 1;
                var budget = bits * 8;
                var end = high - w.ReservedBlocks > budget ? w.ReservedBlocks + budget : high;
                if (sourceLength > count && sourceLength <= bits && end - w.ReservedBlocks >= sourceLength)
                {
                    var mask = (count & (count - 1)) == 0 && (bits & (count - 1)) == 0 ? count - 1 : 0;
                    var savedError = w.Error;
                    if (ScanTrstRootPlacement(ref p, ref w, w.ReservedBlocks, sourceLength,
                        sourceLength, end, out var longer, out var available) && available == sourceLength &&
                        (longer & mask) == 0)
                    {
                        replacement = longer; found = count; placedLong = true;
                    }
                    // An unaligned sufficient first fit must stay unaligned.
                    // Let the original search distinguish it from a later hole.
                    // Failure of optional look-ahead never decides Write success.
                    w.Error = savedError;
                }
            }
            if (w.RedoStaging && w.RedoCheckpoint.IsNotNull &&
                !FindTrstFreeRun(ref p, ref w, preferred, count, out replacement, out found)) return false;
            if (found == 0 && !FindFreeRun(ref p, ref w, preferred, count, count, true, out replacement, out found)) return false;
            if (found == 0) return true;
            if (found != count) return Fail(ref w, DOS.Error.DiskNotValidated);
            // The first buffered relocation can seed many subsequent Writes.
            // Prefer room for the rest of this extent when nearby free space
            // offers it, avoiding a new allocation/extent transition at every
            // short hole. This is placement advice, not a future reservation:
            // only count blocks below are allocated, and all existing proofs
            // still validate later Writes. Keep the original fit as fallback.
            if (!placedLong && w.RedoStaging && w.TrstContinuation.IsNotNull && w.RedoCheckpoint.IsNull && offset == 0 && preferred != replacement)
            {
                var bits = (w.BlockBytes - 12) * 8;
                var wanted = sourceLength;
                if (wanted > bits * 2) wanted = bits * 2;
                var high = w.BlockCount - 1;
                var budget = bits * 8;
                var end = high - replacement > budget ? replacement + budget : high;
                if (wanted > count && end - replacement >= wanted)
                {
                    // If this packet grain divides a bitmap, aligning the
                    // destination keeps later packets from straddling maps.
                    // A whole-write boundary can then use the checked bitmap
                    // continuation instead of general extent reconstruction.
                    var mask = (count & (count - 1)) == 0 && (bits & (count - 1)) == 0 ? count - 1 : 0;
                    var savedError = w.Error;
                    // When the whole source extent can fit in one bitmap,
                    // prefer a hole that keeps its replacement there. This
                    // avoids another clone/admission at a destination-map
                    // boundary. Search is bounded to eight bitmap spans.
                    var oneMap = wanted <= bits;
                    uint longer, available;
                    var foundLonger = oneMap && sourceLeaf == w.ExtentRoot
                        ? ScanTrstRootPlacement(ref p, ref w, replacement, wanted, wanted + mask,
                            end, out longer, out available)
                        : ScanFreeRunBounded(ref p, ref w, replacement, wanted, wanted + mask,
                            oneMap, false, out longer, out available, false, end);
                    if (foundLonger && available == 0 && oneMap)
                    {
                        // A longer cross-map hole is still preferable to a
                        // short first fit. Bound this fallback to four spans.
                        budget = bits * 4;
                        end = high - replacement > budget ? replacement + budget : high;
                        foundLonger = ScanFreeRunBounded(ref p, ref w, replacement, wanted, wanted + mask,
                            false, false, out longer, out available, false, end);
                    }
                    // Optional placement must not fail a valid first fit when
                    // unrelated look-ahead metadata cannot be read.
                    w.Error = savedError;
                    var padding = unchecked(0u - longer) & mask;
                    // Keep a sufficient first fit exactly where it is. Alignment
                    // only applies when moving to a different, longer hole;
                    // otherwise it creates needless fragments before the run.
                    if (foundLonger && longer != replacement && available >= wanted + padding)
                        replacement = longer + padding;
                }
            }
        }
        if (!MarkDataRun(ref p, ref w, replacement, count)) return false;
        if (w.RedoStaging)
        {
            if (!WriteNewDataRun(ref p, ref w, replacement, count, source)) return false;
        }
        else
        {
            p.Copy(source, buffer, count * w.BlockBytes);
            if (!p.WriteDataBlocks(replacement, count, buffer) || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        }

        // Successive full-block writes can extend an earlier relocated run.
        // Retain the unmodified tail's record, moving its key forward inside
        // its own physical extent, without crossing a tree separator.
        if (advanceTail && sourceLeaf == w.ExtentRoot && preferred != replacement)
        {
            if (!InstallTrstRootPrefix(ref p, ref w, ref first, node, start, count,
                replacement, sourceAt, out var installedRoot)) return false;
            if (installedRoot) { moved = true; return true; }
        }
        if (advanceTail && preferred == replacement && beforeLeaf != 0 &&
            beforeNext == start && beforeLength + count <= ushort.MaxValue)
        {
            // Bitmap allocation and unpublished data I/O cannot insert, remove
            // or move extent records. Reuse the locations checked above, but
            // reacquire each image through Edit before changing it. A shared
            // leaf needs one checksum validation and one store for all edits.
            uint afterLeaf = 0; var afterAt = 0;
            if (sourceNext != 0 && !LocateExtentRecord(ref p, ref w, sourceNext,
                out _, out _, out _, out afterLeaf, out afterAt)) return false;
            var tail = start + count;
            var record = Edit(ref p, ref w, sourceLeaf, TreeId, w.Meta);
            if (record.IsNull) return false;
            ReplaceWord(ref p, record, sourceAt, tail);
            ReplaceHalf(ref p, record, sourceAt + 12, (ushort)(sourceLength - count));
            if (beforeLeaf == sourceLeaf)
            {
                ReplaceHalf(ref p, record, beforeAt + 12, (ushort)(beforeLength + count));
                ReplaceWord(ref p, record, beforeAt + 4, tail);
            }
            if (afterLeaf == sourceLeaf) ReplaceWord(ref p, record, afterAt + 8, tail);
            if (!PutSealed(ref p, ref w, sourceLeaf, record)) return false;
            if (beforeLeaf != sourceLeaf)
            {
                record = Edit(ref p, ref w, beforeLeaf, TreeId, w.Meta);
                if (record.IsNull) return false;
                ReplaceHalf(ref p, record, beforeAt + 12, (ushort)(beforeLength + count));
                ReplaceWord(ref p, record, beforeAt + 4, tail);
                if (afterLeaf == beforeLeaf) ReplaceWord(ref p, record, afterAt + 8, tail);
                if (!PutSealed(ref p, ref w, beforeLeaf, record)) return false;
            }
            if (afterLeaf != 0 && afterLeaf != sourceLeaf && afterLeaf != beforeLeaf)
            {
                record = Edit(ref p, ref w, afterLeaf, TreeId, w.Meta);
                if (record.IsNull) return false;
                ReplaceWord(ref p, record, afterAt + 8, tail);
                if (!PutSealed(ref p, ref w, afterLeaf, record)) return false;
            }
            if (!ReleaseDataRun(ref p, ref w, start, count)) return false;
            if (w.TrstContinuation.IsNotNull && beforeLeaf == sourceLeaf &&
                (afterLeaf == 0 || afterLeaf == sourceLeaf))
            {
                var hint = w.TrstContinuation;
                p.WriteUInt32(hint, 0, node); p.WriteUInt32(hint, 4, 0);
                p.WriteUInt32(hint, 8, tail); p.WriteUInt32(hint, 12, sourceLength - count);
                p.WriteUInt32(hint, 16, previous); p.WriteUInt32(hint, 20, beforeLength + count);
                p.WriteUInt32(hint, 24, sourceNext); p.WriteUInt32(hint, 28, sourceLeaf);
                p.WriteUInt32(hint, 32, (uint)sourceAt); p.WriteUInt32(hint, 36, (uint)beforeAt);
                p.WriteUInt32(hint, 40, (uint)afterAt); p.WriteUInt32(hint, 44, p.RedoOverlay.Raw);
            }
            moved = true; return true;
        }

        if (advanceTail)
        {
            // Replacing a prefix needs a shorter source tail, not a temporary
            // prefix record which is immediately deleted. As above, advancing
            // inside this checked leaf interval preserves key order and parent
            // routing. No extent mutation has intervened since
            // sourceLeaf/sourceAt were checked; reacquire its owned image.
            uint afterLeaf = 0; var afterAt = 0;
            if (sourceNext != 0 && !LocateExtentRecord(ref p, ref w, sourceNext,
                out _, out _, out _, out afterLeaf, out afterAt)) return false;
            next = start + count;
            var record = Edit(ref p, ref w, sourceLeaf, TreeId, w.Meta);
            if (record.IsNull) return false;
            ReplaceWord(ref p, record, sourceAt, next);
            ReplaceHalf(ref p, record, sourceAt + 12, (ushort)(sourceLength - count));
            if (afterLeaf == sourceLeaf) ReplaceWord(ref p, record, afterAt + 8, next);
            if (!PutSealed(ref p, ref w, sourceLeaf, record)) return false;
            if (afterLeaf != 0 && afterLeaf != sourceLeaf)
            {
                record = Edit(ref p, ref w, afterLeaf, TreeId, w.Meta);
                if (record.IsNull) return false;
                ReplaceWord(ref p, record, afterAt + 8, next);
                if (!PutSealed(ref p, ref w, afterLeaf, record)) return false;
            }
            if (!ReleaseDataRun(ref p, ref w, start, count)) return false;
            // The common installation below updates predecessor.next,
            // tail.previous and the file's first extent. It locates records
            // again after insertion, which can move them or split the tree.
        }
        else
        {
            if (offset != 0 && !SplitExtent(ref p, ref w, start, offset, out start)) return false;
            if (!ExtentRecord(ref p, ref w, start, out _, out _, out previous)) return false;
            key = start; remaining = count;
            while (remaining != 0)
            {
                if (!ExtentRecord(ref p, ref w, key, out length, out next, out _)) return false;
                var take = length < remaining ? length : remaining;
                if (take < length && !SplitExtent(ref p, ref w, key, take, out next)) return false;
                if (!RemoveExtent(ref p, ref w, key, out next, false) || !ReleaseDataRun(ref p, ref w, key, take)) return false;
                remaining -= take; key = next;
            }
        }
        if (previous == 0) previous = node | 0x80000000; // Legacy first-extent encoding.

        var installed = replacement;
        // Continue an earlier relocated run when possible, including across
        // separate WriteBatched calls. This avoids one new extent per batch.
        if ((previous & 0x80000000) == 0)
        {
            if (!ExtentRecord(ref p, ref w, previous, out length, out _, out var before)) return false;
            if ((ulong)previous + length == replacement && length + count <= ushort.MaxValue)
            {
                if (!SetExtentRecord(ref p, ref w, previous, length + count, next, before)) return false;
                installed = previous;
            }
        }
        if (installed == replacement)
        {
            if (!InsertBnode(ref p, ref w, w.ExtentRoot, replacement, 0, previous, 0, out _, out _) ||
                !SetExtentRecord(ref p, ref w, replacement, count, next, previous)) return false;
            if ((previous & 0x80000000) == 0)
            {
                if (!ExtentRecord(ref p, ref w, previous, out length, out _, out var before) ||
                    !SetExtentRecord(ref p, ref w, previous, length, replacement, before)) return false;
            }
        }
        if (next != 0)
        {
            if (!ExtentRecord(ref p, ref w, next, out length, out var after, out _) ||
                !SetExtentRecord(ref p, ref w, next, length, after, installed)) return false;
        }
        if (first == start) first = installed;
        if (w.RedoStaging && w.TrstContinuation.IsNotNull && next != 0 &&
            !RememberTrstRelocation(ref p, ref w, node, installed, next)) return false;
        moved = true;
        return true;
    }

    // Insertion/removal can move records, so acquire their final locations only
    // after the complete relocation. Retain scalar fields, never this borrowed
    // image. A shared leaf is enough to describe the next consecutive Write;
    // the ordinary continuation entry still validates it and proves durable
    // free space before writing. Other layouts keep the general fallback.
    private static bool RememberTrstRelocation<T>(ref T p, ref Work w, uint node, uint before, uint tail)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var leaf = AmigaSmartFileSystemCore.ExtentRecordView(ref p, w.ExtentRoot, before,
            w.Meta, w.BlockCount, w.BlockBytes, out var block, out var beforeAt);
        if (leaf.IsNull) return FailUnlessSet(ref w);
        TrstLeafLayout(ref p, leaf, out var stride, out var records);
        if (p.ReadUInt8(leaf, 14) != 1 || stride < 14 || (stride & 1) != 0 ||
            records > (w.BlockBytes - 16) / stride) return Fail(ref w, DOS.Error.DiskNotValidated);
        var beforeLength = (uint)p.ReadUInt16(leaf, beforeAt + 12);
        if (beforeLength == 0 || beforeLength == ushort.MaxValue || p.ReadUInt32(leaf, beforeAt + 4) != tail) return true;
        var end = 16 + (int)(records * stride); var tailAt = 0;
        for (var at = 16; at < end; at += (int)stride)
            if (p.ReadUInt32(leaf, at) == tail) { tailAt = at; break; }
        if (tailAt == 0) return true;
        var tailLength = (uint)p.ReadUInt16(leaf, tailAt + 12);
        if (tailLength <= 1) return true;
        var after = p.ReadUInt32(leaf, tailAt + 4); var afterAt = 0;
        if (after != 0)
        {
            for (var at = 16; at < end; at += (int)stride)
                if (p.ReadUInt32(leaf, at) == after) { afterAt = at; break; }
            if (afterAt == 0) return true;
        }
        if (block != w.ExtentRoot)
        {
            if (!ExtentLeaf(ref p, ref w, tail + tailLength - 1, out var endLeaf)) return false;
            if (endLeaf != block) return true;
            // The search expires the borrowed image, but not its scalar
            // locations. Reacquire it before validating the retained hint.
            leaf = View(ref p, ref w, block, TreeId, w.Meta);
            if (leaf.IsNull) return false;
        }
        var hint = w.TrstContinuation;
        p.WriteUInt32(hint, 0, node); p.WriteUInt32(hint, 4, 0);
        p.WriteUInt32(hint, 8, tail); p.WriteUInt32(hint, 12, tailLength);
        p.WriteUInt32(hint, 16, before); p.WriteUInt32(hint, 20, beforeLength);
        p.WriteUInt32(hint, 24, after); p.WriteUInt32(hint, 28, block);
        p.WriteUInt32(hint, 32, (uint)tailAt); p.WriteUInt32(hint, 36, (uint)beforeAt);
        p.WriteUInt32(hint, 40, (uint)afterAt); p.WriteUInt32(hint, 44, p.RedoOverlay.Raw);
        return TrstContinuationLeaf(ref p, w.BlockBytes, hint, leaf, APTR.Null) || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    // FileBlock has already checked this location, and no extent mutation has
    // intervened. Reacquire the leaf through the normal checked view; only the
    // tree search is redundant. Keep widened fields in the out-uint contract.
    private static bool ReadLocatedExtentRecord<T>(ref T p, ref Work w, uint key, uint leaf, int at,
        out uint length, out uint next, out uint previous)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        length = next = previous = 0;
        var image = View(ref p, ref w, leaf, TreeId, w.Meta);
        if (image.IsNull) return false;
        length = p.ReadUInt16(image, at + 12);
        next = p.ReadUInt32(image, at + 4); previous = p.ReadUInt32(image, at + 8);
        return length != 0 && key >= w.ReservedBlocks && (ulong)key + length <= w.BlockCount - 1 ||
            Fail(ref w, DOS.Error.DiskNotValidated);
    }

    // Locations may be retained only until an extent insertion/removal. The
    // scalar record fields survive intervening block I/O; the image does not.
    private static bool LocateExtentRecord<T>(ref T p, ref Work w, uint key,
        out uint length, out uint next, out uint previous, out uint leaf, out int at)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        length = next = previous = 0;
        var image = AmigaSmartFileSystemCore.ExtentRecordView(ref p, w.ExtentRoot, key,
            w.Meta, w.BlockCount, w.BlockBytes, out leaf, out at);
        if (image.IsNull) return FailUnlessSet(ref w);
        length = p.ReadUInt16(image, at + 12);
        next = p.ReadUInt32(image, at + 4); previous = p.ReadUInt32(image, at + 8);
        return length != 0 && key >= w.ReservedBlocks && (ulong)key + length <= w.BlockCount - 1 ||
            Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool ExtentRecord<T>(ref T p, ref Work w, uint key,
        out uint length, out uint next, out uint previous) where T : struct, IAmigaWritableFileSystemPlatform
    {
        length = next = previous = 0;
        var image = AmigaSmartFileSystemCore.ExtentRecordView(ref p, w.ExtentRoot, key,
            w.Meta, w.BlockCount, w.BlockBytes, out _, out var at);
        if (image.IsNull) return FailUnlessSet(ref w);
        length = p.ReadUInt16(image, at + 12);
        next = p.ReadUInt32(image, at + 4); previous = p.ReadUInt32(image, at + 8);
        return length != 0 && key >= w.ReservedBlocks && (ulong)key + length <= w.BlockCount - 1 ||
            Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool SetExtentRecord<T>(ref T p, ref Work w, uint key, uint length, uint next, uint previous)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (length == 0 || length > ushort.MaxValue) return FailUnlessSet(ref w);
        var image = AmigaSmartFileSystemCore.ExtentRecordView(ref p, w.ExtentRoot, key,
            w.Meta, w.BlockCount, w.BlockBytes, out var leaf, out var at);
        if (image.IsNull) return FailUnlessSet(ref w);
        image = Edit(ref p, ref w, leaf, TreeId, w.Meta);
        if (image.IsNull) return false;
        ReplaceHalf(ref p, image, at + 12, (ushort)length);
        ReplaceWord(ref p, image, at + 4, next); ReplaceWord(ref p, image, at + 8, previous);
        return PutSealed(ref p, ref w, leaf, image);
    }

    private static bool SplitExtent<T>(ref T p, ref Work w, uint key, uint leftLength, out uint right)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        right = 0;
        if (!ExtentRecord(ref p, ref w, key, out var length, out var next, out var previous)) return false;
        if (leftLength == 0 || leftLength >= length) return Fail(ref w, DOS.Error.DiskNotValidated);
        right = key + leftLength;
        if (!SetExtentRecord(ref p, ref w, key, leftLength, right, previous) ||
            !InsertBnode(ref p, ref w, w.ExtentRoot, right, 0, key, 0, out _, out _) ||
            !SetExtentRecord(ref p, ref w, right, length - leftLength, next, key)) return false;
        if (next == 0) return true;
        return ExtentRecord(ref p, ref w, next, out length, out var after, out _) &&
            SetExtentRecord(ref p, ref w, next, length, after, right);
    }

    private static bool ReleaseDataRun<T>(ref T p, ref Work w, uint first, uint count)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        while (count != 0)
        {
            if (!BitmapPosition(ref p, ref w, first, out var map, out var offset, out _)) return false;
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
                if ((word & runMask) != 0) return Fail(ref w, DOS.Error.DiskNotValidated);
                ReplaceWord(ref p, bitmap, offset, word | runMask);
                remaining -= span; offset += 4; withinWord = 0;
            }
            // OriginallyFree/FreeSearchMap exclude these retired blocks until
            // commit, even though the transaction's current bitmap frees them.
            if (!PutSealed(ref p, ref w, map, bitmap)) return false;
            w.FreeDelta += (int)take; first += take; count -= take;
        }
        return true;
    }
}
