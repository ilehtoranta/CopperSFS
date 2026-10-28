using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    // A consecutive aligned overwrite can extend an already relocated extent
    // without cloning its accepted metadata. All fallible work precedes data
    // I/O, which targets space free in both the durable and accepted views.
    // Afterwards only bounded, no-fail edits to already-owned images remain.
    private static bool ContinueTrstWrite<T>(ref T p, ref Work w, ref Request r,
        APTR group, APTR active, out bool handled) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var pending = new TrstContinuationWork { Meta = w.Meta, Reservations = w.Reservations,
            RootContainer = w.RootContainer, BlockBytes = w.BlockBytes,
            BlockCount = w.BlockCount, ReservedBlocks = w.ReservedBlocks };
        var success = ContinueTrstWriteReady(ref p, ref pending, ref r, group, active, out handled, out var needsProof);
        if (success && needsProof)
        {
            // The shared checks above established the hint's bounds before
            // proof construction can inspect durable allocation metadata.
            var hint = APTR.FromPointer(group.Raw + TrstGroupContinuation);
            var count = w.BlockBytes == 512 ? r.Length >> 9 : r.Length / w.BlockBytes;
            if (!BuildTrstProof(ref p, ref w, group, active, hint, p.ReadUInt32(hint, 8),
                p.ReadUInt32(hint, 16) + p.ReadUInt32(hint, 20), count, out var ready)) return false;
            if (ready) success = ContinueTrstWriteReady(ref p, ref pending, ref r, group, active, out handled, out _);
            else success = ContinueTrstBoundary(ref p, ref w, ref pending, ref r, group, active, count, out handled);
        }
        if (!success && pending.Error != DOS.Error.None) w.Error = pending.Error;
        if (handled) w.FreeDelta = w.RecycledBlocksDelta = w.RecycledFilesDelta = 0;
        return success;
    }

    private static bool ContinueTrstWriteReady<T>(ref T p, ref TrstContinuationWork w, ref Request r,
        APTR group, APTR active, out bool handled, out bool needsProof, bool deferUpdates = false) where T : struct, IAmigaWritableFileSystemPlatform
    {
        handled = false; needsProof = false;
        var hint = APTR.FromPointer(group.Raw + TrstGroupContinuation);
        if (r.Node == 0 || p.ReadUInt32(hint) != r.Node || p.ReadUInt32(hint, 4) != r.Position ||
            p.ReadUInt32(hint, 44) != active.Raw || r.Length == 0 || r.Length > 65536 ||
            (w.BlockBytes == 512 ? ((r.Position | r.Length) & 511) != 0 :
                r.Position % w.BlockBytes != 0 || r.Length % w.BlockBytes != 0) || (r.Buffer.Raw & 3) != 0 ||
            p.ReadUInt32(active, 8) != (w.BlockBytes | 0x80000000)) return true;
        if (!p.IsMapped(r.Buffer, r.Length)) return FailContinuation(ref w, DOS.Error.BadNumber);
        if (p.ReadUInt32(group, TrstGroupObjectHint) != r.Node) return true;
        var objectBlock = p.ReadUInt32(group, TrstGroupObjectHint + 4);
        var objectAt = (int)p.ReadUInt32(group, TrstGroupObjectHint + 8);
        if (objectBlock >= w.BlockCount || objectAt < 24 || (uint)objectAt + 27 > w.BlockBytes)
            return FailContinuation(ref w, DOS.Error.DiskNotValidated);
        var obj = p.ReadMetadataView(objectBlock, ObjectId, w.Meta);
        if (obj.IsNull || p.ReadUInt32(obj, objectAt + 4) != r.Node) return FailContinuation(ref w, DOS.Error.DiskNotValidated);
        if ((p.ReadUInt8(obj, objectAt + 24) & (128 | 64)) != 0) return FailContinuation(ref w, DOS.Error.ObjectWrongType);
        if (((p.ReadUInt32(obj, objectAt + 8) ^ 15) & 4) != 0) return FailContinuation(ref w, DOS.Error.WriteProtected);
        var size = p.ReadUInt32(obj, objectAt + 16);
        if (r.Position > size || r.Length > size - r.Position || p.ReadUInt32(obj, 12) == RecycledNode) return true;

        var tail = p.ReadUInt32(hint, 8); var tailLength = p.ReadUInt32(hint, 12);
        var before = p.ReadUInt32(hint, 16); var beforeLength = p.ReadUInt32(hint, 20);
        var count = w.BlockBytes == 512 ? r.Length >> 9 : r.Length / w.BlockBytes;
        if (count >= tailLength || beforeLength > ushort.MaxValue - count) return true;
        if (tail < w.ReservedBlocks || tail >= w.BlockCount - 1 || tailLength > w.BlockCount - 1 - tail ||
            before < w.ReservedBlocks || before >= w.BlockCount - 1 || beforeLength > w.BlockCount - 1 - before ||
            count > w.BlockCount - 1 - before - beforeLength) return FailContinuation(ref w, DOS.Error.DiskNotValidated);
        var destination = before + beforeLength;
        var marker = w.RootContainer + 2;
        if ((marker >= destination && marker - destination < count) || (w.Reservations.IsNotNull && p.IsMapped(w.Reservations, OverwriteReservationBytes) && p.ReadUInt32(w.Reservations) != 0)) return true;
        var proof = APTR.FromPointer(group.Raw + TrstGroupProof);
        if (p.ReadUInt32(proof) != active.Raw)
        {
            needsProof = true; return true;
        }
        else if (!TrstProofSealed(ref p, proof)) return FailContinuation(ref w, DOS.Error.DiskNotValidated);
        if (p.ReadUInt32(proof, 4) != p.ReadUInt32(group, 60) || p.ReadUInt32(proof, 8) != p.ReadUInt32(active)) return true;
        if (tail < p.ReadUInt32(proof, 12) || destination < p.ReadUInt32(proof, 16)) return FailContinuation(ref w, DOS.Error.DiskNotValidated);
        var progress = tail - p.ReadUInt32(proof, 12);
        if (progress != destination - p.ReadUInt32(proof, 16)) return FailContinuation(ref w, DOS.Error.DiskNotValidated);
        var limit = p.ReadUInt32(proof, 20);
        if (progress > limit) return true;
        if (count > limit - progress)
        {
            // An exhausted bitmap window need not discard the checked extent
            // layout. The ordinary path can stage a new map in the spare arena.
            // A shorter durable-free window within the same maps still falls
            // back to allocation; it cannot be extended by rebuilding a proof.
            var bits = (w.BlockBytes - 12) * 8;
            needsProof = p.ReadUInt32(proof, 64) + progress >= bits ||
                p.ReadUInt32(proof, 68) + progress >= bits;
            return true;
        }
        var leaf = AmigaSmartFileSystemRedoArena.OwnedMetadataAt(ref p, active, p.ReadUInt32(proof, 24), p.ReadUInt32(hint, 28), TreeId);
        if (leaf.IsNull || !TrstContinuationLeaf(ref p, w.BlockBytes, hint, leaf, proof)) return FailContinuation(ref w, DOS.Error.DiskNotValidated);
        var oldBitmap = AmigaSmartFileSystemRedoArena.OwnedMetadataAt(ref p, active, p.ReadUInt32(proof, 28), p.ReadUInt32(proof, 56), BitmapId);
        var newBitmap = p.ReadUInt32(proof, 56) == p.ReadUInt32(proof, 60) ? oldBitmap :
            AmigaSmartFileSystemRedoArena.OwnedMetadataAt(ref p, active, p.ReadUInt32(proof, 32), p.ReadUInt32(proof, 60), BitmapId);
        if (oldBitmap.IsNull || newBitmap.IsNull) return FailContinuation(ref w, DOS.Error.DiskNotValidated);
        var oldAt = 12 + (int)((p.ReadUInt32(proof, 64) + progress) >> 5) * 4;
        var newAt = 12 + (int)((p.ReadUInt32(proof, 68) + progress) >> 5) * 4;
        if (!TrstBitmapRange(ref p, oldBitmap, oldAt, tail & 31, count, false)) return FailContinuation(ref w, DOS.Error.DiskNotValidated);
        if (!TrstBitmapRange(ref p, newBitmap, newAt, destination & 31, count, true)) return true;
        var runLimit = 0u;
        if (deferUpdates && group.Raw > 1 && p.ReadUInt32(active, 12) == 0)
        {
            runLimit = TrstFreeSpan(ref p, newBitmap, p.ReadUInt32(proof, 68) + progress, limit - progress);
            var occupied = TrstFreeSpan(ref p, oldBitmap, p.ReadUInt32(proof, 64) + progress, runLimit, false);
            if (runLimit > occupied) runLimit = occupied;
            if (runLimit < count) runLimit = 0;
        }
        p.RedoOverlay = APTR.Null;
        var written = p.SupportsUnpublishedDataWrites
            ? p.WriteUnpublishedDataBlocks(destination, count, r.Buffer)
            : p.WriteDataBlocks(destination, count, r.Buffer);
        p.RedoOverlay = active;
        if (!written) return FailContinuation(ref w, DOS.Error.DiskNotValidated);
        if (runLimit != 0) BeginTrstRun(ref p, group, active, count, runLimit, size);
        else ApplyTrstContinuation(ref p, hint, leaf, oldBitmap, newBitmap, count, oldAt, newAt, r.Length);
        handled = true; return true;
    }

    private static void ApplyTrstContinuation<T>(ref T p, APTR hint, APTR leaf, APTR oldBitmap,
        APTR newBitmap, uint count, int oldAt, int newAt, uint byteLength) where T : struct, IAmigaGuestMemory
    {
        // Data I/O expires all borrowed platform views. These pointers name
        // owned indexed-arena slots, which the unpublished transfer cannot move
        // or alter. Revoke verification before edits; each delta seals its image.
        AmigaSmartFileSystemRedoArena.SetOwnedVerification(ref p, leaf, 0);
        AmigaSmartFileSystemRedoArena.SetOwnedVerification(ref p, oldBitmap, 0);
        if (newBitmap != oldBitmap) AmigaSmartFileSystemRedoArena.SetOwnedVerification(ref p, newBitmap, 0);
        ApplyTrstContinuationLeaf(ref p, hint, leaf, count);
        var tail = p.ReadUInt32(hint, 8); var tailLength = p.ReadUInt32(hint, 12);
        var beforeLength = p.ReadUInt32(hint, 20); var destination = p.ReadUInt32(hint, 16) + beforeLength;
        UpdateTrstBitmapRange(ref p, newBitmap, newAt, destination & 31, count, false);
        UpdateTrstBitmapRange(ref p, oldBitmap, oldAt, tail & 31, count, true);
        AmigaSmartFileSystemRedoArena.SetOwnedVerification(ref p, leaf, TreeId);
        AmigaSmartFileSystemRedoArena.SetOwnedVerification(ref p, oldBitmap, BitmapId);
        if (newBitmap != oldBitmap) AmigaSmartFileSystemRedoArena.SetOwnedVerification(ref p, newBitmap, BitmapId);
        p.WriteUInt32(hint, 4, p.ReadUInt32(hint, 4) + byteLength); p.WriteUInt32(hint, 8, tail + count);
        p.WriteUInt32(hint, 12, tailLength - count); p.WriteUInt32(hint, 20, beforeLength + count);
    }

    private static void ApplyTrstContinuationLeaf<T>(ref T p, APTR hint, APTR leaf, uint count)
        where T : struct, IAmigaGuestMemory
    {
        // TrstContinuationLeaf checked every old value before the transfer.
        // Owned images survive that transfer. Accumulate one checksum delta
        // for the known edits instead of reading/sealing each field separately.
        var tail = p.ReadUInt32(hint, 8); var nextTail = tail + count;
        var aligned = unchecked(tail - nextTail);
        var rotated = unchecked(((tail << 16) | (tail >> 16)) - ((nextTail << 16) | (nextTail >> 16)));
        var sum = p.ReadUInt32(leaf, 4);
        var tailAt = (int)p.ReadUInt32(hint, 32);
        p.WriteUInt32(leaf, tailAt, nextTail);
        p.WriteUInt16(leaf, tailAt + 12, (ushort)(p.ReadUInt32(hint, 12) - count));
        sum = unchecked(sum + ((tailAt & 2) == 0 ? aligned + (count << 16) : rotated + count));
        var beforeAt = (int)p.ReadUInt32(hint, 36);
        p.WriteUInt32(leaf, beforeAt + 4, nextTail);
        p.WriteUInt16(leaf, beforeAt + 12, (ushort)(p.ReadUInt32(hint, 20) + count));
        sum = unchecked(sum + ((beforeAt & 2) == 0 ? aligned - (count << 16) : rotated - count));
        if (p.ReadUInt32(hint, 24) != 0)
        {
            var afterAt = (int)p.ReadUInt32(hint, 40);
            p.WriteUInt32(leaf, afterAt + 8, nextTail);
            sum = unchecked(sum + ((afterAt & 2) == 0 ? aligned : rotated));
        }
        p.WriteUInt32(leaf, 4, sum);
    }

    private static bool TrstRecordOffset(int offset, uint count, uint stride)
        => offset >= 16 && (uint)offset - 16 < count * stride && ((uint)offset - 16) % stride == 0;

    // Keep widened fields in the native compiler's established out-uint ABI.
    private static void TrstLeafLayout<T>(ref T p, APTR leaf, out uint stride, out uint records)
        where T : struct, IAmigaGuestMemory
    { stride = p.ReadUInt8(leaf, 15); records = p.ReadUInt16(leaf, 12); }

    private static bool TrstBitmapRange<T>(ref T p, APTR bitmap, int offset, uint bit, uint count, bool free)
        where T : struct, IAmigaGuestMemory
    {
        var remaining = count; var at = offset; var within = bit;
        while (remaining != 0)
        {
            var take = remaining < 32 - within ? remaining : 32 - within;
            var mask = (uint.MaxValue >> (int)within) & (uint.MaxValue << (int)(32 - within - take));
            if ((p.ReadUInt32(bitmap, at) & mask) != (free ? mask : 0)) return false;
            remaining -= take; at += 4; within = 0;
        }
        return true;
    }

    private static void UpdateTrstBitmapRange<T>(ref T p, APTR bitmap, int offset, uint bit, uint count, bool free)
        where T : struct, IAmigaGuestMemory
    {
        if (count == 0) return;
        // These offsets name aligned bitmap words in an already owned image.
        // No block access or fallible operation occurs during the edits. Seal
        // the complete delta once, avoiding a checksum read/write and a second
        // old-word read for every word in a materialized deferred run.
        var sum = p.ReadUInt32(bitmap, 4);
        var remaining = count; var at = offset; var within = bit;
        while (remaining != 0)
        {
            var take = remaining < 32 - within ? remaining : 32 - within;
            var mask = (uint.MaxValue >> (int)within) & (uint.MaxValue << (int)(32 - within - take));
            var old = p.ReadUInt32(bitmap, at);
            var value = free ? old | mask : old & ~mask;
            p.WriteUInt32(bitmap, at, value);
            sum = unchecked(sum + old - value);
            remaining -= take; at += 4; within = 0;
        }
        p.WriteUInt32(bitmap, 4, sum);
    }
}
