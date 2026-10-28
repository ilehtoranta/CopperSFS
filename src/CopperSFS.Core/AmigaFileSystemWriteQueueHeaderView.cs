using System.Runtime.CompilerServices;
using Amiga;
using Header = CopperSFS.AmigaFileSystemWriteQueueLayout.Header;

namespace CopperSFS;

/// <summary>
/// Borrows the platform and addresses an existing physical queue header.
/// Accessors perform one guest-memory operation, without caching or validation.
/// Construction does not touch memory. Storage, image ownership and commit
/// barriers remain the responsibility of the write combiner and its caller.
/// </summary>
public readonly ref struct AmigaFileSystemWriteQueueHeaderView<T> where T : struct, IAmigaGuestMemory
{
    private readonly ref T memory;
    private readonly APTR address;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public AmigaFileSystemWriteQueueHeaderView(ref T memory, APTR address)
    {
        this.memory = ref memory;
        this.address = address;
    }

    public uint Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => memory.ReadUInt32(address, Header.Count);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => memory.WriteUInt32(address, Header.Count, value);
    }

    public uint Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => memory.ReadUInt32(address, Header.Capacity);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => memory.WriteUInt32(address, Header.Capacity, value);
    }

    public uint BlockBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => memory.ReadUInt32(address, Header.BlockBytes);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => memory.WriteUInt32(address, Header.BlockBytes, value);
    }

    public uint MinimumBlock
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => memory.ReadUInt32(address, Header.MinimumBlock);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => memory.WriteUInt32(address, Header.MinimumBlock, value);
    }

    public uint MaximumBlock
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => memory.ReadUInt32(address, Header.MaximumBlock);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => memory.WriteUInt32(address, Header.MaximumBlock, value);
    }

    public uint BulkBlocks
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => memory.ReadUInt32(address, Header.BulkBlocks);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => memory.WriteUInt32(address, Header.BulkBlocks, value);
    }

    public APTR Order
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => APTR.FromPointer(memory.ReadUInt32(address, Header.Order));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => memory.WriteUInt32(address, Header.Order, value.Raw);
    }

    public APTR Data
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => APTR.FromPointer(memory.ReadUInt32(address, Header.Data));
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => memory.WriteUInt32(address, Header.Data, value.Raw);
    }
}
