using System.Buffers.Binary;
using Amiga;

namespace CopperSFS.Tests;

public sealed class WriteQueueHeaderViewTests
{
    [Theory]
    [InlineData(0u, 512u)]
    [InlineData(17u, 1024u)]
    [InlineData(uint.MaxValue, 4u)]
    public void ViewPreservesBigEndianBytesAccessOrderAndPlatformState(uint count, uint bytes)
    {
        const int address = 130; // Word aligned, not longword aligned.
        var memory = new TracedMemory(new byte[512]);
        Array.Fill(memory.Bytes, (byte)0xA7);
        Put(memory.Bytes, address, count);
        Put(memory.Bytes, address + 4, 128);
        Put(memory.Bytes, address + 8, bytes);
        Put(memory.Bytes, address + 24, 0xFEDCBA98);
        Put(memory.Bytes, address + 28, 0xF1234567);
        var expected = (byte[])memory.Bytes.Clone();
        Put(expected, address, unchecked(count + 1));
        Put(expected, address + 20, 64);

        var queue = new AmigaFileSystemWriteQueueHeaderView<TracedMemory>(ref memory, APTR.FromPointer(address));
        queue.Count++;
        queue.BulkBlocks = queue.Capacity >> 1;
        var result = queue.Count * queue.BlockBytes + queue.Order.Raw - queue.Data.Raw;

        Assert.Equal(unchecked((count + 1) * bytes + 0xFEDCBA98u - 0xF1234567u), result);
        Assert.Equal(expected, memory.Bytes); // Neighbouring fields and guard bytes survive.
        Assert.Equal(6, memory.Reads);
        Assert.Equal(2, memory.Writes);
        Assert.Equal(new[] { "R:130", "W:130", "R:134", "W:150", "R:130", "R:138", "R:154", "R:158" }, memory.Accesses);
    }

    [Fact]
    public void BorrowedViewObservesPlatformReplacementInsteadOfRetainingACopy()
    {
        var memory = new TracedMemory(new byte[64]);
        var queue = new AmigaFileSystemWriteQueueHeaderView<TracedMemory>(ref memory, APTR.FromPointer(8));
        var oldBytes = memory.Bytes;
        memory = new TracedMemory(new byte[64]);
        Put(memory.Bytes, 8, 23);
        Assert.Equal(23u, queue.Count);
        queue.Count = 24;
        Assert.Equal(24u, BinaryPrimitives.ReadUInt32BigEndian(memory.Bytes.AsSpan(8)));
        Assert.All(oldBytes, value => Assert.Equal(0, value));
        Assert.Equal(1, memory.Reads);
        Assert.Equal(1, memory.Writes);
    }

    private static void Put(byte[] bytes, int offset, uint value)
        => BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(offset), value);

    private struct TracedMemory(byte[] bytes) : IAmigaGuestMemory
    {
        public byte[] Bytes = bytes;
        public List<string> Accesses = [];
        public int Reads, Writes;
        public uint ReadUInt32(APTR address, int offset = 0)
        {
            var at = checked((int)address.Raw + offset);
            Reads++; Accesses.Add($"R:{at}");
            return BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan(at));
        }
        public void WriteUInt32(APTR address, int offset, uint value)
        {
            var at = checked((int)address.Raw + offset);
            Writes++; Accesses.Add($"W:{at}"); Put(Bytes, at, value);
        }
        public byte ReadUInt8(APTR address, int offset = 0) => throw new NotSupportedException();
        public ushort ReadUInt16(APTR address, int offset = 0) => throw new NotSupportedException();
        public void WriteUInt8(APTR address, int offset, byte value) => throw new NotSupportedException();
        public void WriteUInt16(APTR address, int offset, ushort value) => throw new NotSupportedException();
        public void Copy(APTR source, APTR destination, uint byteCount) => throw new NotSupportedException();
        public void Clear(APTR address, uint byteCount) => throw new NotSupportedException();
        public bool IsMapped(APTR address, uint byteSize) => throw new NotSupportedException();
    }
}
