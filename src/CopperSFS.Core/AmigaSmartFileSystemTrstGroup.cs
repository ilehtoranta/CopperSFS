using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    private const uint TrstGroupMagic = 0x54525342;
    public const uint TrstGroupHeaderBytes = 220;
    private const int TrstGroupObjectHint = 64;
    private const int TrstGroupContinuation = 76;
    private const int TrstGroupProof = 124;

    /// <summary>Two bounded arenas share one caller-owned budget. The previous
    /// accepted image is immutable while the next complete Write is staged.</summary>
    public static uint TrstGroupBytes(uint budget, uint blockBytes)
    {
        if (budget < TrstGroupHeaderBytes || (budget - TrstGroupHeaderBytes) / 2 < blockBytes + 40) return 0;
        var arenaBytes = AmigaSmartFileSystemRedoArena.FitBytes((budget - TrstGroupHeaderBytes) / 2, blockBytes);
        return arenaBytes == 0 ? 0 : TrstGroupHeaderBytes + arenaBytes * 2;
    }

    public static bool InitializeTrstGroup<T>(ref T p, APTR group, uint groupBytes)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var bytes = TrstGroupBytes(groupBytes, p.BlockBytes);
        if (group.IsNull || bytes == 0 || !p.IsMapped(group, bytes)) return false;
        var arenaBytes = (bytes - TrstGroupHeaderBytes) / 2;
        var first = APTR.FromPointer(group.Raw + TrstGroupHeaderBytes);
        var second = APTR.FromPointer(first.Raw + arenaBytes);
        p.Clear(group, TrstGroupHeaderBytes);
        if (!AmigaSmartFileSystemRedoArena.Initialize(ref p, first, arenaBytes, p.BlockBytes) ||
            !AmigaSmartFileSystemRedoArena.Initialize(ref p, second, arenaBytes, p.BlockBytes)) return false;
        p.WriteUInt32(group, 0, TrstGroupMagic); p.WriteUInt32(group, 8, bytes);
        p.WriteUInt32(group, 12, arenaBytes); p.WriteUInt32(group, 16, first.Raw); p.WriteUInt32(group, 20, second.Raw);
        p.WriteUInt32(group, 52, p.BlockBytes);
        return true;
    }

    public static bool TrstGroupPending<T>(ref T p, APTR group)
        where T : struct, IAmigaWritableFileSystemPlatform
        => group.IsNotNull && (p.ReadUInt32(group, 4) & 1) != 0;

    private static bool TrstGroupValid<T>(ref T p, APTR group)
        where T : struct, IAmigaWritableFileSystemPlatform
        => group.IsNotNull && p.IsMapped(group, TrstGroupHeaderBytes) && p.ReadUInt32(group) == TrstGroupMagic &&
            p.ReadUInt32(group, 52) == p.BlockBytes && p.ReadUInt32(group, 8) >= TrstGroupHeaderBytes &&
            p.IsMapped(group, p.ReadUInt32(group, 8));

    private static void SaveTrstGroupRoots<T>(ref T p, APTR group, ref Work w)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        p.WriteUInt32(group, 24, w.RootContainer); p.WriteUInt32(group, 28, w.BitmapBase);
        p.WriteUInt32(group, 32, w.AdminContainer); p.WriteUInt32(group, 36, w.NodeRoot);
        p.WriteUInt32(group, 40, w.ExtentRoot); p.WriteUInt32(group, 44, (w.CaseSensitive ? 1u : 0u) | (w.Recycling ? 2u : 0u));
        p.WriteUInt32(group, 48, w.BlockCount); p.WriteUInt32(group, 52, w.BlockBytes);
        p.WriteUInt32(group, 56, w.ReservedBlocks);
    }

    private static void LoadTrstGroupRoots<T>(ref T p, APTR group, ref Work w)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        w.RootContainer = p.ReadUInt32(group, 24); w.BitmapBase = p.ReadUInt32(group, 28);
        w.AdminContainer = p.ReadUInt32(group, 32); w.NodeRoot = p.ReadUInt32(group, 36);
        w.ExtentRoot = p.ReadUInt32(group, 40); w.CaseSensitive = (p.ReadUInt32(group, 44) & 1) != 0;
        w.Recycling = (p.ReadUInt32(group, 44) & 2) != 0;
        w.BlockCount = p.ReadUInt32(group, 48); w.BlockBytes = p.ReadUInt32(group, 52);
        w.ReservedBlocks = p.ReadUInt32(group, 56);
    }

    /// <summary>Accept one complete Write into a private group. Success grants
    /// read-after-write visibility, not durability: FlushTrstGroup commits every
    /// accepted Write together. On failure the prior accepted view is retained.
    /// Capacity failure never publishes a prefix; callers may flush that prior
    /// group and retry the whole request. Other operations require a group flush.
    /// All volume access must remain serialized and observe RedoOverlay.</summary>
    public static bool WriteTrstGroup<T>(ref T p, ref Work w, ref Request r, APTR group, out uint actual)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        actual = 0; w.Error = DOS.Error.None; w.TrstObjectHint = APTR.Null; w.TrstContinuation = APTR.Null;
        if (!TrstGroupValid(ref p, group)) return Fail(ref w, DOS.Error.NoFreeStore);
        var span = p.ReadUInt32(group, 8); var bytes = p.BlockBytes;
        if (!TrstWorkspaceFits(ref p, ref w, group, span, bytes) || Overlap(group, span, r.Buffer, r.Length))
            return Fail(ref w, DOS.Error.NoFreeStore);
        if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);
        // Retry a failed publication only through recovery, never by encoding
        // again against a partly applied home state without replaying its journal.
        if ((p.ReadUInt32(group, 4) & 2) != 0 && !FlushTrstGroup(ref p, ref w, group)) return false;
        var pending = TrstGroupPending(ref p, group);
        var active = APTR.FromPointer(p.ReadUInt32(group, 16));
        var next = APTR.FromPointer(p.ReadUInt32(group, 20));
        if (!MaterializeTrstGroup(ref p, group, active)) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (p.RedoOverlay != (pending ? active : APTR.Null)) return Fail(ref w, DOS.Error.ActionNotKnown);
        if (!pending)
        {
            p.WriteUInt32(group, TrstGroupObjectHint, 0);
            ReleaseOverwriteReservation(ref p, ref w);
            if (!Flush(ref p, ref w)) return false;
        }
        var buffered = w.Buffered; var batch = w.Batch;
        w.Buffered = APTR.Null; w.Batch = APTR.Null;
        if (!pending)
        {
            if (!Prepare(ref p, ref w)) { w.Buffered = buffered; w.Batch = batch; return false; }
            SaveTrstGroupRoots(ref p, group, ref w);
            var root = View(ref p, ref w, w.RootContainer, ObjectId, w.Meta);
            if (root.IsNull) { w.Buffered = buffered; w.Batch = batch; return false; }
            p.WriteUInt32(group, 60, p.ReadUInt32(root, (int)bytes - 28));
        }
        else LoadTrstGroupRoots(ref p, group, ref w);
        if (pending && p.ReadUInt32(group, TrstGroupContinuation) != 0)
        {
            var continued = ContinueTrstWrite(ref p, ref w, ref r, group, active, out var handled);
            if (!continued || handled)
            {
                w.Buffered = buffered; w.Batch = batch;
                if (!continued)
                {
                    p.WriteUInt32(group, TrstGroupContinuation, 0);
                    p.WriteUInt32(group, TrstGroupProof, 0);
                    p.WriteUInt32(group, TrstGroupObjectHint, 0);
                    return false;
                }
                actual = r.Length; return true;
            }
        }
        p.WriteUInt32(group, TrstGroupContinuation, 0);
        p.WriteUInt32(group, TrstGroupProof, 0);
        var prepared = pending
            ? AmigaSmartFileSystemRedoArena.Clone(ref p, active, next, p.ReadUInt32(group, 12))
            : AmigaSmartFileSystemRedoArena.Initialize(ref p, next, p.ReadUInt32(group, 12), bytes);
        if (!prepared)
        { w.Buffered = buffered; w.Batch = batch; return Fail(ref w, DOS.Error.NoFreeStore); }
        p.RedoOverlay = next;
        if (p.RedoOverlay != next)
        { p.RedoOverlay = pending ? active : APTR.Null; w.Buffered = buffered; w.Batch = batch; return Fail(ref w, DOS.Error.ActionNotKnown); }
        var original = r;
        w.RedoStaging = true; w.RedoCheckpoint = pending ? active : APTR.Null;
        w.TrstObjectHint = APTR.FromPointer(group.Raw + TrstGroupObjectHint);
        w.TrstContinuation = APTR.FromPointer(group.Raw + TrstGroupContinuation);
        var success = Write(ref p, ref w, ref r, out var written);
        w.TrstObjectHint = APTR.Null; w.TrstContinuation = APTR.Null;
        w.RedoStaging = false; w.RedoCheckpoint = APTR.Null;
        if (p.ReadUInt32(next, 12) != 0) success = Fail(ref w, DOS.Error.NoFreeStore);
        if (success)
        {
            success = TrstGroupHasJournalSpace(ref p, ref w, group, next, out var continuationSafe);
            if (success && continuationSafe && p.ReadUInt32(group, TrstGroupContinuation) != 0)
            {
                // The conservative admission covers every future allocation in
                // the touched maps. Construct the next window while that exact
                // slot set is still owned by this invocation, instead of counting
                // its durable free bits again on the next Write. No admission is
                // retained separately from the normal checked continuation proof.
                var hint = APTR.FromPointer(group.Raw + TrstGroupContinuation);
                var savedError = w.Error;
                _ = BuildTrstProof(ref p, ref w, group, next, hint, p.ReadUInt32(hint, 8),
                    p.ReadUInt32(hint, 16) + p.ReadUInt32(hint, 20), 1, out _, true);
                // This is optional look-ahead. A declined/failed probe leaves an
                // invalid proof, while the complete Write already has admission.
                w.Error = savedError;
            }
        }
        w.Buffered = buffered; w.Batch = batch;
        if (!success)
        {
            p.WriteUInt32(group, TrstGroupObjectHint, 0);
            p.WriteUInt32(group, TrstGroupContinuation, 0);
            r = original; p.RedoOverlay = pending ? active : APTR.Null;
            return false;
        }
        p.WriteUInt32(group, 16, next.Raw); p.WriteUInt32(group, 20, active.Raw);
        var dirty = p.ReadUInt32(next) != 0;
        p.WriteUInt32(group, 4, dirty ? 1u : 0u);
        p.RedoOverlay = dirty ? next : APTR.Null;
        actual = written;
        return true;
    }

    private static bool TrstWorkspaceFits<T>(ref T p, ref Work w, APTR group, uint span, uint bytes)
        where T : struct, IAmigaGuestMemory
    {
        if (w.Meta.IsNull || w.Data.IsNull || w.Bitmap.IsNull || w.Journal.IsNull) return false;
        var lower = w.Meta.Raw; var upper = lower;
        if (w.Data.Raw < lower) lower = w.Data.Raw;
        if (w.Data.Raw > upper) upper = w.Data.Raw;
        if (w.Bitmap.Raw < lower) lower = w.Bitmap.Raw;
        if (w.Bitmap.Raw > upper) upper = w.Bitmap.Raw;
        if (w.Journal.Raw < lower) lower = w.Journal.Raw;
        if (w.Journal.Raw > upper) upper = w.Journal.Raw;
        // A mapped, disjoint envelope proves all four buffer ranges at once.
        // Do not retain that proof across calls. Separate memory regions, holes,
        // address wrap and a group between buffers use the exact old checks.
        if (upper <= uint.MaxValue - bytes)
        {
            var envelope = APTR.FromPointer(lower); var length = upper - lower + bytes;
            if (p.IsMapped(envelope, length) && !Overlap(group, span, envelope, length)) return true;
        }
        return p.IsMapped(w.Meta, bytes) && p.IsMapped(w.Data, bytes) &&
            p.IsMapped(w.Bitmap, bytes) && p.IsMapped(w.Journal, bytes) &&
            !Overlap(group, span, w.Meta, bytes) && !Overlap(group, span, w.Data, bytes) &&
            !Overlap(group, span, w.Bitmap, bytes) && !Overlap(group, span, w.Journal, bytes);
    }

    // Never acknowledge a group whose final bitmap leaves no journal space.
    // The bound covers a code byte plus four literal bytes per source word,
    // the seven-byte record header and alignment padding. On roomy volumes a
    // cheap lower bound proves that enough originally free blocks survive:
    // even if every bit in each staged bitmap were consumed, they would fit.
    // Near capacity, use the real allocator without issuing any writes.
    private static bool TrstGroupHasJournalSpace<T>(ref T p, ref Work w, APTR group, APTR arena,
        out bool continuationSafe, bool allowSearch = true)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        continuationSafe = false;
        var count = p.ReadUInt32(arena);
        if (count == 0) return true;
        var recordBytes = (w.BlockBytes / 4) * 5 + 8;
        var payload = w.BlockBytes - 16;
        var pages = (count * recordBytes + payload - 1) / payload;
        var stride = w.BlockBytes + AmigaSmartFileSystemRedoArena.SlotHeaderBytes;
        var slot = APTR.FromPointer(p.ReadUInt32(arena, 20));
        var bitmapCount = 0u;
        for (var i = 0u; i < count; i++, slot = APTR.FromPointer(slot.Raw + stride))
            if (p.ReadUInt32(slot, (int)AmigaSmartFileSystemRedoArena.SlotHeaderBytes) == BitmapId) bitmapCount++;
        var unavailable = bitmapCount * ((w.BlockBytes - 12) * 8) + count;
        var originalFree = p.ReadUInt32(group, 60);
        if (originalFree >= unavailable && originalFree - unavailable >= pages)
        { continuationSafe = true; return true; }
        // A touched bitmap can only consume blocks that were originally free
        // in that map. Count those bits when the cheap whole-map bound fails.
        // This still charges every possible future allocation in those maps,
        // so the certificate remains valid throughout a continuation window.
        if (originalFree >= count + pages)
        {
            if (!TrstTouchedMapFreeCount(ref p, ref w, arena, count, out var touchedFree)) return false;
            unavailable = touchedFree + count;
            if (originalFree >= unavailable && originalFree - unavailable >= pages)
            { continuationSafe = true; return true; }
        }
        if (!allowSearch) return false;
        var staged = new RedoPlatform<T> { Inner = p, Arena = arena,
            Buffer = APTR.FromPointer(p.ReadUInt32(arena, 20)), Stride = stride, Count = count };
        var stream = new RedoStream { Search = p.ReservedBlocks, Bitmap = uint.MaxValue };
        var overlay = p.RedoOverlay;
        p.RedoOverlay = APTR.Null;
        var enough = true;
        for (var i = 0u; i < pages; i++)
            if (!TrstSpace(ref p, ref w, ref staged, ref stream, out _)) { enough = false; break; }
        p.RedoOverlay = overlay;
        return enough;
    }

    private static bool TrstTouchedMapFreeCount<T>(ref T p, ref Work w, APTR arena, uint count,
        out uint free) where T : struct, IAmigaWritableFileSystemPlatform
    {
        free = 0;
        var slot = p.ReadUInt32(arena, 20);
        var maps = (w.BlockCount - 1) / ((w.BlockBytes - 12) * 8) + 1;
        var overlay = p.RedoOverlay;
        p.RedoOverlay = APTR.Null;
        for (var i = 0u; i < count; i++, slot += w.BlockBytes + AmigaSmartFileSystemRedoArena.SlotHeaderBytes)
        {
            var image = APTR.FromPointer(slot + AmigaSmartFileSystemRedoArena.SlotHeaderBytes);
            if (p.ReadUInt32(image) != BitmapId) continue;
            // File payload can begin with BTMP too. Only actual allocation
            // bitmap targets can consume free space in this bound.
            var target = p.ReadUInt32(APTR.FromPointer(slot));
            if (target < w.BitmapBase || target - w.BitmapBase >= maps) continue;
            var original = View(ref p, ref w, target, BitmapId, w.Journal);
            if (original.IsNull) { p.RedoOverlay = overlay; return false; }
            // Consume the borrowed original before the next block access.
            free += CountTrstBitmapFreeBits(ref p, original, w.BlockBytes);
        }
        p.RedoOverlay = overlay;
        return true;
    }

    /// <summary>Make the complete accepted group durable with the ordinary
    /// original-SFS-compatible TRST protocol. Failed commits retain the logical
    /// view; retries recover any published journal before attempting publication.</summary>
    public static bool FlushTrstGroup<T>(ref T p, ref Work w, APTR group)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        w.Error = DOS.Error.None;
        if (!TrstGroupValid(ref p, group)) return Fail(ref w, DOS.Error.NoFreeStore);
        if (!MaterializeTrstGroup(ref p, group, APTR.FromPointer(p.ReadUInt32(group, 16))))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        p.WriteUInt32(group, TrstGroupContinuation, 0);
        p.WriteUInt32(group, TrstGroupProof, 0);
        if (!TrstGroupPending(ref p, group)) return true;
        if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);
        var active = APTR.FromPointer(p.ReadUInt32(group, 16));
        var buffered = w.Buffered; var batch = w.Batch;
        w.Buffered = APTR.Null; w.Batch = APTR.Null;
        p.RedoOverlay = APTR.Null;
        if ((p.ReadUInt32(group, 4) & 2) != 0)
        {
            if (!Prepare(ref p, ref w))
            { p.RedoOverlay = active; w.Buffered = buffered; w.Batch = batch; return false; }
        }
        else LoadTrstGroupRoots(ref p, group, ref w);
        var staged = new RedoPlatform<T> { Inner = p, Arena = active,
            Buffer = APTR.FromPointer(p.ReadUInt32(active, 20)), Stride = p.BlockBytes + AmigaSmartFileSystemRedoArena.SlotHeaderBytes, Count = p.ReadUInt32(active) };
        p.WriteUInt32(group, 4, 3);
        var success = CommitTrst(ref p, ref w, ref staged);
        w.Buffered = buffered; w.Batch = batch;
        if (!success) { p.RedoOverlay = active; return false; }
        p.WriteUInt32(group, 4, 0);
        p.WriteUInt32(group, TrstGroupObjectHint, 0);
        return true;
    }
}
