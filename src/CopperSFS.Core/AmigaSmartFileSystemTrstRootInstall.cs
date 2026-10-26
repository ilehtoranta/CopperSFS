using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    // The general allocator has written the complete replacement payload into
    // unpublished space. A non-full root leaf can install that run, advance the
    // old tail and repair all neighbours in one owned image. No tree separator
    // or split can intervene. Other layouts retain the ordinary insertion path.
    private static bool InstallTrstRootPrefix<T>(ref T p, ref Work w, ref uint first, uint node,
        uint start, uint count, uint replacement, int sourceAt, out bool installed)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        installed = false;
        var leaf = View(ref p, ref w, w.ExtentRoot, TreeId, w.Meta);
        if (leaf.IsNull) return false;
        TrstLeafLayout(ref p, leaf, out var stride, out var records);
        if (p.ReadUInt8(leaf, 14) != 1 || stride < 14 || (stride & 1) != 0 ||
            records >= (w.BlockBytes - 16) / stride) return true;
        if (!TrstRecordOffset(sourceAt, records, stride) || p.ReadUInt32(leaf, sourceAt) != start)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var length = (uint)p.ReadUInt16(leaf, sourceAt + 12);
        if (count == 0 || count >= length || (replacement >= start && replacement - start < length))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var previous = p.ReadUInt32(leaf, sourceAt + 8);
        var after = p.ReadUInt32(leaf, sourceAt + 4);
        var hasPrevious = previous != 0 && (previous & 0x80000000) == 0;
        var beforeAt = 0; var afterAt = 0;
        var end = 16 + (int)(records * stride); var insertAt = end;
        for (var at = 16; at < end; at += (int)stride)
        {
            var key = p.ReadUInt32(leaf, at);
            if (key == replacement) return Fail(ref w, DOS.Error.DiskNotValidated);
            if (insertAt == end && key > replacement) insertAt = at;
            if (hasPrevious && key == previous) beforeAt = at;
            if (after != 0 && key == after) afterAt = at;
        }
        if ((hasPrevious && (beforeAt == 0 || beforeAt == sourceAt ||
                p.ReadUInt32(leaf, beforeAt + 4) != start ||
                !TrstRootExtentValid(ref p, ref w, leaf, beforeAt))) ||
            (after != 0 && (afterAt == 0 || afterAt == sourceAt || afterAt == beforeAt ||
                p.ReadUInt32(leaf, afterAt + 8) != start ||
                !TrstRootExtentValid(ref p, ref w, leaf, afterAt))))
            return Fail(ref w, DOS.Error.DiskNotValidated);

        // Edit copies a borrowed/cache view into the bounded candidate arena.
        // All retained positions are scalars, and no extent mutation occurred
        // since their validation. Insertion shifts positions at/after insertAt.
        leaf = Edit(ref p, ref w, w.ExtentRoot, TreeId, w.Meta);
        if (leaf.IsNull) return false;
        if (previous == 0) previous = node | 0x80000000;
        InsertBnode(ref p, leaf, (int)records, (int)stride, replacement, 0, previous, true);
        if (sourceAt >= insertAt) sourceAt += (int)stride;
        if (beforeAt >= insertAt) beforeAt += (int)stride;
        if (afterAt >= insertAt) afterAt += (int)stride;
        var tail = start + count; var tailLength = length - count;
        p.WriteUInt32(leaf, insertAt + 4, tail); p.WriteUInt16(leaf, insertAt + 12, (ushort)count);
        p.WriteUInt32(leaf, sourceAt, tail); p.WriteUInt32(leaf, sourceAt + 8, replacement);
        p.WriteUInt16(leaf, sourceAt + 12, (ushort)tailLength);
        if (beforeAt != 0) p.WriteUInt32(leaf, beforeAt + 4, replacement);
        if (afterAt != 0) p.WriteUInt32(leaf, afterAt + 8, tail);
        if (!Put(ref p, ref w, w.ExtentRoot, leaf) || !ReleaseDataRun(ref p, ref w, start, count)) return false;
        if (first == start) first = replacement;
        if (w.TrstContinuation.IsNotNull && tailLength > 1 && count < ushort.MaxValue)
        {
            // ReleaseDataRun cannot move owned extent images. Preserve the
            // usual continuation layout/link validation without rereading it.
            var hint = w.TrstContinuation;
            p.WriteUInt32(hint, 0, node); p.WriteUInt32(hint, 4, 0);
            p.WriteUInt32(hint, 8, tail); p.WriteUInt32(hint, 12, tailLength);
            p.WriteUInt32(hint, 16, replacement); p.WriteUInt32(hint, 20, count);
            p.WriteUInt32(hint, 24, after); p.WriteUInt32(hint, 28, w.ExtentRoot);
            p.WriteUInt32(hint, 32, (uint)sourceAt); p.WriteUInt32(hint, 36, (uint)insertAt);
            p.WriteUInt32(hint, 40, (uint)afterAt); p.WriteUInt32(hint, 44, p.RedoOverlay.Raw);
            if (!TrstContinuationLeaf(ref p, w.BlockBytes, hint, leaf, APTR.Null))
                return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        installed = true; return true;
    }

    private static bool TrstRootExtentValid<T>(ref T p, ref Work w, APTR leaf, int at)
        where T : struct, IAmigaGuestMemory
    {
        var key = p.ReadUInt32(leaf, at); var length = (uint)p.ReadUInt16(leaf, at + 12);
        return length != 0 && key >= w.ReservedBlocks && key < w.BlockCount - 1 && length <= w.BlockCount - 1 - key;
    }
}
