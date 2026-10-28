using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    // An established continuation only uses metadata scratch and the existing
    // reservation set. Allocation, journal and generic-write state stay in Work.
    private struct TrstContinuationWork
    {
        public APTR Meta, Reservations;
        public uint RootContainer, BlockBytes, BlockCount, ReservedBlocks;
        public DOS.Error Error;
    }

    private static bool FailContinuation(ref TrstContinuationWork w, DOS.Error error)
    { w.Error = error; return false; }

    /// <summary>Try one complete overwrite using an existing pending proof.
    /// A successful call with handled=false requests the ordinary WriteTrstGroup
    /// path and does not accept any bytes. Only metadata scratch is needed;
    /// this entry cannot build proofs, allocate, publish or extend a file.
    /// All volume access must remain serialized and observe RedoOverlay.</summary>
    public static bool TryContinueTrstGroup<T>(ref T p, APTR group, APTR meta, APTR reservations,
        ref Request r, out bool handled, out DOS.Error error, bool deferUpdates = false)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        handled = false; error = DOS.Error.None;
        if (group.IsNull) return true;
        // Retain the same header and full-span checks in their sole caller.
        // This region certificate still expires before any protection query.
        var mappedLower = 0u; var mappedUpper = 0u;
        if (group.IsNull || !p.TryMappedRegion(group, TrstGroupHeaderBytes, out mappedLower, out mappedUpper) ||
            p.ReadUInt32(group) != TrstGroupMagic || p.ReadUInt32(group, 52) != p.BlockBytes)
        { error = DOS.Error.NoFreeStore; return false; }
        var mappedSpan = p.ReadUInt32(group, 8);
        if (mappedSpan < TrstGroupHeaderBytes ||
            (mappedSpan > mappedUpper - group.Raw && !p.IsMapped(group, mappedSpan)))
        { error = DOS.Error.NoFreeStore; return false; }
        var active = APTR.FromPointer(p.ReadUInt32(group, 16));
        var deferred = p.ReadUInt32(group, TrstDeferredCount) != 0 || p.ReadUInt32(group, TrstDeferredInverse) != 0;
        // A publication in progress needs recovery through the ordinary path.
        if (!deferred && (p.ReadUInt32(group, 4) != 1 || p.ReadUInt32(group, TrstGroupContinuation) == 0 ||
            p.ReadUInt32(group, TrstGroupProof) != active.Raw)) return true;
        var span = p.ReadUInt32(group, 8); var bytes = p.BlockBytes;
        if (meta.IsNull || ((meta.Raw < mappedLower || meta.Raw > mappedUpper || bytes > mappedUpper - meta.Raw) && !p.IsMapped(meta, bytes)) || Overlap(group, span, meta, bytes) ||
            Overlap(group, span, r.Buffer, r.Length))
        { error = DOS.Error.NoFreeStore; return false; }
        // An eligible deferred transfer writes only unpublished destinations and
        // accepts its complete byte count only after the device reports success.
        // Opted-in platforms can let that write enforce hardware protection.
        // A declined continuation still reaches the ordinary checked path.
        var queryProtection = !deferred || !deferUpdates || !p.SupportsUnpublishedDataWrites ||
            !p.UnpublishedDataWritesEnforceProtection;
        if (queryProtection && p.WriteProtected())
        { error = DOS.Error.DiskWriteProtected; return false; }
        // A device query expires every region bound, even when it succeeds.
        if (queryProtection) mappedLower = mappedUpper = 0;
        if (p.RedoOverlay != active) { error = DOS.Error.ActionNotKnown; return false; }
        if (deferred)
        {
            if (deferUpdates)
            {
                // Keep the single-caller deferred path in this invocation. The
                // same span, private integrity and request checks still run,
                // and a protection query has already revoked region reuse.
                handled = false; error = DOS.Error.None;
                // Retain every deferred-run validation in the hot caller.
                // The checked scalars remain local to this invocation. Until
                // payload I/O below, only private RAM reads and mapping checks
                // occur, so use the validated values without reading them again.
                if (group.Raw <= 1 || span < TrstGroupHeaderBytes || group.Raw > uint.MaxValue - span ||
                    (queryProtection && !p.IsMapped(group, span)) || p.ReadUInt32(group) != TrstGroupMagic ||
                    p.ReadUInt32(group, 4) != 1 || p.ReadUInt32(group, 8) != span) { error = DOS.Error.DiskNotValidated; return false; }
                var validationArenaBytes = p.ReadUInt32(group, 12);
                var validationBytes = p.ReadUInt32(group, 52);
                if (validationBytes < 512 || validationBytes > 32768 || validationArenaBytes < validationBytes + 40 ||
                    validationArenaBytes > (uint.MaxValue - TrstGroupHeaderBytes) / 2 ||
                    span != TrstGroupHeaderBytes + validationArenaBytes * 2) { error = DOS.Error.DiskNotValidated; return false; }
                var validationFirst = group.Raw + TrstGroupHeaderBytes;
                if ((active.Raw != validationFirst && active.Raw != validationFirst + validationArenaBytes) ||
                    p.ReadUInt32(group, 16) != active.Raw ||
                    p.ReadUInt32(active, 8) != (validationBytes | 0x80000000 | AmigaSmartFileSystemRedoArena.Deferred) ||
                    p.ReadUInt32(active, 12) != group.Raw) { error = DOS.Error.DiskNotValidated; return false; }
                var validationMask = p.ReadUInt32(active, 16);
                if (validationMask == 0 || validationMask == uint.MaxValue || (validationMask & (validationMask + 1)) != 0 ||
                    validationMask + 1 > (validationArenaBytes - AmigaSmartFileSystemRedoArena.HeaderBytes) / 4) { error = DOS.Error.DiskNotValidated; return false; }
                var validationPrefix = AmigaSmartFileSystemRedoArena.HeaderBytes + (validationMask + 1) * 4;
                var validationCapacity = p.ReadUInt32(active, 4); var validationSlots = p.ReadUInt32(active);
                var validationAvailable = validationArenaBytes - validationPrefix;
                if (validationCapacity == 0 || validationCapacity > validationMask || validationCapacity > (validationMask + 1) - ((validationMask + 1) >> 2) ||
                    validationSlots == 0 || validationSlots > validationCapacity || p.ReadUInt32(active, 20) != active.Raw + validationPrefix) { error = DOS.Error.DiskNotValidated; return false; }
                // The two-arena span bounds available below 2 GiB. The first 512-byte
                // bound therefore makes the 520-byte shift/add product overflow-safe.
                if (validationBytes == 512)
                {
                    if (validationCapacity > (validationAvailable >> 9) || (validationCapacity << 9) + (validationCapacity << 3) > validationAvailable) { error = DOS.Error.DiskNotValidated; return false; }
                }
                else if (validationCapacity > validationAvailable / (validationBytes + AmigaSmartFileSystemRedoArena.SlotHeaderBytes)) { error = DOS.Error.DiskNotValidated; return false; }
                var validationCount = p.ReadUInt32(group, TrstDeferredCount); var validationLimit = p.ReadUInt32(group, TrstDeferredLimit);
                if (validationCount == 0 || validationCount > validationLimit || validationLimit > ushort.MaxValue ||
                    p.ReadUInt32(group, TrstDeferredInverse) != ~validationCount ||
                    p.ReadUInt32(group, TrstDeferredSeal) != TrstRunSeal(ref p, group, active)) { error = DOS.Error.DiskNotValidated; return false; }
                var validationPosition = p.ReadUInt32(group, TrstGroupContinuation + 4);
                var validationFileLimit = p.ReadUInt32(group, TrstDeferredFileLimit);
                // The validated count and block size above prove this product fits.
                var validationAcceptedBytes = validationBytes == 512 ? validationCount << 9 : validationCount * validationBytes;
                if (!(validationPosition <= validationFileLimit && validationAcceptedBytes <= validationFileLimit - validationPosition)) { error = DOS.Error.DiskNotValidated; return false; }
                var runBytes = validationBytes; var accepted = validationCount;
                var position = validationPosition + validationAcceptedBytes;
                var fileLimit = validationFileLimit;
                var eligible = r.Node == p.ReadUInt32(group, TrstGroupContinuation) && r.Position == position &&
                    r.Length != 0 && r.Length <= 65536 && (r.Buffer.Raw & 3) == 0 &&
                    (runBytes == 512 ? (r.Length & 511) == 0 : r.Length % runBytes == 0) &&
                    r.Length <= fileLimit - position;
                var count = runBytes == 512 ? r.Length >> 9 : r.Length / runBytes;
                if (!eligible || count > validationLimit - accepted ||
                    (reservations.IsNotNull && ((reservations.Raw >= mappedLower && reservations.Raw <= mappedUpper && OverwriteReservationBytes <= mappedUpper - reservations.Raw) || p.IsMapped(reservations, OverwriteReservationBytes)) && p.ReadUInt32(reservations) != 0))
                {
                    if (!MaterializeTrstRun(ref p, active, group)) { error = DOS.Error.DiskNotValidated; return false; }
                    return true;
                }
                if ((r.Buffer.Raw < mappedLower || r.Buffer.Raw > mappedUpper || r.Length > mappedUpper - r.Buffer.Raw) && !p.IsMapped(r.Buffer, r.Length)) { error = DOS.Error.BadNumber; return false; }
                var destination = p.ReadUInt32(group, TrstGroupContinuation + 16) +
                    p.ReadUInt32(group, TrstGroupContinuation + 20) + accepted;
                p.RedoOverlay = APTR.Null;
                var written = p.SupportsUnpublishedDataWrites
                    ? p.WriteUnpublishedDataBlocks(destination, count, r.Buffer)
                    : p.WriteDataBlocks(destination, count, r.Buffer);
                p.RedoOverlay = active;
                // Preserve the entire accepted run, including its certificate, on I/O
                // failure. No retry, read or flush may lose the earlier accepted prefix.
                if (!written)
                {
                    // A failed direct write has not advanced the accepted run. Query
                    // protection only on this error path, preserving the earlier view.
                    error = p.SupportsUnpublishedDataWrites && p.UnpublishedDataWritesEnforceProtection && p.WriteProtected()
                        ? DOS.Error.DiskWriteProtected : DOS.Error.DiskNotValidated;
                    return false;
                }
                accepted += count;
                p.WriteUInt32(group, TrstDeferredCount, accepted);
                p.WriteUInt32(group, TrstDeferredInverse, ~accepted);
                handled = true; return true;
            }
            if (!MaterializeTrstRun(ref p, active, group)) { error = DOS.Error.DiskNotValidated; return false; }
        }
        var w = new TrstContinuationWork { Meta = meta, Reservations = reservations,
            RootContainer = p.ReadUInt32(group, 24), BlockBytes = bytes,
            BlockCount = p.ReadUInt32(group, 48), ReservedBlocks = p.ReadUInt32(group, 56) };
        var success = ContinueTrstWriteReady(ref p, ref w, ref r, group, active, out handled, out _, deferUpdates);
        error = w.Error;
        if (!success)
        {
            p.WriteUInt32(group, TrstGroupContinuation, 0);
            p.WriteUInt32(group, TrstGroupProof, 0);
            p.WriteUInt32(group, TrstGroupObjectHint, 0);
        }
        return success;
    }
}
