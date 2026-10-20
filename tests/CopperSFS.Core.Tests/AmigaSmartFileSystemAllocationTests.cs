using System.Buffers.Binary;
using Amiga;
using CopperSFS;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(2u, 109u)]
    [InlineData(31u, 109u)]
    [InlineData(32u, 109u)]
    [InlineData(109u, 109u)]
    [InlineData(110u, 114u)]
    [InlineData(126u, 109u)]
    public void AllocationSkipsUsedWordsAndWrapsWithoutLosingTheFirstFreeBlock(uint cursor, uint expected)
    {
        var seed = new Disk(Image());
        var work = BatchWork(ref seed);
        var create = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = seed.Name("Scan"), Length = 4 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref seed, ref work, ref create, false));
        var original = seed.Snapshot();
        var bitmap = Block(original, 9);
        bitmap[12..].Clear();
        // Free data and undo space, separated by long runs of used bits.
        // The final data candidate (126) is used; 127 is the reserved root.
        foreach (var free in Enumerable.Range(114, 12).Prepend(109))
        {
            var at = 12 + free / 32 * 4;
            U32(bitmap, at, BinaryPrimitives.ReadUInt32BigEndian(bitmap[at..]) | (1u << (31 - free % 32)));
        }
        Sum(bitmap);
        U32(Block(original, 2), Bytes - 28, 13);
        Sum(Block(original, 2));
        var before = AllocatedState(original);
        var write = new AmigaSmartFileSystemWriteCore.Request
        { Node = create.Node, Buffer = APTR.FromPointer(4000), Length = 1 };
        Disk Start()
        {
            var disk = new Disk(original.ToArray()) { Buffered = true };
            disk.Ram[4000] = 0xA3;
            return disk;
        }
        AmigaSmartFileSystemWriteCore.Work Work(ref Disk disk)
        {
            var result = BatchWork(ref disk);
            result.AllocationCursor = cursor;
            result.JournalCursor = 126; // Must also wrap and skip used words.
            return result;
        }
        var probe = Start();
        work = Work(ref probe);
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref probe, ref work, ref write, out var actual), work.Error.ToString());
        Assert.Equal(1u, actual);
        var written = probe.Snapshot();
        var expectedBitmap = Block(original, 9).ToArray();
        var offset = 12 + (int)(expected / 32) * 4;
        U32(expectedBitmap, offset, BinaryPrimitives.ReadUInt32BigEndian(expectedBitmap.AsSpan(offset)) & ~(1u << (int)(31 - expected % 32)));
        Sum(expectedBitmap);
        Assert.Equal(expectedBitmap, Block(written, 9).ToArray());
        Assert.Equal((byte)0xA3, written[expected * Bytes]);
        var after = AllocatedState(written);
        for (var cut = 1; cut <= probe.IoEvents; cut++)
        {
            var interrupted = Start(); interrupted.CutAfterIo = cut;
            work = Work(ref interrupted);
            Assert.Throws<PowerCut>(() => AmigaSmartFileSystemWriteCore.Write(ref interrupted, ref work, ref write, out _));
            var recovered = new Disk(interrupted.Snapshot());
            work = BatchWork(ref recovered);
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work), $"cursor {cursor}, cut {cut}");
            var state = AllocatedState(recovered.Snapshot());
            Assert.True(state.SequenceEqual(before) || state.SequenceEqual(after), $"cursor {cursor}, cut {cut}");
        }
    }
}
