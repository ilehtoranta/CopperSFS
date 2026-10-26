using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    // Immutable certificate for one unchanged arena: identity, original free
    // count, slot count, source/destination starts and length, three owned slot
    // indices, three validated record offsets, leaf layout, bitmap keys and initial bits.
    // The final word seals the preceding eighteen words against stale/corrupt
    // private fields. Current extent links and allocation bits remain checked.
    private static bool TrstProofSealed<T>(ref T p, APTR proof)
        where T : struct, IAmigaGuestMemory
    {
        var sum = 0u;
        for (var at = 0; at < 76; at += 4) sum = unchecked(sum + p.ReadUInt32(proof, at));
        return sum == uint.MaxValue;
    }

    private static bool BuildTrstProof<T>(ref T p, ref Work w, APTR group, APTR active, APTR hint,
        uint tail, uint destination, uint count, out bool ready, bool journalSpaceKnown = false)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        ready = false;
        var proof = APTR.FromPointer(group.Raw + TrstGroupProof);
        p.WriteUInt32(proof, 0, 0);
        var bits = (w.BlockBytes - 12) * 8;
        var sourceBit = tail % bits; var destinationBit = destination % bits;
        var limit = p.ReadUInt32(hint, 12) - 1;
        var maximum = ushort.MaxValue - p.ReadUInt32(hint, 20);
        if (limit > maximum) limit = maximum;
        if (limit > bits - sourceBit) limit = bits - sourceBit;
        if (limit > bits - destinationBit) limit = bits - destinationBit;
        if (limit > w.BlockCount - 1 - destination) limit = w.BlockCount - 1 - destination;
        if (count > limit) return true;
        if (!BitmapPosition(ref w, tail, out var oldMap, out _, out _) ||
            !BitmapPosition(ref w, destination, out var newMap, out _, out _)) return false;
        var leaf = AmigaSmartFileSystemRedoArena.MetadataView(ref p, active, p.ReadUInt32(hint, 28), TreeId, false, out var found);
        if (leaf.IsNull) return !found || FailUnlessSet(ref w);
        if (!TrstContinuationLeaf(ref p, w.BlockBytes, hint, leaf, APTR.Null)) return Fail(ref w, DOS.Error.DiskNotValidated);
        var oldBitmap = AmigaSmartFileSystemRedoArena.MetadataView(ref p, active, oldMap, BitmapId, false, out found);
        if (oldBitmap.IsNull) return !found || FailUnlessSet(ref w);
        var newBitmap = oldMap == newMap ? oldBitmap :
            AmigaSmartFileSystemRedoArena.MetadataView(ref p, active, newMap, BitmapId, false, out found);
        if (newBitmap.IsNull) return !found || FailUnlessSet(ref w);
        if (!journalSpaceKnown && !TrstGroupHasJournalSpace(ref p, ref w, group, active, out _, false))
            return w.Error == DOS.Error.None;

        // The slot set and its image types cannot change during continuations.
        // Stop before any staged target, including a retired target that is
        // currently free but would later be replaced by journal publication.
        var slots = p.ReadUInt32(active); var slotBase = p.ReadUInt32(active, 20);
        var stride = w.BlockBytes + AmigaSmartFileSystemRedoArena.SlotHeaderBytes;
        var slot = slotBase;
        for (var i = 0u; i < slots; i++, slot += stride)
        {
            var target = p.ReadUInt32(APTR.FromPointer(slot));
            if (target >= destination && target - destination < limit) limit = target - destination;
        }
        var marker = w.RootContainer + 2;
        if (marker >= destination && marker - destination < limit) limit = marker - destination;
        if (count > limit) return true;

        // Inspect the durable view once for this bounded window. The arena's
        // current bitmap is checked again for every requested Write. No cached
        // platform pointer survives the next block access or the return.
        p.RedoOverlay = APTR.Null;
        // Eager admission can return directly to a flush, which reuses Journal
        // before another block access. Request an owned scratch copy there so
        // no borrowed platform view remains live across that reuse.
        var durable = journalSpaceKnown
            ? (Read(ref p, ref w, newMap, BitmapId, w.Journal) ? w.Journal : APTR.Null)
            : View(ref p, ref w, newMap, BitmapId, w.Journal);
        if (durable.IsNull) { p.RedoOverlay = active; return false; }
        var free = TrstFreeSpan(ref p, durable, destinationBit, limit);
        p.RedoOverlay = active;
        if (free < count) return true;

        TrstLeafLayout(ref p, leaf, out var recordStride, out var records);
        p.WriteUInt32(proof, 4, p.ReadUInt32(group, 60));
        p.WriteUInt32(proof, 8, slots); p.WriteUInt32(proof, 12, tail);
        p.WriteUInt32(proof, 16, destination); p.WriteUInt32(proof, 20, free);
        p.WriteUInt32(proof, 24, (leaf.Raw - AmigaSmartFileSystemRedoArena.SlotHeaderBytes - slotBase) / stride);
        p.WriteUInt32(proof, 28, (oldBitmap.Raw - AmigaSmartFileSystemRedoArena.SlotHeaderBytes - slotBase) / stride);
        p.WriteUInt32(proof, 32, (newBitmap.Raw - AmigaSmartFileSystemRedoArena.SlotHeaderBytes - slotBase) / stride);
        p.WriteUInt32(proof, 36, p.ReadUInt32(hint, 32));
        p.WriteUInt32(proof, 40, p.ReadUInt32(hint, 36));
        p.WriteUInt32(proof, 44, p.ReadUInt32(hint, 24) == 0 ? 0 : p.ReadUInt32(hint, 40));
        p.WriteUInt32(proof, 48, records); p.WriteUInt32(proof, 52, recordStride);
        p.WriteUInt32(proof, 56, oldMap); p.WriteUInt32(proof, 60, newMap);
        p.WriteUInt32(proof, 64, sourceBit); p.WriteUInt32(proof, 68, destinationBit);
        var sum = active.Raw;
        for (var at = 4; at < 72; at += 4) sum = unchecked(sum + p.ReadUInt32(proof, at));
        p.WriteUInt32(proof, 72, ~sum); p.WriteUInt32(proof, 0, active.Raw);
        ready = true; return true;
    }

    private static uint TrstFreeSpan<T>(ref T p, APTR bitmap, uint bit, uint limit, bool free = true)
        where T : struct, IAmigaGuestMemory
    {
        var offset = 12 + (int)(bit >> 5) * 4;
        var within = bit & 31; var result = 0u;
        while (result < limit)
        {
            // Full words need no dynamic masks. Consume a bounded run from
            // this already verified bitmap using the same scalar/unrolled
            // scanners as allocation; never read beyond the proof window.
            if (within == 0 && limit - result >= 32)
            {
                var words = (limit - result) >> 5;
                var cursor = APTR.FromPointer(bitmap.Raw + (uint)offset);
                var skipped = free ? SkipFreeBitmapWords(ref p, cursor, words) :
                    SkipUsedBitmapWords(ref p, cursor, words);
                result += skipped; offset += (int)(skipped >> 3);
                if (result == limit) return result;
            }
            var take = 32 - within;
            if (take > limit - result) take = limit - result;
            var word = p.ReadUInt32(bitmap, offset);
            if (!free) word = ~word;
            var mask = (uint.MaxValue >> (int)within) & (uint.MaxValue << (int)(32 - within - take));
            if ((word & mask) != mask) return result + LeadingZeros(~word << (int)within);
            result += take; offset += 4; within = 0;
        }
        return result;
    }

    private static bool TrstContinuationLeaf<T>(ref T p, uint blockBytes, APTR hint, APTR leaf, APTR proof)
        where T : struct, IAmigaGuestMemory
    {
        var tailAt = (int)p.ReadUInt32(hint, 32); var beforeAt = (int)p.ReadUInt32(hint, 36);
        var afterAt = (int)p.ReadUInt32(hint, 40); var after = p.ReadUInt32(hint, 24);
        TrstLeafLayout(ref p, leaf, out var stride, out var records);
        if (p.ReadUInt8(leaf, 14) != 1) return false;
        if (proof.IsNull)
        {
            if (stride < 14 || (stride & 1) != 0 || records > (blockBytes - 16) / stride ||
                !TrstRecordOffset(tailAt, records, stride) || !TrstRecordOffset(beforeAt, records, stride) || tailAt == beforeAt ||
                (after != 0 && (!TrstRecordOffset(afterAt, records, stride) || afterAt == tailAt || afterAt == beforeAt)))
                return false;
        }
        // Construction checked offset bounds, alignment and distinctness. The
        // sealed certificate may reuse those offsets only with this same layout.
        else if (records != p.ReadUInt32(proof, 48) || stride != p.ReadUInt32(proof, 52) ||
            (uint)tailAt != p.ReadUInt32(proof, 36) || (uint)beforeAt != p.ReadUInt32(proof, 40) ||
            (after != 0 && (p.ReadUInt32(proof, 44) == 0 || (uint)afterAt != p.ReadUInt32(proof, 44))))
            return false;
        var tail = p.ReadUInt32(hint, 8); var before = p.ReadUInt32(hint, 16);
        if (p.ReadUInt32(leaf, tailAt) != tail || (uint)p.ReadUInt16(leaf, tailAt + 12) != p.ReadUInt32(hint, 12) ||
            p.ReadUInt32(leaf, tailAt + 4) != after || p.ReadUInt32(leaf, tailAt + 8) != before ||
            p.ReadUInt32(leaf, beforeAt) != before || (uint)p.ReadUInt16(leaf, beforeAt + 12) != p.ReadUInt32(hint, 20) ||
            p.ReadUInt32(leaf, beforeAt + 4) != tail ||
            (after != 0 && (p.ReadUInt32(leaf, afterAt) != after || p.ReadUInt32(leaf, afterAt + 8) != tail)))
            return false;
        return true;
    }

}
