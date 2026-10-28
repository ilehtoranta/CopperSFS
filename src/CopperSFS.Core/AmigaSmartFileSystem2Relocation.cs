using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystem2WriteCore
{
    // Only replaces full blocks. Leading/trailing partial blocks are handled by
    // WriteBatched, and all replacements remain inside that request's transaction.
    private static bool RelocateOverwrite<T>(ref T p, ref Work w, ref uint first, uint node,
        uint start, uint offset, uint count, APTR source, APTR buffer, out bool moved)
        where T : struct, IAmigaBlockRangeFileSystemPlatform
    {
        moved = false;
        if (!ExtentRecord(ref p, ref w, start, out var length, out var next, out var previous)) return false;
        var remaining = count; var key = start; var skip = offset; var fragmented = false;
        // Check the whole range before reserving space; adjacent extent records
        // alone do not justify moving already contiguous data.
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
        if (!fragmented) return true;

        uint preferred = 0;
        if (offset == 0 && previous != 0 && (previous & 0x80000000) == 0)
        {
            if (!ExtentRecord(ref p, ref w, previous, out var beforeLength, out _, out _)) return false;
            preferred = previous + beforeLength;
        }
        // Require the full replacement run inside one bitmap. A failed search
        // has made no persistent changes, so the caller can use in-place undo.
        if (!FindFreeRun(ref p, ref w, preferred, count, count, true, out var replacement, out var found)) return false;
        if (found == 0) return true;
        if (found != count) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!MarkDataRun(ref p, ref w, replacement, count)) return false;
        p.Copy(source, buffer, count * p.Geometry.BlockBytes);
        if (!p.WriteBlocks(replacement, count, buffer) || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);

        if (offset != 0 && !SplitExtent(ref p, ref w, start, offset, out start)) return false;
        if (!ExtentRecord(ref p, ref w, start, out _, out _, out previous)) return false;
        if (previous == 0) previous = node | 0x80000000; // Legacy first-extent encoding.
        key = start; remaining = count;
        while (remaining != 0)
        {
            if (!ExtentRecord(ref p, ref w, key, out length, out next, out _)) return false;
            var take = length < remaining ? length : remaining;
            if (take < length && !SplitExtent(ref p, ref w, key, take, out next)) return false;
            if (!RemoveExtent(ref p, ref w, key, out next, false) || !ReleaseDataRun(ref p, ref w, key, take)) return false;
            remaining -= take; key = next;
        }

        var installed = replacement;
        // Continue an earlier relocated run when possible, including across
        // separate WriteBatched calls. This avoids one new extent per batch.
        if ((previous & 0x80000000) == 0)
        {
            if (!ExtentRecord(ref p, ref w, previous, out length, out _, out var before)) return false;
            if ((ulong)previous + length == replacement && (ulong)length + count <= uint.MaxValue)
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
        moved = true;
        return true;
    }

    private static bool ExtentRecord<T>(ref T p, ref Work w, uint key,
        out uint length, out uint next, out uint previous) where T : struct, IAmigaWritableFileSystemPlatform
    {
        length = next = previous = 0;
        if (!ExtentLeaf(ref p, ref w, key, out _)) return false;
        var entries = p.ReadUInt16(w.Meta, 12); var stride = p.ReadUInt8(w.Meta, 15);
        for (var i = 0; i < entries; i++)
        {
            var at = 16 + i * stride;
            if (p.ReadUInt32(w.Meta, at) != key) continue;
            length = p.ReadUInt32(w.Meta, at + 12);
            next = p.ReadUInt32(w.Meta, at + 4); previous = p.ReadUInt32(w.Meta, at + 8);
            return length != 0 && key >= p.Geometry.ReservedBlocks && (ulong)key + length <= p.Geometry.BlockCount - 1 ||
                Fail(ref w, DOS.Error.DiskNotValidated);
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool SetExtentRecord<T>(ref T p, ref Work w, uint key, uint length, uint next, uint previous)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (length == 0 || (ulong)key + length > p.Geometry.BlockCount - 1 || !ExtentLeaf(ref p, ref w, key, out var leaf))
            return FailUnlessSet(ref w);
        var entries = p.ReadUInt16(w.Meta, 12); var stride = p.ReadUInt8(w.Meta, 15);
        for (var i = 0; i < entries; i++)
        {
            var at = 16 + i * stride;
            if (p.ReadUInt32(w.Meta, at) != key) continue;
            p.WriteUInt32(w.Meta, at + 12, length);
            p.WriteUInt32(w.Meta, at + 4, next); p.WriteUInt32(w.Meta, at + 8, previous);
            return Put(ref p, ref w, leaf, w.Meta);
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
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
        var bits = (p.Geometry.BlockBytes - 12) * 8;
        while (count != 0)
        {
            if (!BitmapPosition(ref p, ref w, first, out var map, out _, out _) ||
                !Read(ref p, ref w, map, BitmapId, w.Bitmap)) return false;
            var take = bits - first % bits;
            if (take > count) take = count;
            for (var i = 0u; i < take; i++)
            {
                var bit = (first + i) % bits; var at = 12 + (int)(bit / 32) * 4; var mask = 1u << (int)(31 - bit % 32);
                var word = p.ReadUInt32(w.Bitmap, at);
                if ((word & mask) != 0) return Fail(ref w, DOS.Error.DiskNotValidated);
                p.WriteUInt32(w.Bitmap, at, word | mask);
            }
            // OriginallyFree/FreeSearchMap exclude these retired blocks until
            // commit, even though the transaction's current bitmap frees them.
            if (!Put(ref p, ref w, map, w.Bitmap)) return false;
            w.FreeDelta += (int)take; first += take; count -= take;
        }
        return true;
    }
}
