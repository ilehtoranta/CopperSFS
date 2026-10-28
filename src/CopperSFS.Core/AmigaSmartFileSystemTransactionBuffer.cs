using Amiga;
using BufferLayout = CopperSFS.AmigaSmartFileSystemTransactionBufferLayout;
using BufferHeader = CopperSFS.AmigaSmartFileSystemTransactionBufferLayout.Header;
using BufferEntry = CopperSFS.AmigaSmartFileSystemTransactionBufferLayout.Entry;

namespace CopperSFS;

/// <summary>
/// Caller-owned staging for one SFS undo transaction. ReadBlock must consult
/// this buffer before physical storage; WriteBlock must always bypass staging
/// (a separate physical queue may combine writes within a flush interval).
/// The writer publishes durable undo records before draining staged blocks.
/// </summary>
public static class AmigaSmartFileSystemTransactionBuffer
{
    public const uint MaximumCapacity = 32;

    public static uint StorageBytes(uint blockBytes, uint capacity)
        => BufferLayout.HeaderBytes + capacity * (blockBytes + BufferLayout.EntryHeaderBytes);

    public static bool Initialize<T>(ref T p, APTR buffer, uint blockBytes, uint capacity)
        where T : struct, IAmigaGuestMemory
    {
        if (buffer.IsNull || blockBytes == 0 || (blockBytes & 3) != 0 ||
            capacity == 0 || capacity > MaximumCapacity ||
            blockBytes > (uint.MaxValue - BufferLayout.HeaderBytes) / capacity - BufferLayout.EntryHeaderBytes ||
            !p.IsMapped(buffer, StorageBytes(blockBytes, capacity))) return false;
        p.WriteUInt32(buffer, BufferHeader.Count, 0);
        p.WriteUInt32(buffer, BufferHeader.Capacity, capacity);
        return true;
    }

    internal static bool Valid<T>(ref T p, APTR buffer, uint blockBytes)
        where T : struct, IAmigaGuestMemory
    {
        if (blockBytes == 0 || (blockBytes & 3) != 0 || !p.IsMapped(buffer, BufferLayout.HeaderBytes)) return false;
        var capacity = p.ReadUInt32(buffer, BufferHeader.Capacity);
        return capacity != 0 && capacity <= MaximumCapacity &&
            blockBytes <= (uint.MaxValue - BufferLayout.HeaderBytes) / capacity - BufferLayout.EntryHeaderBytes &&
            p.IsMapped(buffer, StorageBytes(blockBytes, capacity));
    }

    public static void Discard<T>(ref T p, APTR buffer) where T : struct, IAmigaGuestMemory
    { if (buffer.IsNotNull) p.WriteUInt32(buffer, BufferHeader.Count, 0); }

    internal static uint Count<T>(ref T p, APTR buffer) where T : struct, IAmigaGuestMemory
        => p.ReadUInt32(buffer, BufferHeader.Count);

    internal static APTR Slot(APTR buffer, uint blockBytes, uint index)
        => APTR.FromPointer(buffer.Raw + BufferLayout.HeaderBytes + index * (blockBytes + BufferLayout.EntryHeaderBytes));

    public static bool TryRead<T>(ref T p, APTR buffer, uint blockBytes,
        uint block, APTR destination, out bool found) where T : struct, IAmigaGuestMemory
    {
        found = false;
        if (buffer.IsNull) return true;
        var count = p.ReadUInt32(buffer, BufferHeader.Count);
        if (count > MaximumCapacity || count > p.ReadUInt32(buffer, BufferHeader.Capacity)) return false;
        var slot = APTR.FromPointer(buffer.Raw + BufferLayout.HeaderBytes);
        for (var index = 0u; index < count; index++, slot = APTR.FromPointer(slot.Raw + blockBytes + BufferLayout.EntryHeaderBytes))
        {
            if (p.ReadUInt32(slot, BufferEntry.Block) != block) continue;
            p.Copy(APTR.FromPointer(slot.Raw + BufferEntry.Image), destination, blockBytes);
            found = true;
            break;
        }
        return true;
    }

    internal static bool HasRoom<T>(ref T p, APTR buffer, uint blockBytes, uint block)
        where T : struct, IAmigaGuestMemory
    {
        var count = p.ReadUInt32(buffer, BufferHeader.Count);
        if (count < p.ReadUInt32(buffer, BufferHeader.Capacity)) return true;
        var slot = APTR.FromPointer(buffer.Raw + BufferLayout.HeaderBytes);
        for (var index = 0u; index < count; index++, slot = APTR.FromPointer(slot.Raw + blockBytes + BufferLayout.EntryHeaderBytes))
            if (p.ReadUInt32(slot, BufferEntry.Block) == block) return true;
        return false;
    }

    internal static void Store<T>(ref T p, APTR buffer, uint blockBytes, uint block, APTR source)
        where T : struct, IAmigaGuestMemory
    {
        var count = p.ReadUInt32(buffer, BufferHeader.Count);
        var index = 0u;
        var slot = APTR.FromPointer(buffer.Raw + BufferLayout.HeaderBytes);
        for (; index < count; index++, slot = APTR.FromPointer(slot.Raw + blockBytes + BufferLayout.EntryHeaderBytes))
            if (p.ReadUInt32(slot, BufferEntry.Block) == block) break;
        p.WriteUInt32(slot, BufferEntry.Block, block);
        p.Copy(source, APTR.FromPointer(slot.Raw + BufferEntry.Image), blockBytes);
        if (index == count) p.WriteUInt32(buffer, BufferHeader.Count, count + 1);
    }
}
