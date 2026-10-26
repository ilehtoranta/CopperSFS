using System.Buffers.Binary;
using Amiga;
using CopperSFS;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private static readonly APTR RangePayload = APTR.FromPointer(262144);
    private static readonly APTR RangeScratch = APTR.FromPointer(327680);

    private static (byte[] Image, uint File, uint Neighbor) RangeFixture()
    {
        const int count = 1024;
        var image = new byte[count * Bytes]; Image().CopyTo(image, 0);
        var root = Block(image, 0);
        U32(root, 44, count * Bytes); U32(root, 48, count); Sum(root);
        root.CopyTo(Block(image, count - 1));
        U32(Block(image, count - 1), 8, count - 1); Sum(Block(image, count - 1));
        var map = Block(image, 9); var at = 12 + (count - 1) / 32 * 4;
        U32(map, at, BinaryPrimitives.ReadUInt32BigEndian(map[at..]) & ~1u); Sum(map);
        U32(Block(image, 2), Bytes - 28, 82 + count - Blocks - 1); Sum(Block(image, 2));
        var disk = new Disk(image); var work = BatchWork(ref disk);
        var file = new AmigaSmartFileSystemWriteCore.Request { Directory = 1, Name = disk.Name("Bulk"), Length = 4 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref file, false));
        var neighbor = new AmigaSmartFileSystemWriteCore.Request { Directory = 1, Name = disk.Name("Gap"), Length = 3 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref neighbor, false));
        for (var chunk = 0; chunk < 4; chunk++)
        foreach (var (node, value) in new[] { (file.Node, (byte)0x31), (neighbor.Node, (byte)0x71) })
        {
            Array.Fill(disk.Ram, value, (int)RangePayload.Raw, 8192);
            var write = new AmigaSmartFileSystemWriteCore.Request
            { Node = node, Position = (uint)chunk * 8192, Buffer = RangePayload, Length = 8192 };
            Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref write, out var actual), work.Error.ToString());
            Assert.Equal(8192u, actual);
        }
        return (disk.Snapshot(), file.Node, neighbor.Node);
    }

    private static AmigaSmartFileSystemWriteCore.Work RangeWork(ref Disk disk, bool buffered, bool combined)
    {
        var work = buffered ? BufferedWork(ref disk) : BatchWork(ref disk);
        work.Reservations = APTR.FromPointer(420000);
        if (combined) Combine(ref disk, 32);
        disk.IoEvents = disk.Writes = disk.Flushes = 0;
        return work;
    }

    private static bool RangeWrite(ref Disk disk, ref AmigaSmartFileSystemWriteCore.Work work,
        uint node, bool relocate, out uint actual, bool scratch = true)
    {
        Array.Fill(disk.Ram, (byte)0xA7, (int)RangePayload.Raw, 32768);
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = node, Buffer = RangePayload, Length = 32768 };
        return AmigaSmartFileSystemWriteCore.WriteBatched(ref disk, ref work, ref request,
            scratch ? RangeScratch : APTR.Null, scratch ? 65536u : 0, out actual, relocate);
    }

    private static byte[] RangeRead(ref Disk disk, uint node)
    {
        Assert.Equal(32768, AmigaSmartFileSystemCore.Read(ref disk, node, 0, RangePayload, 32768,
            APTR.FromPointer(512), APTR.FromPointer(1024), out var error));
        Assert.Equal(DOS.Error.None, error);
        return disk.Ram.AsSpan((int)RangePayload.Raw, 32768).ToArray();
    }

    [Theory]
    [InlineData(false, false, false)] [InlineData(false, false, true)]
    [InlineData(false, true, false)] [InlineData(false, true, true)]
    [InlineData(true, false, false)] [InlineData(true, false, true)]
    [InlineData(true, true, false)] [InlineData(true, true, true)]
    public void RangeWriteEverySectorAndFlushCutRecovers(bool buffered, bool combined, bool relocate)
    {
        var seed = RangeFixture(); var probe = new Disk(seed.Image.ToArray()) { Buffered = true };
        var work = RangeWork(ref probe, buffered, combined);
        Assert.True(RangeWrite(ref probe, ref work, seed.File, relocate, out var actual), work.Error.ToString());
        Assert.Equal(32768u, actual);
        Assert.Contains(probe.DataWrites, run => run.Count > 1);
        Assert.All(RangeRead(ref probe, seed.File), b => Assert.Equal(0xA7, b));
        for (var cut = 1; cut <= probe.IoEvents; cut++)
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true };
            work = RangeWork(ref disk, buffered, combined); disk.CutAfterIo = cut;
            Assert.Throws<PowerCut>(() => RangeWrite(ref disk, ref work, seed.File, relocate, out _));
            var recovered = new Disk(disk.Snapshot()); var recovery = BatchWork(ref recovered);
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref recovery), $"Cut {cut}: {recovery.Error}");
            var bytes = RangeRead(ref recovered, seed.File);
            Assert.True(bytes.All(b => b == 0x31) || bytes.All(b => b == 0xA7), $"Mixed data at cut {cut}");
            Assert.All(RangeRead(ref recovered, seed.Neighbor), b => Assert.Equal(0x71, b));
            Assert.Equal(BinaryPrimitives.ReadUInt32BigEndian(seed.Image.AsSpan(2 * Bytes + Bytes - 28)),
                BinaryPrimitives.ReadUInt32BigEndian(recovered.Snapshot().AsSpan(2 * Bytes + Bytes - 28)));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RangeWritePreservesAcknowledgedBufferAndRestoresDescriptorsAfterFailure(bool combined)
    {
        var seed = RangeFixture(); var disk = new Disk(seed.Image) { Buffered = true };
        var work = RangeWork(ref disk, true, combined);
        Array.Fill(disk.Ram, (byte)0x71, 4000, 32);
        var small = new AmigaSmartFileSystemWriteCore.Request { Node = seed.Neighbor, Buffer = APTR.FromPointer(4000), Length = 32 };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref small, out _));
        Assert.True(AmigaSmartFileSystemWriteBuffer.Count(ref disk, disk.WriteBack) > 0);
        Assert.True(RangeWrite(ref disk, ref work, seed.File, true, out _), work.Error.ToString());
        Assert.Equal(disk.WriteBack, work.Buffered); Assert.Equal(disk.Batch, work.Batch);
        Assert.Equal(0u, AmigaSmartFileSystemWriteBuffer.Count(ref disk, disk.WriteBack));
        disk.FailAtWrite = disk.Writes + 10;
        Assert.False(RangeWrite(ref disk, ref work, seed.File, true, out var actual));
        Assert.Equal(0u, actual); Assert.Equal(disk.WriteBack, work.Buffered); Assert.Equal(disk.Batch, work.Batch);
        disk.FailAtWrite = 0;
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work));
        Assert.True(RangeWrite(ref disk, ref work, seed.File, true, out actual), work.Error.ToString());
        Assert.Equal(32768u, actual);
        Assert.All(RangeRead(ref disk, seed.Neighbor), b => Assert.Equal(0x71, b));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void RangeWriteAppendsSixtyFourKiBInOneTransfer(bool buffered)
    {
        var seed = RangeFixture(); var disk = new Disk(seed.Image) { Buffered = true };
        var work = RangeWork(ref disk, buffered, false);
        var create = new AmigaSmartFileSystemWriteCore.Request { Directory = 1, Name = disk.Name("Append"), Length = 6 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref create, false));
        work.AllocationCursor = 512; // A known free run in this deliberately fragmented fixture.
        Array.Fill(disk.Ram, (byte)0xC9, (int)RangePayload.Raw, 65536);
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = create.Node, Buffer = RangePayload, Length = 65536 };
        disk.DataWrites.Clear();
        Assert.True(AmigaSmartFileSystemWriteCore.WriteBatched(ref disk, ref work, ref request, RangeScratch, 65536, out var actual), work.Error.ToString());
        Assert.Equal(65536u, actual); Assert.Contains(disk.DataWrites, run => run.Count == 128);
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work));
        var recovered = new Disk(disk.Snapshot());
        Assert.Equal(65536, AmigaSmartFileSystemCore.Read(ref recovered, create.Node, 0, RangePayload, 65536,
            APTR.FromPointer(512), APTR.FromPointer(1024), out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.All(recovered.Ram.AsSpan((int)RangePayload.Raw, 65536).ToArray(), b => Assert.Equal(0xC9, b));
    }

    [Fact]
    public void RangeWriteFallsBackWithoutScratch()
    {
        var seed = RangeFixture(); var disk = new Disk(seed.Image); var work = BatchWork(ref disk);
        Assert.True(RangeWrite(ref disk, ref work, seed.File, true, out var actual, scratch: false), work.Error.ToString());
        Assert.Equal(32768u, actual); Assert.All(RangeRead(ref disk, seed.File), b => Assert.Equal(0xA7, b));
    }

    private static (byte[] Image, uint File, uint Neighbor) LinearRangeFixture()
    {
        var seed = RangeFixture(); var disk = new Disk(seed.Image); var work = BatchWork(ref disk);
        var create = new AmigaSmartFileSystemWriteCore.Request { Directory = 1, Name = disk.Name("Linear"), Length = 6 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref create, false));
        work.AllocationCursor = 512;
        Array.Fill(disk.Ram, (byte)0x31, (int)RangePayload.Raw, 32768);
        for (var chunk = 0; chunk < 3; chunk++)
        {
            var request = new AmigaSmartFileSystemWriteCore.Request
                { Node = create.Node, Position = (uint)chunk * 32768, Buffer = RangePayload, Length = 32768 };
            Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out var actual), work.Error.ToString());
            Assert.Equal(32768u, actual);
        }
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work));
        return (disk.Snapshot(), create.Node, seed.Neighbor);
    }

    private static byte[] ReadLinearRange(ref Disk disk, uint node, out uint[] blocks)
    {
        disk.DataReads.Clear();
        Assert.Equal(98304, AmigaSmartFileSystemCore.Read(ref disk, node, 0, RangePayload, 98304,
            APTR.FromPointer(512), APTR.FromPointer(1024), out var error));
        Assert.Equal(DOS.Error.None, error);
        blocks = disk.DataReads.SelectMany(run => Enumerable.Range((int)run.Block, (int)run.Count).Select(n => (uint)n)).ToArray();
        Assert.Equal(192, blocks.Length);
        return disk.Ram.AsSpan((int)RangePayload.Raw, 98304).ToArray();
    }

    private static bool WriteLinearRange(ref Disk disk, ref AmigaSmartFileSystemWriteCore.Work work,
        uint node, uint position, out uint actual)
    {
        Array.Fill(disk.Ram, (byte)0xA7, (int)RangePayload.Raw, 65536);
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = node, Position = position, Buffer = RangePayload, Length = 65536 };
        return AmigaSmartFileSystemWriteCore.WriteBatched(ref disk, ref work, ref request, RangeScratch, 65536, out actual);
    }

    [Theory]
    [InlineData(false, false, 0u)] [InlineData(false, false, 8192u)]
    [InlineData(false, true, 0u)] [InlineData(false, true, 8192u)]
    [InlineData(true, false, 0u)] [InlineData(true, false, 8192u)]
    [InlineData(true, true, 0u)] [InlineData(true, true, 8192u)]
    public void ContiguousRangeRelocatesAndEveryCutPreservesEdgesAndNeighbors(bool buffered, bool combined, uint position)
    {
        var seed = LinearRangeFixture(); var probe = new Disk(seed.Image.ToArray()) { Buffered = true };
        var original = ReadLinearRange(ref probe, seed.File, out var oldBlocks);
        Assert.Equal(Enumerable.Range((int)oldBlocks[0], 192).Select(n => (uint)n), oldBlocks);
        var expected = original.ToArray(); Array.Fill(expected, (byte)0xA7, (int)position, 65536);
        var work = RangeWork(ref probe, buffered, combined);
        Assert.True(WriteLinearRange(ref probe, ref work, seed.File, position, out var actual), work.Error.ToString());
        Assert.Equal(65536u, actual);
        Assert.Equal(expected, ReadLinearRange(ref probe, seed.File, out var newBlocks));
        var first = (int)(position / Bytes);
        for (var i = 0; i < oldBlocks.Length; i++)
        {
            if (i < first || i >= first + 128) Assert.Equal(oldBlocks[i], newBlocks[i]);
            else Assert.DoesNotContain(newBlocks[i], oldBlocks);
        }
        var free = BinaryPrimitives.ReadUInt32BigEndian(seed.Image.AsSpan(2 * Bytes + Bytes - 28));
        for (var cut = 1; cut <= probe.IoEvents; cut++)
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true };
            work = RangeWork(ref disk, buffered, combined); disk.CutAfterIo = cut;
            Assert.Throws<PowerCut>(() => WriteLinearRange(ref disk, ref work, seed.File, position, out _));
            var recovered = new Disk(disk.Snapshot()); var recovery = BatchWork(ref recovered);
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref recovery), $"Cut {cut}: {recovery.Error}");
            var bytes = ReadLinearRange(ref recovered, seed.File, out _);
            Assert.True(bytes.SequenceEqual(original) || bytes.SequenceEqual(expected), $"Mixed data at cut {cut}");
            Assert.All(RangeRead(ref recovered, seed.Neighbor), b => Assert.Equal(0x71, b));
            Assert.Equal(free, BinaryPrimitives.ReadUInt32BigEndian(recovered.Snapshot().AsSpan(2 * Bytes + Bytes - 28)));
        }
        work = RangeWork(ref probe, buffered, combined);
        for (var repeat = 0; repeat < 4; repeat++)
        {
            Assert.True(WriteLinearRange(ref probe, ref work, seed.File, position, out _), work.Error.ToString());
            Assert.Equal(expected, ReadLinearRange(ref probe, seed.File, out _));
            Assert.Equal(free, BinaryPrimitives.ReadUInt32BigEndian(probe.Snapshot().AsSpan(2 * Bytes + Bytes - 28)));
        }
    }
}
