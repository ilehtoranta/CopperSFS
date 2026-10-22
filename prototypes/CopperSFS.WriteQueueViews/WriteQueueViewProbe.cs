using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Amiga;
using Header = CopperSFS.AmigaFileSystemWriteQueueLayout.Header;

namespace CopperSFS.Prototypes;

/// <summary>Equivalent native entry points for code-generation comparison.
/// The harness places a queue address at $800 and initializes its header.</summary>
public static class WriteQueueViewProbe
{
    public static uint Raw()
    {
        var address = APTR.FromPointer(APTR.ReadUInt32(APTR.FromPointer(0x800), 0));
        APTR.WriteUInt32(address, Header.Count, APTR.ReadUInt32(address, Header.Count) + 1);
        APTR.WriteUInt32(address, Header.BulkBlocks, APTR.ReadUInt32(address, Header.Capacity) >> 1);
        return APTR.ReadUInt32(address, Header.Count) * APTR.ReadUInt32(address, Header.BlockBytes) +
            APTR.ReadUInt32(address, Header.Order) - APTR.ReadUInt32(address, Header.Data);
    }

    public static uint NativeView()
    {
        var queue = new NativeWriteQueueHeaderView(APTR.FromPointer(APTR.ReadUInt32(APTR.FromPointer(0x800), 0)));
        queue.Count++;
        queue.BulkBlocks = queue.Capacity >> 1;
        return queue.Count * queue.BlockBytes + queue.Order.Raw - queue.Data.Raw;
    }

    public static uint BorrowedView()
    {
        var memory = new WriteQueueNativeMemory();
        var queue = new AmigaFileSystemWriteQueueHeaderView<WriteQueueNativeMemory>(ref memory,
            APTR.FromPointer(APTR.ReadUInt32(APTR.FromPointer(0x800), 0)));
        queue.Count++;
        queue.BulkBlocks = queue.Capacity >> 1;
        return queue.Count * queue.BlockBytes + queue.Order.Raw - queue.Data.Raw;
    }

    public static uint RawPointers()
    {
        var address = APTR.FromPointer(APTR.ReadUInt32(APTR.FromPointer(0x800), 0));
        APTR.WriteUInt32(address, Header.Order, address.Raw + 288);
        APTR.WriteUInt32(address, Header.Data, address.Raw + 3360);
        return APTR.ReadUInt32(address, Header.Order) + APTR.ReadUInt32(address, Header.Data);
    }

    public static uint BorrowedPointers()
    {
        var memory = new WriteQueueNativeMemory();
        var address = APTR.FromPointer(APTR.ReadUInt32(APTR.FromPointer(0x800), 0));
        var queue = new AmigaFileSystemWriteQueueHeaderView<WriteQueueNativeMemory>(ref memory, address);
        queue.Order = APTR.FromPointer(address.Raw + 288);
        queue.Data = APTR.FromPointer(address.Raw + 3360);
        return queue.Order.Raw + queue.Data.Raw;
    }

}

[StructLayout(LayoutKind.Sequential, Pack = 4, Size = 4)]
public struct WriteQueueNativeMemory : IAmigaGuestMemory
{
    public byte ReadUInt8(APTR address, int offset = 0) => APTR.ReadUInt8(address, offset);
    public ushort ReadUInt16(APTR address, int offset = 0) => APTR.ReadUInt16(address, offset);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public uint ReadUInt32(APTR address, int offset = 0) => APTR.ReadUInt32(address, offset);
    public void WriteUInt8(APTR address, int offset, byte value) => APTR.WriteUInt8(address, offset, value);
    public void WriteUInt16(APTR address, int offset, ushort value) => APTR.WriteUInt16(address, offset, value);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void WriteUInt32(APTR address, int offset, uint value) => APTR.WriteUInt32(address, offset, value);
    // These operations are deliberately outside the field-access prototype.
    public void Clear(APTR address, uint byteCount) => throw new NotSupportedException();
    public void Copy(APTR source, APTR destination, uint byteCount) => throw new NotSupportedException();
    public bool IsMapped(APTR address, uint byteSize) => throw new NotSupportedException();
}
