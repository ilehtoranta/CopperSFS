using Amiga;
using QueueLayout = CopperSFS.AmigaFileSystemWriteQueueLayout;
using QueueHeader = CopperSFS.AmigaFileSystemWriteQueueLayout.Header;
using QueueEntry = CopperSFS.AmigaFileSystemWriteQueueLayout.Entry;

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

    public static uint StorageBytes(uint blockBytes, uint capacity)
        => QueueLayout.HeaderBytes + capacity * (QueueLayout.IndexBytesPerEntry + blockBytes) + blockBytes;

    public static bool Initialize<T>(ref T p, APTR queue, uint blockBytes, uint capacity)
        where T : struct, IAmigaGuestMemory
    {
        var header = new AmigaFileSystemWriteQueueHeaderView<T>(ref p, queue);
        if (queue.IsNull || blockBytes == 0 || (blockBytes & 3) != 0 ||
            capacity == 0 || capacity > MaximumCapacity ||
            blockBytes > (uint.MaxValue - QueueLayout.HeaderBytes - capacity * QueueLayout.IndexBytesPerEntry) / (capacity + 1) ||
            !p.IsMapped(queue, StorageBytes(blockBytes, capacity))) return false;
        p.Clear(queue, QueueLayout.HeaderBytes);
        header.Capacity = capacity; header.BlockBytes = blockBytes;
        header.Order = APTR.FromPointer(queue.Raw + QueueLayout.HeaderBytes + capacity * QueueLayout.EntryBytes);
        header.Data = APTR.FromPointer(queue.Raw + QueueLayout.HeaderBytes + capacity * QueueLayout.IndexBytesPerEntry);
        return true;
    }

    public static uint Count<T>(ref T p, APTR queue) where T : struct, IAmigaGuestMemory
    {
        var header = new AmigaFileSystemWriteQueueHeaderView<T>(ref p, queue);
        return queue.IsNull ? 0 : header.Count;
    }

    public static void Discard<T>(ref T p, APTR queue) where T : struct, IAmigaGuestMemory
    {
        var header = new AmigaFileSystemWriteQueueHeaderView<T>(ref p, queue);
        header.Count = 0;
        p.Clear(APTR.FromPointer(queue.Raw + QueueHeader.HashBuckets), QueueLayout.HashBytes);
    }

    private static APTR Data<T>(ref T p, APTR queue, uint index) where T : struct, IAmigaGuestMemory
    {
        var header = new AmigaFileSystemWriteQueueHeaderView<T>(ref p, queue);
        return APTR.FromPointer(header.Data.Raw + index * header.BlockBytes);
    }

    private static APTR Find<T>(ref T p, APTR queue, uint block) where T : struct, IAmigaGuestMemory
    {
        var link = p.ReadUInt32(queue, QueueHeader.HashBuckets + (int)(block & QueueLayout.HashBucketMask) * QueueLayout.PointerBytes);
        while (link != 0)
        {
            var entry = APTR.FromPointer(link);
            if (p.ReadUInt32(entry, QueueEntry.Block) == block) return entry;
            link = p.ReadUInt32(entry, QueueEntry.HashNext);
        }
        return APTR.Null;
    }

    /// <summary>Read-only owned image. Sort, Discard or the next queue mutation
    /// expires the view. Copy it to caller storage before retaining or editing it.</summary>
    public static APTR ReadView<T>(ref T p, APTR queue, uint block) where T : struct, IAmigaGuestMemory
    {
        var header = new AmigaFileSystemWriteQueueHeaderView<T>(ref p, queue);
        if (queue.IsNull || header.Count == 0 || block < header.MinimumBlock || block > header.MaximumBlock) return APTR.Null;
        var entry = Find(ref p, queue, block);
        return entry.IsNull ? APTR.Null : APTR.FromPointer(p.ReadUInt32(entry, QueueEntry.Image));
    }

    /// <summary>Validate an owned image once, invalidating verification on every
    /// replacement. The read-only view has the same lifetime as ReadView.</summary>
    public static APTR ReadMetadataView<T>(ref T p, APTR queue, uint block, uint id, out bool found)
        where T : struct, IAmigaFileSystemPlatform
    {
        var header = new AmigaFileSystemWriteQueueHeaderView<T>(ref p, queue);
        found = false;
        if (queue.IsNull || header.Count == 0 || block < header.MinimumBlock || block > header.MaximumBlock) return APTR.Null;
        var entry = Find(ref p, queue, block);
        if (entry.IsNull) return APTR.Null;
        found = true;
        var image = APTR.FromPointer(p.ReadUInt32(entry, QueueEntry.Image));
        if (id != 0 && p.ReadUInt32(entry, QueueEntry.VerifiedId) != id)
        {
            if (!AmigaFileSystemBlockIo.ValidateMetadata(ref p, block, id, image, header.BlockBytes)) return APTR.Null;
            p.WriteUInt32(entry, QueueEntry.VerifiedId, id);
        }
        return image;
    }

    public static bool Intersects<T>(ref T p, APTR queue, uint block, uint count) where T : struct, IAmigaGuestMemory
    {
        var header = new AmigaFileSystemWriteQueueHeaderView<T>(ref p, queue);
        if (queue.IsNull || header.Count == 0 || count == 0 || block > header.MaximumBlock ||
            (block < header.MinimumBlock && count <= header.MinimumBlock - block)) return false;
        var entries = header.Count;
        for (var i = 0u; i < entries; i++)
        {
            var key = p.ReadUInt32(queue, (int)(QueueLayout.HeaderBytes + i * QueueLayout.EntryBytes));
            if (key >= block && key - block < count) return true;
        }
        return false;
    }

    private static APTR Add<T>(ref T p, APTR queue, uint index, uint block) where T : struct, IAmigaGuestMemory
    {
        var header = new AmigaFileSystemWriteQueueHeaderView<T>(ref p, queue);
        var bucket = QueueHeader.HashBuckets + (int)(block & QueueLayout.HashBucketMask) * QueueLayout.PointerBytes;
        var entry = APTR.FromPointer(queue.Raw + QueueLayout.HeaderBytes + index * QueueLayout.EntryBytes);
        p.WriteUInt32(entry, QueueEntry.Block, block); p.WriteUInt32(entry, QueueEntry.HashNext, p.ReadUInt32(queue, bucket));
        p.WriteUInt32(entry, QueueEntry.Image, Data(ref p, queue, index).Raw); p.WriteUInt32(entry, QueueEntry.VerifiedId, 0);
        p.WriteUInt32(queue, bucket, entry.Raw);
        // Inverse map from a payload slot to its stable hash-table entry.
        var owners = header.Order.Raw + header.Capacity * QueueLayout.PointerBytes;
        p.WriteUInt32(APTR.FromPointer(owners), (int)index * QueueLayout.PointerBytes, entry.Raw);
        return entry;
    }

    /// <summary>Copies the complete range, or returns false without changing the queue.</summary>
    public static bool TryStore<T>(ref T p, APTR queue, uint block, uint count, APTR source)
        where T : struct, IAmigaGuestMemory
    {
        var header = new AmigaFileSystemWriteQueueHeaderView<T>(ref p, queue);
        var used = header.Count; var capacity = header.Capacity;
        if (count == 0 || count > capacity || count - 1 > uint.MaxValue - block) return false;
        var bytes = header.BlockBytes;
        var separate = used == 0 || block > header.MaximumBlock || block + count - 1 < header.MinimumBlock;
        if (separate)
        {
            if (count > capacity - used) return false;
            // Streaming callers retain one bulk copy rather than one per sector.
            p.Copy(source, Data(ref p, queue, used), count * bytes);
            for (var i = 0u; i < count; i++) Add(ref p, queue, used + i, block + i);
            header.Count = used + count;
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
                p.WriteUInt32(entry, QueueEntry.VerifiedId, 0);
                p.Copy(APTR.FromPointer(source.Raw + i * bytes), APTR.FromPointer(p.ReadUInt32(entry, QueueEntry.Image)), bytes);
            }
            header.Count = next;
        }
        if (used == 0 || block < header.MinimumBlock) header.MinimumBlock = block;
        if (used == 0 || block + count - 1 > header.MaximumBlock) header.MaximumBlock = block + count - 1;
        return true;
    }

    public static void Sort<T>(ref T p, APTR queue) where T : struct, IAmigaGuestMemory
    {
        var header = new AmigaFileSystemWriteQueueHeaderView<T>(ref p, queue);
        var order = APTR.FromPointer(header.Order.Raw);
        var count = header.Count;
        var ordered = true; var previousKey = 0u;
        for (var index = 0u; index < count; index++)
        {
            var entry = queue.Raw + QueueLayout.HeaderBytes + index * QueueLayout.EntryBytes;
            var key = p.ReadUInt32(APTR.FromPointer(entry), QueueEntry.Block);
            if (index != 0 && key < previousKey) ordered = false;
            previousKey = key;
            p.WriteUInt32(order, (int)index * QueueLayout.PointerBytes, entry);
        }
        // Shell insertion keeps the common ordered case cheap without quadratic
        // shifting when low-numbered journal/home blocks arrive after streaming data.
        for (var gap = ordered ? 0u : count >> 1; gap != 0; gap >>= 1)
        for (var index = gap; index < count; index++)
        {
            var selected = p.ReadUInt32(order, (int)index * QueueLayout.PointerBytes);
            var key = p.ReadUInt32(APTR.FromPointer(selected), QueueEntry.Block); var at = index;
            while (at >= gap)
            {
                var previous = p.ReadUInt32(order, (int)(at - gap) * QueueLayout.PointerBytes);
                if (p.ReadUInt32(APTR.FromPointer(previous), QueueEntry.Block) < key) break;
                p.WriteUInt32(order, (int)at * QueueLayout.PointerBytes, previous); at -= gap;
            }
            p.WriteUInt32(order, (int)at * QueueLayout.PointerBytes, selected);
        }
        // Permute payloads into physical order using one block of scratch.
        // Every image survives, including already transferred prefixes after a
        // failed drain. Stable entries retain their hash links and verification.
        var bytes = header.BlockBytes; var capacity = header.Capacity;
        var data = header.Data.Raw;
        var owners = APTR.FromPointer(order.Raw + capacity * QueueLayout.PointerBytes);
        var scratch = APTR.FromPointer(data + capacity * bytes);
        var destination = APTR.FromPointer(data);
        for (var index = 0u; index < count; index++, destination = APTR.FromPointer(destination.Raw + bytes))
        {
            var displaced = p.ReadUInt32(owners, (int)index * QueueLayout.PointerBytes);
            if (displaced == p.ReadUInt32(order, (int)index * QueueLayout.PointerBytes)) continue;
            p.Copy(destination, scratch, bytes);
            var at = index; var target = destination;
            while (true)
            {
                var entry = APTR.FromPointer(p.ReadUInt32(order, (int)at * QueueLayout.PointerBytes));
                var source = APTR.FromPointer(p.ReadUInt32(entry, QueueEntry.Image));
                p.Copy(entry.Raw == displaced ? scratch : source, target, bytes);
                p.WriteUInt32(entry, QueueEntry.Image, target.Raw);
                p.WriteUInt32(owners, (int)at * QueueLayout.PointerBytes, entry.Raw);
                if (entry.Raw == displaced) break;
                at = (source.Raw - data) / bytes; target = source;
            }
        }
    }

    /// <summary>Returns one adjacent run in sorted order; never fills unwritten holes.</summary>
    public static APTR Run<T>(ref T p, APTR queue, uint ordinal, out uint block, out uint count)
        where T : struct, IAmigaGuestMemory
    {
        var header = new AmigaFileSystemWriteQueueHeaderView<T>(ref p, queue);
        var bytes = header.BlockBytes;
        var order = APTR.FromPointer(header.Order.Raw);
        var first = APTR.FromPointer(p.ReadUInt32(order, (int)ordinal * QueueLayout.PointerBytes));
        block = p.ReadUInt32(first, QueueEntry.Block); count = 1;
        var used = header.Count;
        while (ordinal + count < used)
        {
            var entry = APTR.FromPointer(p.ReadUInt32(order, (int)(ordinal + count) * QueueLayout.PointerBytes));
            if (p.ReadUInt32(entry, QueueEntry.Block) - block != count) break;
            count++;
        }
        return APTR.FromPointer(header.Data.Raw + ordinal * bytes);
    }
}
