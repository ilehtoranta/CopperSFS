using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    // A buffered group can retire blocks which remain reachable on disk.
    // Prefer a complete replacement run free in all three allocation views,
    // so the ordinary unpublished-data path does not need payload arena slots.
    // If no such run exists, retain the generic staged-data fallback.
    private static bool FindTrstFreeRun<T>(ref T p, ref Work w, uint preferred, uint length,
        out uint first, out uint count) where T : struct, IAmigaWritableFileSystemPlatform
    {
        first = count = 0;
        if (preferred >= w.ReservedBlocks && preferred < w.BlockCount - 1)
        {
            // Search forward from the previous replacement run. Restarting at
            // the beginning after one occupied bit repeatedly merges bitmap
            // images already known to precede this file's current allocation.
            // The full fallback below still finds space before this position.
            if (!ScanFreeRun(ref p, ref w, preferred, length, length, true, false, out first, out count, true)) return false;
            if (count != 0) return true;
        }
        return ScanFreeRun(ref p, ref w, w.ReservedBlocks, length, length, true, false, out first, out count, true);
    }

    private static APTR TrstFreeSearchMap<T>(ref T p, ref Work w, uint mapIndex)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        // FreeSearchMap supplies mutable scratch, including marker and live
        // reservations. It does not return a borrowed platform view.
        if (!FreeSearchMap(ref p, ref w, mapIndex)) return APTR.Null;
        var map = w.BitmapBase + mapIndex;
        var checkpoint = AmigaSmartFileSystemRedoArena.MetadataView(ref p, w.RedoCheckpoint,
            map, BitmapId, false, out var found);
        if (found && checkpoint.IsNull) { FailUnlessSet(ref w); return APTR.Null; }
        var overlay = p.RedoOverlay;
        p.RedoOverlay = APTR.Null;
        var original = View(ref p, ref w, map, BitmapId, w.Journal);
        if (original.IsNull) { p.RedoOverlay = overlay; return APTR.Null; }
        // The original view is consumed before another block access. The
        // checkpoint is an owned arena image and survives that original read.
        IntersectTrstBitmapWords(ref p, w.Bitmap, original, checkpoint, w.BlockBytes);
        p.RedoOverlay = overlay;
        // A future home write must not replace any newly unpublished data.
        // The current candidate arena contains every accepted checkpoint slot.
        var bits = (w.BlockBytes - 12) * 8;
        var slots = p.ReadUInt32(overlay);
        var slot = p.ReadUInt32(overlay, 20);
        for (var i = 0u; i < slots; i++, slot += w.BlockBytes + AmigaSmartFileSystemRedoArena.SlotHeaderBytes)
            ClearSearchBit(ref p, w.Bitmap, mapIndex, bits, p.ReadUInt32(APTR.FromPointer(slot)));
        return w.Bitmap;
    }
}
