using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    // A source/destination bitmap boundary does not invalidate the retained
    // extent layout. Admit missing maps into the existing spare arena, prove
    // its complete window, then accept the whole Write with the same owned-
    // image continuation. The accepted arena is never edited speculatively.
    private static bool ContinueTrstBoundary<T>(ref T p, ref Work w, ref TrstContinuationWork pending,
        ref Request r, APTR group, APTR active, uint count, out bool handled)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        handled = false;
        var hint = APTR.FromPointer(group.Raw + TrstGroupContinuation);
        var tail = p.ReadUInt32(hint, 8);
        var destination = p.ReadUInt32(hint, 16) + p.ReadUInt32(hint, 20);
        var bits = (w.BlockBytes - 12) * 8;
        // A request spanning maps still uses the general whole-Write path.
        if (count > bits - tail % bits || count > bits - destination % bits) return true;
        if (!BitmapPosition(ref w, tail, out var oldMap, out _, out _) ||
            !BitmapPosition(ref w, destination, out var newMap, out _, out _)) return false;
        var oldBitmap = AmigaSmartFileSystemRedoArena.MetadataView(ref p, active, oldMap, BitmapId, false, out var oldFound);
        if (oldFound && oldBitmap.IsNull) return Fail(ref w, DOS.Error.DiskNotValidated);
        var newBitmap = oldMap == newMap ? oldBitmap :
            AmigaSmartFileSystemRedoArena.MetadataView(ref p, active, newMap, BitmapId, false, out _);
        if (oldBitmap.IsNotNull && newBitmap.IsNotNull) return true;

        var next = APTR.FromPointer(p.ReadUInt32(group, 20));
        if (!AmigaSmartFileSystemRedoArena.Clone(ref p, active, next, p.ReadUInt32(group, 12)))
            return Fail(ref w, DOS.Error.NoFreeStore);
        p.RedoOverlay = next;
        if (p.RedoOverlay != next)
        { p.RedoOverlay = active; return Fail(ref w, DOS.Error.ActionNotKnown); }
        var success = oldBitmap.IsNotNull || OwnTrstBitmap(ref p, ref w, next, oldMap);
        if (success && oldMap != newMap && newBitmap.IsNull) success = OwnTrstBitmap(ref p, ref w, next, newMap);
        if (success)
        {
            p.WriteUInt32(hint, 44, next.Raw);
            success = BuildTrstProof(ref p, ref w, group, next, hint, tail, destination, count, out var ready);
            if (success && ready)
                success = ContinueTrstWriteReady(ref p, ref pending, ref r, group, next, out handled, out _);
        }
        if (handled)
        {
            // No fallible operation remains after the unpublished payload and
            // bounded owned-image edits. The whole new view becomes accepted.
            p.WriteUInt32(group, 16, next.Raw); p.WriteUInt32(group, 20, active.Raw);
        }
        else
        {
            p.RedoOverlay = active;
            p.WriteUInt32(hint, 44, active.Raw);
            p.WriteUInt32(group, TrstGroupProof, 0);
            // On a declined probe, WriteTrstGroup reclones the accepted arena
            // before general allocation. Speculative slots cannot leak into it.
        }
        return success;
    }

    private static bool OwnTrstBitmap<T>(ref T p, ref Work w, APTR arena, uint block)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var image = View(ref p, ref w, block, BitmapId, w.Bitmap);
        if (image.IsNull) return false;
        // The borrowed image is unchanged and verified. Copy it before the
        // next platform block access; only the owned copy survives that access.
        return AmigaSmartFileSystemRedoArena.StoreSealed(ref p, arena, block, image) || Fail(ref w, DOS.Error.NoFreeStore);
    }
}
