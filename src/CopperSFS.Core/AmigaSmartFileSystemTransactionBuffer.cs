using Amiga;

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
    private const uint HeaderBytes = 8;

    public static uint StorageBytes(uint blockBytes, uint capacity)
        => HeaderBytes + capacity * (blockBytes + 4);

    public static bool Initialize<T>(ref T p, APTR buffer, uint blockBytes, uint capacity)
        where T : struct, IAmigaGuestMemory
    {
        if (buffer.IsNull || blockBytes == 0 || (blockBytes & 3) != 0 ||
            capacity == 0 || capacity > MaximumCapacity ||
            blockBytes > (uint.MaxValue - HeaderBytes) / capacity - 4 ||
            !p.IsMapped(buffer, StorageBytes(blockBytes, capacity))) return false;
        p.WriteUInt32(buffer, 0, 0);
        p.WriteUInt32(buffer, 4, capacity);
        return true;
    }

    internal static bool Valid<T>(ref T p, APTR buffer, uint blockBytes)
        where T : struct, IAmigaGuestMemory
    {
        if (blockBytes == 0 || (blockBytes & 3) != 0 || !p.IsMapped(buffer, HeaderBytes)) return false;
        var capacity = p.ReadUInt32(buffer, 4);
        return capacity != 0 && capacity <= MaximumCapacity &&
            blockBytes <= (uint.MaxValue - HeaderBytes) / capacity - 4 &&
            p.IsMapped(buffer, StorageBytes(blockBytes, capacity));
    }

    public static void Discard<T>(ref T p, APTR buffer) where T : struct, IAmigaGuestMemory
    { if (buffer.IsNotNull) p.WriteUInt32(buffer, 0, 0); }

    internal static uint Count<T>(ref T p, APTR buffer) where T : struct, IAmigaGuestMemory
        => p.ReadUInt32(buffer);

    internal static APTR Slot(APTR buffer, uint blockBytes, uint index)
        => APTR.FromPointer(buffer.Raw + HeaderBytes + index * (blockBytes + 4));

    public static bool TryRead<T>(ref T p, APTR buffer, uint blockBytes,
        uint block, APTR destination, out bool found) where T : struct, IAmigaGuestMemory
    {
        found = false;
        if (buffer.IsNull) return true;
        var count = p.ReadUInt32(buffer);
        if (count > MaximumCapacity || count > p.ReadUInt32(buffer, 4)) return false;
        var slot = APTR.FromPointer(buffer.Raw + HeaderBytes);
        for (var index = 0u; index < count; index++, slot = APTR.FromPointer(slot.Raw + blockBytes + 4))
        {
            if (p.ReadUInt32(slot) != block) continue;
            p.Copy(APTR.FromPointer(slot.Raw + 4), destination, blockBytes);
            found = true;
            break;
        }
        return true;
    }

    internal static bool HasRoom<T>(ref T p, APTR buffer, uint blockBytes, uint block)
        where T : struct, IAmigaGuestMemory
    {
        var count = p.ReadUInt32(buffer);
        if (count < p.ReadUInt32(buffer, 4)) return true;
        var slot = APTR.FromPointer(buffer.Raw + HeaderBytes);
        for (var index = 0u; index < count; index++, slot = APTR.FromPointer(slot.Raw + blockBytes + 4))
            if (p.ReadUInt32(slot) == block) return true;
        return false;
    }

    internal static void Store<T>(ref T p, APTR buffer, uint blockBytes, uint block, APTR source)
        where T : struct, IAmigaGuestMemory
    {
        var count = p.ReadUInt32(buffer);
        var index = 0u;
        var slot = APTR.FromPointer(buffer.Raw + HeaderBytes);
        for (; index < count; index++, slot = APTR.FromPointer(slot.Raw + blockBytes + 4))
            if (p.ReadUInt32(slot) == block) break;
        p.WriteUInt32(slot, 0, block);
        p.Copy(source, APTR.FromPointer(slot.Raw + 4), blockBytes);
        if (index == count) p.WriteUInt32(buffer, 0, count + 1);
    }
}
