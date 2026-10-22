using Amiga;
using CopperSFS;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private static void Combine(ref Disk disk, uint capacity = 64)
    {
        disk.WriteQueue = APTR.FromPointer(131072);
        Assert.True(AmigaFileSystemWriteCombiner.Initialize(ref disk, disk.WriteQueue, Bytes, capacity));
    }

    [Fact]
    public void CombiningOwnsDataSortsAdjacentBlocksAndNeverWritesHoles()
    {
        var disk = new Disk(Image()) { Buffered = true };
        Combine(ref disk);
        foreach (var block in new uint[] { 69, 5, 8, 7, 6, 70, 5 })
        {
            Array.Fill(disk.Ram, (byte)(block + disk.ReadUInt32(disk.WriteQueue)), 4000, Bytes);
            Assert.True(disk.WriteBlock(block, APTR.FromPointer(4000)));
        }
        Array.Fill(disk.Ram, (byte)0xFF, 4000, Bytes);
        Assert.True(disk.ReadBlock(5, APTR.FromPointer(4000)));
        Assert.All(disk.Ram.AsSpan(4000, Bytes).ToArray(), b => Assert.Equal(11, b));
        Assert.Equal(0, disk.Writes);
        Assert.True(disk.Flush());
        Assert.Equal(new[] { (5u, 4u), (69u, 2u) }, disk.PhysicalWrites);
        Assert.Equal(1, disk.Flushes);
        Assert.Equal(0u, AmigaFileSystemWriteCombiner.Count(ref disk, disk.WriteQueue));
        Assert.All(disk.Snapshot().AsSpan(5 * Bytes, Bytes).ToArray(), b => Assert.Equal(11, b));
    }

    [Fact]
    public void CombiningCapacityFailureIsAtomicAndReplacementWorksWhenFull()
    {
        var disk = new Disk(Image()); Combine(ref disk, 4);
        var source = APTR.FromPointer(4000);
        Array.Fill(disk.Ram, (byte)0xA1, 4000, Bytes * 4);
        Assert.True(AmigaFileSystemWriteCombiner.TryStore(ref disk, disk.WriteQueue, 10, 4, source));
        Array.Fill(disk.Ram, (byte)0xB2, 4000, Bytes * 4);
        Assert.False(AmigaFileSystemWriteCombiner.TryStore(ref disk, disk.WriteQueue, 12, 4, source));
        Assert.True(disk.ReadBlock(12, source)); Assert.Equal(0xA1, disk.Ram[4000]);
        Array.Fill(disk.Ram, (byte)0xB2, 4000, Bytes * 4);
        Assert.True(AmigaFileSystemWriteCombiner.TryStore(ref disk, disk.WriteQueue, 11, 2, source));
        Assert.True(disk.ReadBlock(12, source)); Assert.Equal(0xB2, disk.Ram[4000]);
        Assert.True(disk.WriteBlock(20, source)); // pressure drains, without flushing
        Assert.Equal(0, disk.Flushes); Assert.Equal(4, disk.Writes);
        Assert.Equal(1u, AmigaFileSystemWriteCombiner.Count(ref disk, disk.WriteQueue));
    }

    [Fact]
    public void CombiningFullQueueHandlesHashCollisionsAndInvalidatesMetadataVerification()
    {
        var disk = new Disk(Image()); Combine(ref disk, 128);
        var input = APTR.FromPointer(4000);
        for (var block = 128u; block != 0; block--)
        {
            Array.Fill(disk.Ram, (byte)block, 4000, Bytes);
            Assert.True(AmigaFileSystemWriteCombiner.TryStore(ref disk, disk.WriteQueue, block, 1, input));
        }
        Assert.False(AmigaFileSystemWriteCombiner.TryStore(ref disk, disk.WriteQueue, 129, 1, input));
        Assert.False(AmigaFileSystemWriteCombiner.TryStore(ref disk, disk.WriteQueue, uint.MaxValue - 1, 4, input));
        AmigaFileSystemWriteCombiner.Sort(ref disk, disk.WriteQueue);
        var data = AmigaFileSystemWriteCombiner.Run(ref disk, disk.WriteQueue, 0, out var first, out var count);
        Assert.Equal(1u, first); Assert.Equal(128u, count);
        for (var i = 0u; i < count; i++) Assert.All(disk.Ram.AsSpan((int)(data.Raw + i * Bytes), Bytes).ToArray(), b => Assert.Equal((byte)(i + 1), b));

        Block(Image(), 5).CopyTo(disk.Ram.AsSpan(4000, Bytes));
        Assert.True(AmigaFileSystemWriteCombiner.TryStore(ref disk, disk.WriteQueue, 5, 1, input));
        Assert.True(AmigaFileSystemWriteCombiner.ReadMetadataView(ref disk, disk.WriteQueue, 5, Tree, out var found).IsNotNull);
        Assert.True(found);
        disk.Ram[4004] ^= 1;
        Assert.True(AmigaFileSystemWriteCombiner.TryStore(ref disk, disk.WriteQueue, 5, 1, input));
        Assert.True(AmigaFileSystemWriteCombiner.ReadMetadataView(ref disk, disk.WriteQueue, 5, Tree, out found).IsNull);
        Assert.True(found); // invalid queued metadata must not fall back to old media
    }

    [Theory]
    [InlineData(2u, false)]
    [InlineData(64u, false)]
    [InlineData(128u, false)]
    [InlineData(2u, true)]
    [InlineData(64u, true)]
    [InlineData(128u, true)]
    public void EveryCombinedSectorCutRecoversOldOrNewCheckpoint(uint capacity, bool buffered)
    {
        var initial = Image();
        var probe = new Disk(initial.ToArray()) { Buffered = true };
        var work = buffered ? BufferedWork(ref probe) : BatchWork(ref probe); Combine(ref probe, capacity);
        BufferedOverwrite(ref probe, ref work, 0xA7);
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref probe, ref work));
        for (var cut = 1; cut <= probe.IoEvents; cut++)
        {
            var disk = new Disk(initial.ToArray()) { Buffered = true };
            work = buffered ? BufferedWork(ref disk) : BatchWork(ref disk); Combine(ref disk, capacity);
            disk.CutAfterIo = cut;
            Assert.Throws<PowerCut>(() =>
            {
                BufferedOverwrite(ref disk, ref work, 0xA7);
                AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work);
            });
            // Device writes can persist in any subset; RAM-queued writes cannot.
            foreach (var image in disk.CrashImages())
            {
                var recovered = new Disk(image); var recovery = BatchWork(ref recovered);
                Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref recovery), $"{capacity}/{buffered}/{cut}: {recovery.Error}");
                var contents = ReadBufferedFile(ref recovered);
                Assert.True(contents.All(b => b == 0xA7) || contents.SequenceEqual(
                    Enumerable.Range(0, 1300).Select(i => (byte)(i * 17))), $"mixed result at {cut}");
            }
        }
    }

    [Theory]
    [InlineData(2u, 1300u)]
    [InlineData(64u, 1300u)]
    [InlineData(2u, 3000u)]
    [InlineData(64u, 3000u)]
    [InlineData(128u, 1300u)]
    [InlineData(128u, 3000u)]
    public void EveryCombinedWriteOrFlushFailureRetainsAcknowledgedDataForRetry(uint capacity, uint length)
    {
        var probe = new Disk(Image()) { Buffered = true };
        var work = BufferedWork(ref probe); Combine(ref probe, capacity);
        Write(ref probe, ref work);
        var before = probe.Writes;
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref probe, ref work));
        for (var failure = 1; failure <= probe.Writes - before + probe.Flushes; failure++)
        {
            var disk = new Disk(Image()) { Buffered = true };
            work = BufferedWork(ref disk); Combine(ref disk, capacity);
            Write(ref disk, ref work);
            if (failure <= probe.Writes - before) disk.FailAtWrite = disk.Writes + failure;
            else disk.FailAtFlush = failure - (probe.Writes - before);
            Assert.False(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work));
            disk.FailAtWrite = disk.FailAtFlush = 0;
            Assert.All(ReadBufferedFile(ref disk, 3, length), b => Assert.Equal(0xA7, b));
            Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), $"{capacity}/{failure}: {work.Error}");
            var recovered = new Disk(disk.Snapshot());
            Assert.All(ReadBufferedFile(ref recovered, 3, length), b => Assert.Equal(0xA7, b));
        }

        void Write(ref Disk disk, ref AmigaSmartFileSystemWriteCore.Work state)
        {
            Array.Fill(disk.Ram, (byte)0xA7, 4000, (int)length);
            var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Buffer = APTR.FromPointer(4000), Length = length };
            Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref state, ref request, out var actual), state.Error.ToString());
            Assert.Equal(length, actual);
        }
    }
}
