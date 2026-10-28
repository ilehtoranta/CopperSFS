using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    /// <summary>
    /// Optional zeroed, mount-owned RAM shared by EVERY writer/allocator on a
    /// volume. Eight sequential overwrite reservations; no persistent allocation
    /// for the unused tails. Keep separate from all request and scratch buffers.
    /// Release a node on close or a discontinuous seek; zero releases all nodes.
    /// </summary>
    public const uint OverwriteReservationBytes = 260;
    private const int ReservationSlots = 8, ReservationStride = 32;
    private const int ReservationNode = 0, ReservationFirst = 4, ReservationLeft = 8,
        ReservationNext = 12, ReservationSize = 16, ReservationOrigin = 20;

    private static bool HasOverwriteReservations<T>(ref T p, ref Work w)
        where T : struct, IAmigaFileSystemPlatform => w.Reservations.IsNotNull &&
        p.IsMapped(w.Reservations, OverwriteReservationBytes) && p.ReadUInt32(w.Reservations, 0) != 0;

    public static void ReleaseOverwriteReservation<T>(ref T p, ref Work w, uint node = 0)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (!HasOverwriteReservations(ref p, ref w)) return;
        if (node == 0) { p.Clear(w.Reservations, OverwriteReservationBytes); return; }
        for (var i = 0; i < ReservationSlots; i++)
        {
            var slot = APTR.FromPointer(w.Reservations.Raw + 4 + (uint)(i * ReservationStride));
            if (p.ReadUInt32(slot, ReservationNode) != node) continue;
            p.Clear(slot, ReservationStride);
            p.WriteUInt32(w.Reservations, 0, p.ReadUInt32(w.Reservations, 0) - 1);
        }
    }

    public static bool HasOverwriteReservation<T>(ref T p, ref Work w, uint node)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (!HasOverwriteReservations(ref p, ref w)) return false;
        for (var i = 0; i < ReservationSlots; i++)
            if (p.ReadUInt32(w.Reservations, 4 + i * ReservationStride) == node) return true;
        return false;
    }

    private static bool OverwriteReserved<T>(ref T p, ref Work w, uint block)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (!HasOverwriteReservations(ref p, ref w)) return false;
        for (var i = 0; i < ReservationSlots; i++)
        {
            var slot = APTR.FromPointer(w.Reservations.Raw + 4 + (uint)(i * ReservationStride));
            if (p.ReadUInt32(slot, ReservationNode) == 0) continue;
            var first = p.ReadUInt32(slot, ReservationFirst);
            if (block >= first && block - first < p.ReadUInt32(slot, ReservationLeft)) return true;
        }
        return false;
    }

    // Mask only search scratch, never the actual allocation bitmap.
    private static void MaskOverwriteReservations<T>(ref T p, ref Work w, uint mapIndex, APTR except)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (!HasOverwriteReservations(ref p, ref w)) return;
        var bits = (p.Geometry.BlockBytes - 12) * 8;
        var mapStart = mapIndex * bits;
        for (var i = 0; i < ReservationSlots; i++)
        {
            var slot = APTR.FromPointer(w.Reservations.Raw + 4 + (uint)(i * ReservationStride));
            if (slot == except || p.ReadUInt32(slot, ReservationNode) == 0) continue;
            var start = p.ReadUInt32(slot, ReservationFirst);
            var end = start + p.ReadUInt32(slot, ReservationLeft);
            if (start < mapStart) start = mapStart;
            if (end > mapStart + bits) end = mapStart + bits;
            while (start < end)
            {
                var bit = start - mapStart;
                var take = 32 - bit % 32;
                if (take > end - start) take = end - start;
                var mask = (uint.MaxValue >> (int)(bit % 32)) & (uint.MaxValue << (int)(32 - bit % 32 - take));
                var at = 12 + (int)(bit / 32) * 4;
                p.WriteUInt32(w.Bitmap, at, p.ReadUInt32(w.Bitmap, at) & ~mask);
                start += take;
            }
        }
    }

    private static bool PlanOverwrite<T>(ref T p, ref Work w, ref Request r, uint size, uint first,
        uint capacity, bool enabled, out APTR reservation)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        reservation = APTR.Null;
        if (w.Reservations.IsNull || !p.IsMapped(w.Reservations, OverwriteReservationBytes)) return true;
        var bytes = p.Geometry.BlockBytes;
        APTR vacant = APTR.Null;
        uint held = 0;
        for (var i = 0; i < ReservationSlots; i++)
        {
            var slot = APTR.FromPointer(w.Reservations.Raw + 4 + (uint)(i * ReservationStride));
            var node = p.ReadUInt32(slot, ReservationNode);
            if (node == r.Node)
            {
                if (enabled && r.Position == p.ReadUInt32(slot, ReservationNext) * bytes &&
                    size == p.ReadUInt32(slot, ReservationSize) && first == p.ReadUInt32(slot, ReservationOrigin) &&
                    r.Length <= size - r.Position && (r.Length % bytes == 0 || r.Position + r.Length == size))
                { reservation = slot; return true; }
                ReleaseOverwriteReservation(ref p, ref w, r.Node);
                node = 0;
            }
            if (node == 0) { if (vacant.IsNull) vacant = slot; }
            else held += p.ReadUInt32(slot, ReservationLeft);
        }
        var blocks = size / bytes + (size % bytes == 0 ? 0u : 1u);
        if (!enabled || vacant.IsNull || r.Position != 0 || r.Length < 65536 || r.Length > size ||
            capacity * bytes < 65536 || blocks > ushort.MaxValue ||
            (r.Length % bytes != 0 && r.Length != size)) return true;
        // Leave room for journal and metadata allocation. A failed lookahead
        // must not evict an existing stream merely to obtain a larger run.
        if (!Read(ref p, ref w, w.RootContainer, ObjectId, w.Meta)) return false;
        var free = p.ReadUInt32(w.Meta, (int)bytes - 28);
        if ((ulong)blocks + held + 128 > free) return true;
        if (!FindFreeRun(ref p, ref w, 0, blocks, blocks, false, out var destination, out var found, false)) return false;
        if (found == 0) return true;
        p.WriteUInt32(vacant, ReservationNode, r.Node);
        p.WriteUInt32(vacant, ReservationFirst, destination);
        p.WriteUInt32(vacant, ReservationLeft, blocks);
        p.WriteUInt32(vacant, ReservationNext, 0);
        p.WriteUInt32(vacant, ReservationSize, size);
        p.WriteUInt32(vacant, ReservationOrigin, destination);
        p.WriteUInt32(w.Reservations, 0, p.ReadUInt32(w.Reservations, 0) + 1);
        reservation = vacant;
        return true;
    }

    private static bool ReservedOverwriteRun<T>(ref T p, ref Work w, APTR slot, uint node,
        uint index, uint count, out uint destination)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        destination = 0;
        if (slot.IsNull || p.ReadUInt32(slot, ReservationNode) != node ||
            p.ReadUInt32(slot, ReservationNext) != index || count > p.ReadUInt32(slot, ReservationLeft)) return true;
        var first = p.ReadUInt32(slot, ReservationFirst);
        var bits = (p.Geometry.BlockBytes - 12) * 8;
        var loaded = uint.MaxValue;
        for (var i = 0u; i < count; i++)
        {
            var block = first + i;
            if (block / bits != loaded)
            {
                loaded = block / bits;
                if (!FreeSearchMap(ref p, ref w, loaded, slot)) return false;
            }
            var bit = block % bits;
            if ((p.ReadUInt32(w.Bitmap, 12 + (int)(bit / 32) * 4) & (1u << (int)(31 - bit % 32))) == 0)
            { ReleaseOverwriteReservation(ref p, ref w, node); return true; }
        }
        destination = first;
        return true;
    }

    private static void AdvanceOverwriteReservation<T>(ref T p, ref Work w, APTR slot, uint node, uint count)
        where T : struct, IAmigaFileSystemPlatform
    {
        // Space pressure can evict reservations while metadata is being logged.
        if (slot.IsNull || p.ReadUInt32(slot, ReservationNode) != node) return;
        var left = p.ReadUInt32(slot, ReservationLeft);
        if (count >= left) { ReleaseOverwriteReservation(ref p, ref w, node); return; }
        p.WriteUInt32(slot, ReservationFirst, p.ReadUInt32(slot, ReservationFirst) + count);
        p.WriteUInt32(slot, ReservationNext, p.ReadUInt32(slot, ReservationNext) + count);
        p.WriteUInt32(slot, ReservationLeft, left - count);
    }
}
