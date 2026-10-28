using System.Runtime.CompilerServices;
using Amiga;
using Header = CopperSFS.AmigaFileSystemWriteQueueLayout.Header;

namespace CopperSFS.Prototypes;

/// <summary>Native-only comparison for the borrowed generic view. APTR accesses
/// are compiler intrinsics and cannot execute on the host CLR. Experimental;
/// this type is not part of the handler or the shared memory abstraction.</summary>
public readonly struct NativeWriteQueueHeaderView
{
    private readonly APTR address;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public NativeWriteQueueHeaderView(APTR address) => this.address = address;

    public uint Count
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => APTR.ReadUInt32(address, Header.Count);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => APTR.WriteUInt32(address, Header.Count, value);
    }

    public uint Capacity
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => APTR.ReadUInt32(address, Header.Capacity);
    }

    public uint BlockBytes
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => APTR.ReadUInt32(address, Header.BlockBytes);
    }

    public uint BulkBlocks
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => APTR.ReadUInt32(address, Header.BulkBlocks);
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => APTR.WriteUInt32(address, Header.BulkBlocks, value);
    }

    public APTR Order
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => APTR.FromPointer(APTR.ReadUInt32(address, Header.Order));
    }

    public APTR Data
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => APTR.FromPointer(APTR.ReadUInt32(address, Header.Data));
    }
}
