using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    private const int TrstDeferredCount = 200;
    private const int TrstDeferredInverse = 204;
    private const int TrstDeferredFileLimit = 208;
    private const int TrstDeferredLimit = 212;
    private const int TrstDeferredSeal = 216;

    // The existing hint stays at the materialized prefix. Only this bounded
    // count advances after a whole successful unpublished payload transfer.
    // All arena accessors materialize before exposing or changing any image.
    // No borrowed metadata pointer is retained and no payload is buffered here.
    private static uint TrstRunSeal<T>(ref T p, APTR group, APTR arena)
        where T : struct, IAmigaGuestMemory
    {
        // A position-weighted sum also detects changes to a proof whose own
        // additive checksum has been recomputed. This is a private integrity
        // check, not authentication. Count/inverse are checked separately.
        var sum = 0u; var weighted = 0u;
        // Preserve every ordered prefix sum, using a moving address and
        // constant displacements instead of rebuilding an indexed address for
        // each word. Only the original 192-byte prefix is read by this loop.
        var pointer = group.Raw;
        var remaining = 6u;
        while (remaining != 0)
        {
            var input = APTR.FromPointer(pointer);
            sum = unchecked(sum + p.ReadUInt32(input, 0)); weighted = unchecked(weighted + sum);
            sum = unchecked(sum + p.ReadUInt32(input, 4)); weighted = unchecked(weighted + sum);
            sum = unchecked(sum + p.ReadUInt32(input, 8)); weighted = unchecked(weighted + sum);
            sum = unchecked(sum + p.ReadUInt32(input, 12)); weighted = unchecked(weighted + sum);
            sum = unchecked(sum + p.ReadUInt32(input, 16)); weighted = unchecked(weighted + sum);
            sum = unchecked(sum + p.ReadUInt32(input, 20)); weighted = unchecked(weighted + sum);
            sum = unchecked(sum + p.ReadUInt32(input, 24)); weighted = unchecked(weighted + sum);
            sum = unchecked(sum + p.ReadUInt32(input, 28)); weighted = unchecked(weighted + sum);
            pointer += 32; remaining--;
        }
        sum = unchecked(sum + p.ReadUInt32(group, 192)); weighted = unchecked(weighted + sum);
        sum = unchecked(sum + p.ReadUInt32(group, 196)); weighted = unchecked(weighted + sum);
        sum = unchecked(sum + p.ReadUInt32(group, TrstDeferredFileLimit)); weighted = unchecked(weighted + sum);
        sum = unchecked(sum + p.ReadUInt32(group, TrstDeferredLimit)); weighted = unchecked(weighted + sum);
        sum = unchecked(sum + p.ReadUInt32(arena, 0)); weighted = unchecked(weighted + sum);
        sum = unchecked(sum + p.ReadUInt32(arena, 4)); weighted = unchecked(weighted + sum);
        sum = unchecked(sum + p.ReadUInt32(arena, 8)); weighted = unchecked(weighted + sum);
        sum = unchecked(sum + p.ReadUInt32(arena, 16)); weighted = unchecked(weighted + sum);
        sum = unchecked(sum + p.ReadUInt32(arena, 20)); weighted = unchecked(weighted + sum);
        return weighted;
    }

    private static void BeginTrstRun<T>(ref T p, APTR group, APTR arena, uint count, uint limit, uint fileLimit)
        where T : struct, IAmigaGuestMemory
    {
        p.WriteUInt32(group, TrstDeferredCount, count);
        p.WriteUInt32(group, TrstDeferredInverse, ~count);
        p.WriteUInt32(group, TrstDeferredFileLimit, fileLimit);
        p.WriteUInt32(group, TrstDeferredLimit, limit);
        p.WriteUInt32(arena, 8, p.ReadUInt32(arena, 8) | AmigaSmartFileSystemRedoArena.Deferred);
        p.WriteUInt32(arena, 12, group.Raw);
        p.WriteUInt32(group, TrstDeferredSeal, TrstRunSeal(ref p, group, arena));
    }

    private static bool TrstRunValid<T>(ref T p, APTR group, APTR arena)
        where T : struct, IAmigaGuestMemory
    {
        if (group.Raw <= 1 || !p.IsMapped(group, TrstGroupHeaderBytes)) return false;
        return TrstRunValidSpan(ref p, group, arena, p.ReadUInt32(group, 8));
    }

    // span is an untrusted length, not a cached mapping certificate. Recheck
    // its entire mapping before reading any group/arena byte. The pending entry
    // already has this scalar before its protection query, so it need not walk
    // the memory list again merely to retrieve the length after that device I/O.
    private static bool TrstRunValidSpan<T>(ref T p, APTR group, APTR arena, uint span, bool spanMapped = false)
        where T : struct, IAmigaGuestMemory
    {
        if (group.Raw <= 1 || span < TrstGroupHeaderBytes || group.Raw > uint.MaxValue - span ||
            (!spanMapped && !p.IsMapped(group, span)) || p.ReadUInt32(group) != TrstGroupMagic ||
            p.ReadUInt32(group, 4) != 1 || p.ReadUInt32(group, 8) != span) return false;
        var arenaBytes = p.ReadUInt32(group, 12);
        var bytes = p.ReadUInt32(group, 52);
        if (bytes < 512 || bytes > 32768 || arenaBytes < bytes + 40 ||
            arenaBytes > (uint.MaxValue - TrstGroupHeaderBytes) / 2 ||
            span != TrstGroupHeaderBytes + arenaBytes * 2) return false;
        var first = group.Raw + TrstGroupHeaderBytes;
        if ((arena.Raw != first && arena.Raw != first + arenaBytes) ||
            p.ReadUInt32(group, 16) != arena.Raw ||
            p.ReadUInt32(arena, 8) != (bytes | 0x80000000 | AmigaSmartFileSystemRedoArena.Deferred) ||
            p.ReadUInt32(arena, 12) != group.Raw) return false;
        var mask = p.ReadUInt32(arena, 16);
        if (mask == 0 || mask == uint.MaxValue || (mask & (mask + 1)) != 0 ||
            mask + 1 > (arenaBytes - AmigaSmartFileSystemRedoArena.HeaderBytes) / 4) return false;
        var prefix = AmigaSmartFileSystemRedoArena.HeaderBytes + (mask + 1) * 4;
        var capacity = p.ReadUInt32(arena, 4); var slots = p.ReadUInt32(arena);
        var available = arenaBytes - prefix;
        if (capacity == 0 || capacity > mask || capacity > (mask + 1) - ((mask + 1) >> 2) ||
            slots == 0 || slots > capacity || p.ReadUInt32(arena, 20) != arena.Raw + prefix) return false;
        // The two-arena span bounds available below 2 GiB. The first 512-byte
        // bound therefore makes the 520-byte shift/add product overflow-safe.
        if (bytes == 512)
        {
            if (capacity > (available >> 9) || (capacity << 9) + (capacity << 3) > available) return false;
        }
        else if (capacity > available / (bytes + AmigaSmartFileSystemRedoArena.SlotHeaderBytes)) return false;
        var count = p.ReadUInt32(group, TrstDeferredCount); var limit = p.ReadUInt32(group, TrstDeferredLimit);
        if (count == 0 || count > limit || limit > ushort.MaxValue ||
            p.ReadUInt32(group, TrstDeferredInverse) != ~count ||
            p.ReadUInt32(group, TrstDeferredSeal) != TrstRunSeal(ref p, group, arena)) return false;
        var position = p.ReadUInt32(group, TrstGroupContinuation + 4);
        var fileLimit = p.ReadUInt32(group, TrstDeferredFileLimit);
        // count <= 65535 and bytes <= 32768 above prove this product fits.
        var acceptedBytes = bytes == 512 ? count << 9 : count * bytes;
        return position <= fileLimit && acceptedBytes <= fileLimit - position;
    }

    private static bool MaterializeTrstGroup<T>(ref T p, APTR group, APTR arena)
        where T : struct, IAmigaGuestMemory
        => p.ReadUInt32(group, TrstDeferredCount) != 0 || p.ReadUInt32(group, TrstDeferredInverse) != 0
            ? MaterializeTrstRun(ref p, arena, group) : AmigaSmartFileSystemRedoArena.Materialize(ref p, arena);

    // Memory-only: all validation precedes the bounded, no-fail image edits.
    // Called by every arena read/edit/clone/store boundary and before commit.
    internal static bool MaterializeTrstRun<T>(ref T p, APTR arena, APTR group)
        where T : struct, IAmigaGuestMemory
    {
        if (!TrstRunValid(ref p, group, arena)) return false;
        var bytes = p.ReadUInt32(group, 52); var count = p.ReadUInt32(group, TrstDeferredCount);
        var hint = APTR.FromPointer(group.Raw + TrstGroupContinuation);
        var proof = APTR.FromPointer(group.Raw + TrstGroupProof);
        var tail = p.ReadUInt32(hint, 8); var destination = p.ReadUInt32(hint, 16) + p.ReadUInt32(hint, 20);
        if (p.ReadUInt32(proof) != arena.Raw || p.ReadUInt32(hint, 44) != arena.Raw ||
            tail < p.ReadUInt32(proof, 12) || destination < p.ReadUInt32(proof, 16) ||
            count >= p.ReadUInt32(hint, 12) || p.ReadUInt32(hint, 20) > ushort.MaxValue - count) return false;
        var progress = tail - p.ReadUInt32(proof, 12);
        var limit = p.ReadUInt32(proof, 20);
        if (progress != destination - p.ReadUInt32(proof, 16) || progress > limit || count > limit - progress) return false;
        var leaf = AmigaSmartFileSystemRedoArena.OwnedMetadataAtRaw(ref p, arena,
            p.ReadUInt32(proof, 24), p.ReadUInt32(hint, 28), TreeId);
        if (leaf.IsNull || !TrstContinuationLeaf(ref p, bytes, hint, leaf, proof)) return false;
        var oldBitmap = AmigaSmartFileSystemRedoArena.OwnedMetadataAtRaw(ref p, arena,
            p.ReadUInt32(proof, 28), p.ReadUInt32(proof, 56), BitmapId);
        var newBitmap = p.ReadUInt32(proof, 56) == p.ReadUInt32(proof, 60) ? oldBitmap :
            AmigaSmartFileSystemRedoArena.OwnedMetadataAtRaw(ref p, arena,
                p.ReadUInt32(proof, 32), p.ReadUInt32(proof, 60), BitmapId);
        if (oldBitmap.IsNull || newBitmap.IsNull) return false;
        var oldAt = 12 + (int)((p.ReadUInt32(proof, 64) + progress) >> 5) * 4;
        var newAt = 12 + (int)((p.ReadUInt32(proof, 68) + progress) >> 5) * 4;
        if (!TrstBitmapRange(ref p, oldBitmap, oldAt, tail & 31, count, false) ||
            !TrstBitmapRange(ref p, newBitmap, newAt, destination & 31, count, true)) return false;
        p.WriteUInt32(arena, 8, bytes | 0x80000000); p.WriteUInt32(arena, 12, 0);
        ApplyTrstContinuation(ref p, hint, leaf, oldBitmap, newBitmap, count, oldAt, newAt,
            bytes == 512 ? count << 9 : count * bytes);
        for (var at = TrstDeferredCount; at <= TrstDeferredSeal; at += 4) p.WriteUInt32(group, at, 0);
        return true;
    }

}
