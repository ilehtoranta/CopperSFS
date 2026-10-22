using Amiga;

namespace CopperSFS;

/// <summary>
/// Optional, caller-owned physical write queue. The platform must consult it
/// after logical transaction overlays and drain it before every device flush.
/// Sorting is confined to a single barrier interval. A failed drain retains
/// every image, including its completed prefix, for recovery and retry.
/// </summary>
public static class AmigaFileSystemWriteCombiner
{
    public const uint MaximumCapacity = 128;
    private const uint HeaderBytes = 288;

    public static uint StorageBytes(uint blockBytes, uint capacity)
        => HeaderBytes + capacity * (24 + blockBytes) + blockBytes;

    public static bool Initialize<T>(ref T p, APTR queue, uint blockBytes, uint capacity)
        where T : struct, IAmigaGuestMemory
    {
        if (queue.IsNull || blockBytes == 0 || (blockBytes & 3) != 0 ||
            capacity == 0 || capacity > MaximumCapacity ||
            blockBytes > (uint.MaxValue - HeaderBytes - capacity * 24) / (capacity + 1) ||
            !p.IsMapped(queue, StorageBytes(blockBytes, capacity))) return false;
        p.Clear(queue, HeaderBytes);
        p.WriteUInt32(queue, 4, capacity); p.WriteUInt32(queue, 8, blockBytes);
        p.WriteUInt32(queue, 24, queue.Raw + HeaderBytes + capacity * 16);
        p.WriteUInt32(queue, 28, queue.Raw + HeaderBytes + capacity * 24);
        return true;
    }

    public static uint Count<T>(ref T p, APTR queue) where T : struct, IAmigaGuestMemory
        => queue.IsNull ? 0 : p.ReadUInt32(queue);

    public static void Discard<T>(ref T p, APTR queue) where T : struct, IAmigaGuestMemory
    {
        p.WriteUInt32(queue, 0, 0);
        p.Clear(APTR.FromPointer(queue.Raw + 32), 256);
    }

    private static APTR Data<T>(ref T p, APTR queue, uint index) where T : struct, IAmigaGuestMemory
        => APTR.FromPointer(p.ReadUInt32(queue, 28) + index * p.ReadUInt32(queue, 8));

    private static APTR Find<T>(ref T p, APTR queue, uint block) where T : struct, IAmigaGuestMemory
    {
        var link = p.ReadUInt32(queue, 32 + (int)(block & 63) * 4);
        while (link != 0)
        {
            var entry = APTR.FromPointer(link);
            if (p.ReadUInt32(entry) == block) return entry;
            link = p.ReadUInt32(entry, 4);
        }
        return APTR.Null;
    }

    /// <summary>Read-only owned image. Sort, Discard or the next queue mutation
    /// expires the view. Copy it to caller storage before retaining or editing it.</summary>
    public static APTR ReadView<T>(ref T p, APTR queue, uint block) where T : struct, IAmigaGuestMemory
    {
        if (queue.IsNull || p.ReadUInt32(queue) == 0 || block < p.ReadUInt32(queue, 12) || block > p.ReadUInt32(queue, 16)) return APTR.Null;
        var entry = Find(ref p, queue, block);
        return entry.IsNull ? APTR.Null : APTR.FromPointer(p.ReadUInt32(entry, 8));
    }

    /// <summary>Validate an owned image once, invalidating verification on every
    /// replacement. The read-only view has the same lifetime as ReadView.</summary>
    public static APTR ReadMetadataView<T>(ref T p, APTR queue, uint block, uint id, out bool found)
        where T : struct, IAmigaFileSystemPlatform
    {
        found = false;
        if (queue.IsNull || p.ReadUInt32(queue) == 0 || block < p.ReadUInt32(queue, 12) || block > p.ReadUInt32(queue, 16)) return APTR.Null;
        var entry = Find(ref p, queue, block);
        if (entry.IsNull) return APTR.Null;
        found = true;
        var image = APTR.FromPointer(p.ReadUInt32(entry, 8));
        if (id != 0 && p.ReadUInt32(entry, 12) != id)
        {
            if (!AmigaFileSystemBlockIo.ValidateMetadata(ref p, block, id, image, p.ReadUInt32(queue, 8))) return APTR.Null;
            p.WriteUInt32(entry, 12, id);
        }
        return image;
    }

    public static bool Intersects<T>(ref T p, APTR queue, uint block, uint count) where T : struct, IAmigaGuestMemory
    {
        if (queue.IsNull || p.ReadUInt32(queue) == 0 || count == 0 || block > p.ReadUInt32(queue, 16) ||
            (block < p.ReadUInt32(queue, 12) && count <= p.ReadUInt32(queue, 12) - block)) return false;
        var entries = p.ReadUInt32(queue);
        for (var i = 0u; i < entries; i++)
        {
            var key = p.ReadUInt32(queue, (int)(HeaderBytes + i * 16));
            if (key >= block && key - block < count) return true;
        }
        return false;
    }

    private static APTR Add<T>(ref T p, APTR queue, uint index, uint block) where T : struct, IAmigaGuestMemory
    {
        var bucket = 32 + (int)(block & 63) * 4;
        var entry = APTR.FromPointer(queue.Raw + HeaderBytes + index * 16);
        p.WriteUInt32(entry, 0, block); p.WriteUInt32(entry, 4, p.ReadUInt32(queue, bucket));
        p.WriteUInt32(entry, 8, Data(ref p, queue, index).Raw); p.WriteUInt32(entry, 12, 0);
        p.WriteUInt32(queue, bucket, entry.Raw);
        // Inverse map from a payload slot to its stable hash-table entry.
        var owners = p.ReadUInt32(queue, 24) + p.ReadUInt32(queue, 4) * 4;
        p.WriteUInt32(APTR.FromPointer(owners), (int)index * 4, entry.Raw);
        return entry;
    }

    /// <summary>Copies the complete range, or returns false without changing the queue.</summary>
    public static bool TryStore<T>(ref T p, APTR queue, uint block, uint count, APTR source)
        where T : struct, IAmigaGuestMemory
    {
        var used = p.ReadUInt32(queue); var capacity = p.ReadUInt32(queue, 4);
        if (count == 0 || count > capacity || count - 1 > uint.MaxValue - block) return false;
        var bytes = p.ReadUInt32(queue, 8);
        var separate = used == 0 || block > p.ReadUInt32(queue, 16) || block + count - 1 < p.ReadUInt32(queue, 12);
        if (separate)
        {
            if (count > capacity - used) return false;
            // Streaming callers retain one bulk copy rather than one per sector.
            p.Copy(source, Data(ref p, queue, used), count * bytes);
            for (var i = 0u; i < count; i++) Add(ref p, queue, used + i, block + i);
            p.WriteUInt32(queue, 0, used + count);
        }
        else
        {
            var missing = 0u;
            for (var i = 0u; i < count; i++) if (Find(ref p, queue, block + i).IsNull) missing++;
            if (missing > capacity - used) return false;
            var next = used;
            for (var i = 0u; i < count; i++)
            {
                var entry = Find(ref p, queue, block + i);
                if (entry.IsNull) { entry = Add(ref p, queue, next, block + i); next++; }
                p.WriteUInt32(entry, 12, 0);
                p.Copy(APTR.FromPointer(source.Raw + i * bytes), APTR.FromPointer(p.ReadUInt32(entry, 8)), bytes);
            }
            p.WriteUInt32(queue, 0, next);
        }
        if (used == 0 || block < p.ReadUInt32(queue, 12)) p.WriteUInt32(queue, 12, block);
        if (used == 0 || block + count - 1 > p.ReadUInt32(queue, 16)) p.WriteUInt32(queue, 16, block + count - 1);
        return true;
    }

    public static void Sort<T>(ref T p, APTR queue) where T : struct, IAmigaGuestMemory
    {
        var order = APTR.FromPointer(p.ReadUInt32(queue, 24));
        var count = p.ReadUInt32(queue);
        var ordered = true; var previousKey = 0u;
        for (var index = 0u; index < count; index++)
        {
            var entry = queue.Raw + HeaderBytes + index * 16;
            var key = p.ReadUInt32(APTR.FromPointer(entry));
            if (index != 0 && key < previousKey) ordered = false;
            previousKey = key;
            p.WriteUInt32(order, (int)index * 4, entry);
        }
        // Shell insertion keeps the common ordered case cheap without quadratic
        // shifting when low-numbered journal/home blocks arrive after streaming data.
        for (var gap = ordered ? 0u : count >> 1; gap != 0; gap >>= 1)
        for (var index = gap; index < count; index++)
        {
            var selected = p.ReadUInt32(order, (int)index * 4);
            var key = p.ReadUInt32(APTR.FromPointer(selected)); var at = index;
            while (at >= gap)
            {
                var previous = p.ReadUInt32(order, (int)(at - gap) * 4);
                if (p.ReadUInt32(APTR.FromPointer(previous)) < key) break;
                p.WriteUInt32(order, (int)at * 4, previous); at -= gap;
            }
            p.WriteUInt32(order, (int)at * 4, selected);
        }
        // Permute payloads into physical order using one block of scratch.
        // Every image survives, including already transferred prefixes after a
        // failed drain. Stable entries retain their hash links and verification.
        var bytes = p.ReadUInt32(queue, 8); var capacity = p.ReadUInt32(queue, 4);
        var data = p.ReadUInt32(queue, 28);
        var owners = APTR.FromPointer(order.Raw + capacity * 4);
        var scratch = APTR.FromPointer(data + capacity * bytes);
        var destination = APTR.FromPointer(data);
        for (var index = 0u; index < count; index++, destination = APTR.FromPointer(destination.Raw + bytes))
        {
            var displaced = p.ReadUInt32(owners, (int)index * 4);
            if (displaced == p.ReadUInt32(order, (int)index * 4)) continue;
            p.Copy(destination, scratch, bytes);
            var at = index; var target = destination;
            while (true)
            {
                var entry = APTR.FromPointer(p.ReadUInt32(order, (int)at * 4));
                var source = APTR.FromPointer(p.ReadUInt32(entry, 8));
                p.Copy(entry.Raw == displaced ? scratch : source, target, bytes);
                p.WriteUInt32(entry, 8, target.Raw);
                p.WriteUInt32(owners, (int)at * 4, entry.Raw);
                if (entry.Raw == displaced) break;
                at = (source.Raw - data) / bytes; target = source;
            }
        }
    }

    /// <summary>Returns one adjacent run in sorted order; never fills unwritten holes.</summary>
    public static APTR Run<T>(ref T p, APTR queue, uint ordinal, out uint block, out uint count)
        where T : struct, IAmigaGuestMemory
    {
        var bytes = p.ReadUInt32(queue, 8);
        var order = APTR.FromPointer(p.ReadUInt32(queue, 24));
        var first = APTR.FromPointer(p.ReadUInt32(order, (int)ordinal * 4));
        block = p.ReadUInt32(first); count = 1;
        var used = p.ReadUInt32(queue);
        while (ordinal + count < used)
        {
            var entry = APTR.FromPointer(p.ReadUInt32(order, (int)(ordinal + count) * 4));
            if (p.ReadUInt32(entry) - block != count) break;
            count++;
        }
        return APTR.FromPointer(p.ReadUInt32(queue, 28) + ordinal * bytes);
    }
}
