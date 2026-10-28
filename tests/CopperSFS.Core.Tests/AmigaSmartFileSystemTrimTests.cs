using System.Buffers.Binary;
using Amiga;
using CopperSFS;
using Task = System.Threading.Tasks.Task;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Fact]
    public void ManualTrimReplaysOriginalTrstBeforeAuthorizingDiscard()
    {
        var image = Image(); var marker = Block(image, 4);
        Header(image, 4, 0x54525354);
        U16(marker, 16, 7); U32(marker, 18, 20); marker[22] = 0; marker[23] = 0xC0;
        marker[24] = (byte)'T'; marker[25] = (byte)'E'; marker[26] = (byte)'S'; marker[27] = (byte)'T';
        marker[28] = 0x7F; marker[29] = 0x7E; Sum(marker);
        var disk = new Disk(image) { Buffered = true, DiscardPattern = 2 };
        var work = BatchWork(ref disk);
        Assert.True(AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, 0, out var result));
        Assert.True(result.Blocks != 0);
        Assert.Equal("TEST", System.Text.Encoding.ASCII.GetString(ReadBufferedFile(ref disk).AsSpan(0, 4)));
    }

    [Theory]
    [InlineData(false, 2u, 0)]
    [InlineData(false, 128u, 2)]
    [InlineData(true, 2u, 0)]
    [InlineData(true, 2u, 1)]
    [InlineData(true, 128u, 1)]
    [InlineData(true, 128u, 2)]
    public void EveryCheckpointAndIrreversibleTrimSectorCutPreservesOldOrNewAllocatedState(
        bool buffered, uint capacity, int pattern)
    {
        for (var operation = 0; operation < 5; operation++)
        {
            var initial = Image(); var old = AllocatedState(initial);
            var probe = new Disk(initial.ToArray()) { Buffered = true, DiscardPattern = pattern };
            var work = TrimWork(ref probe, buffered, capacity);
            TrimMutation(ref probe, ref work, operation);
            var committed = new Disk(initial.ToArray()) { Buffered = true };
            var commitWork = TrimWork(ref committed, buffered, capacity);
            TrimMutation(ref committed, ref commitWork, operation);
            Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref committed, ref commitWork));
            var expected = AllocatedState(committed.Snapshot());
            probe.IoEvents = 0;
            Assert.True(AmigaSmartFileSystemWriteCore.Trim(ref probe, ref work, APTR.Null, 0, out var result), work.Error.ToString());
            Assert.True(result.Blocks != 0); Assert.Equal((uint)probe.DiscardSectors, result.Blocks);
            Assert.Equal(expected, AllocatedState(probe.Snapshot()));
            for (var cut = 1; cut <= probe.IoEvents; cut++)
            {
                var disk = new Disk(initial.ToArray()) { Buffered = true, DiscardPattern = pattern };
                work = TrimWork(ref disk, buffered, capacity);
                TrimMutation(ref disk, ref work, operation);
                disk.IoEvents = 0; disk.CutAfterIo = cut;
                Assert.Throws<PowerCut>(() => AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _));
                foreach (var image in disk.CrashImages())
                {
                    var recovered = new Disk(image); var recovery = BatchWork(ref recovered);
                    Assert.True(AmigaSmartFileSystemWriteCore.Trim(ref recovered, ref recovery, APTR.Null, 0, out _), $"restart trim {buffered}/{capacity}/{operation}/{cut}: {recovery.Error}");
                    Assert.True(AmigaSmartFileSystemCore.Mount(ref recovered, recovery.Meta));
                    var actual = AllocatedState(recovered.Snapshot());
                    Assert.True(actual.SequenceEqual(expected) || buffered && actual.SequenceEqual(old),
                        $"mixed allocation/data after {buffered}/{capacity}/{operation}/{cut}");
                    if (disk.DiscardSectors != 0) Assert.Equal(expected, actual);
                    Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(ReadBufferedFile(ref recovered, 4, 5)));
                }
            }
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void EveryPartiallyFailedDiscardCanBeRetriedWithoutLosingCommittedData(bool buffered)
    {
        var probe = new Disk(Image()) { Buffered = true }; var work = TrimWork(ref probe, buffered, 128);
        BufferedOverwrite(ref probe, ref work, 0xA7);
        Assert.True(AmigaSmartFileSystemWriteCore.Trim(ref probe, ref work, APTR.Null, 0, out _));
        var expected = AllocatedState(probe.Snapshot());
        for (var failure = 1; failure <= probe.DiscardSectors; failure++)
        {
            var disk = new Disk(Image()) { Buffered = true, DiscardPattern = 2, FailAtDiscardSector = failure };
            work = TrimWork(ref disk, buffered, 128); BufferedOverwrite(ref disk, ref work, 0xA7);
            Assert.False(AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _));
            Assert.Equal(DOS.Error.SeekError, work.Error); Assert.Equal(expected, AllocatedState(disk.Snapshot()));
            disk.FailAtDiscardSector = 0;
            Assert.True(AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _));
            Assert.Equal(expected, AllocatedState(disk.Snapshot()));
            Assert.All(ReadBufferedFile(ref disk), b => Assert.Equal(0xA7, b));
        }
    }

    [Fact]
    public void QueryUnsupportedInvalidLimitsReadOnlyAndDryRunNeverDiscard()
    {
        var disk = new Disk(Image()) { Buffered = true }; var work = BufferedWork(ref disk);
        BufferedOverwrite(ref disk, ref work, 0xA7);
        var io = disk.IoEvents;
        foreach (var flags in new[] { 3u, 4u, uint.MaxValue })
        {
            Assert.False(AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, flags, out _));
            Assert.Equal(DOS.Error.BadNumber, work.Error); Assert.Equal(io, disk.IoEvents);
        }
        Assert.True(AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, AmigaSmartFileSystemTrimPacket.QueryOnly, out _));
        Assert.Equal(io, disk.IoEvents); Assert.Empty(disk.DiscardRequests);
        disk.DiscardSupported = false;
        Assert.False(AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _));
        Assert.Equal(DOS.Error.ActionNotKnown, work.Error); Assert.Equal(io, disk.IoEvents);
        disk.DiscardSupported = true; disk.DiscardLimits.Granularity = 0;
        Assert.False(AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _));
        Assert.Equal(DOS.Error.BadNumber, work.Error); Assert.Equal(io, disk.IoEvents);
        disk.DiscardLimits.Granularity = 1; disk.Protected = true;
        Assert.False(AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _));
        Assert.Equal(DOS.Error.DiskWriteProtected, work.Error); Assert.Equal(io, disk.IoEvents);
        disk.Protected = false;
        Assert.True(AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, AmigaSmartFileSystemTrimPacket.DryRun, out var dry));
        Assert.True(dry.Blocks != 0); Assert.Empty(disk.DiscardRequests);
        Assert.True(AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, 0, out var real));
        Assert.Equal(dry.Blocks, real.Blocks); Assert.Equal(dry.Ranges, real.Ranges);
    }

    [Theory]
    [InlineData(1u, 0u, 1u)]
    [InlineData(4u, 0u, 9u)]
    [InlineData(4u, 3u, 9u)]
    [InlineData(8u, 5u, 32u)]
    public void TrimRespectsAlignmentGranularityLimitsAndPartitionEdges(uint granularity, uint alignment, uint maximum)
    {
        var disk = new Disk(Image()) { Buffered = true,
            DiscardLimits = new() { Granularity = granularity, Alignment = alignment, MaximumBlocks = maximum } };
        var work = BatchWork(ref disk);
        Assert.True(AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, 0, out var result));
        Assert.Equal((uint)disk.DiscardSectors, result.Blocks);
        foreach (var (block, count) in disk.DiscardRequests)
        {
            Assert.Equal(alignment, block % granularity); Assert.Equal(0u, count % granularity);
            Assert.InRange(count, granularity, maximum); Assert.True(block >= 1 && block + count <= 127);
        }
    }

    [Theory]
    [InlineData(0)] // bitmap checksum
    [InlineData(2)] // allocated root object
    [InlineData(4)] // clean transaction marker
    [InlineData(5)] // extent root
    [InlineData(6)] // node root
    [InlineData(9)] // bitmap itself
    [InlineData(10)] // admin root
    public void InvalidBitmapNeverAuthorizesDiscard(int corrupt)
    {
        var image = Image();
        if (corrupt == 0) Block(image, 9)[4] ^= 1;
        else
        {
            var map = Block(image, 9); var at = 12 + corrupt / 32 * 4;
            U32(map, at, BinaryPrimitives.ReadUInt32BigEndian(map[at..]) | (1u << (31 - corrupt % 32))); Sum(map);
        }
        var disk = new Disk(image) { Buffered = true }; var work = BatchWork(ref disk);
        Assert.False(AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _));
        Assert.Empty(disk.DiscardRequests); Assert.Equal(DOS.Error.DiskNotValidated, work.Error);
    }

    [Fact]
    public void OrdinaryMutationAndFlushDoNotProbeOrDiscard()
    {
        var disk = new Disk(Image()) { Buffered = true }; var work = BufferedWork(ref disk); Combine(ref disk, 128);
        for (var i = 0; i < 3; i++) BufferedOverwrite(ref disk, ref work, (byte)(0xA0 + i));
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work));
        Assert.Equal(0, disk.DiscardQueries); Assert.Empty(disk.DiscardRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void AllBitmapsAreCheckedBeforeDiscardAndRunsCrossBitmapBoundaries(bool corruptLastMap)
    {
        const int count = 5001;
        var initial = Image(); var image = new byte[count * Bytes]; initial.CopyTo(image, 0);
        Block(initial, 127).CopyTo(image.AsSpan((count - 1) * Bytes, Bytes));
        foreach (var root in new[] { 0, count - 1 })
        { U32(Block(image, root), 48, count); U32(Block(image, root), 96, 12); Sum(Block(image, root)); }
        foreach (var map in new[] { 12, 13 })
        {
            Header(image, map, Bitmap);
            for (var at = 12; at < Bytes; at += 4) U32(Block(image, map), at, uint.MaxValue);
        }
        for (var block = 0; block < count; block++)
        {
            var used = block < Blocks && (BinaryPrimitives.ReadUInt32BigEndian(Block(initial, 9)[(12 + block / 32 * 4)..]) &
                (1u << (31 - block % 32))) == 0;
            if (!used && block != 12 && block != 13 && block != count - 1) continue;
            var index = block % 4000; var map = Block(image, 12 + block / 4000); var at = 12 + index / 32 * 4;
            U32(map, at, BinaryPrimitives.ReadUInt32BigEndian(map[at..]) & ~(1u << (31 - index % 32)));
        }
        Sum(Block(image, 12)); Sum(Block(image, 13));
        if (corruptLastMap) Block(image, 13)[4] ^= 1;
        var disk = new Disk(image) { Buffered = true, DiscardLimits = new() { MaximumBlocks = 6000, Granularity = 1 } };
        var work = BatchWork(ref disk);
        var success = AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _);
        if (corruptLastMap) { Assert.False(success); Assert.Empty(disk.DiscardRequests); }
        else
        {
            Assert.True(success, work.Error.ToString());
            Assert.Contains(disk.DiscardRequests, range => range.Block < 4000 && range.Block + range.Count > 4000);
            Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(ReadBufferedFile(ref disk, 4, 5)));
            Assert.Equal(Enumerable.Range(0, 1300).Select(i => (byte)(i * 17)), ReadBufferedFile(ref disk));
        }
    }

    [Fact]
    public async Task SerializedMountQueuesTwoWritersReaderAndSecondTrimUntilDiscardCompletes()
    {
        var disk = new Disk(Image()) { Buffered = true }; var work = BufferedWork(ref disk); Combine(ref disk, 128);
        var gate = new object(); using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        using var writerAReady = new ManualResetEventSlim(); using var writerBReady = new ManualResetEventSlim();
        using var readerReady = new ManualResetEventSlim(); using var secondReady = new ManualResetEventSlim();
        var once = 0;
        disk.BeforeDiscard = () => { if (Interlocked.Exchange(ref once, 1) == 0) { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(20))); } };
        var trim = Task.Run(() => { lock (gate) Assert.True(AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _)); });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(20)));
        var a = Task.Run(() => { writerAReady.Set(); lock (gate) TrimMutation(ref disk, ref work, 0); });
        var b = Task.Run(() => { writerBReady.Set(); lock (gate) BufferedOverwrite(ref disk, ref work, 0xA7); });
        var reader = Task.Run(() => { readerReady.Set(); lock (gate) Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(ReadBufferedFile(ref disk, 4, 5))); });
        var second = Task.Run(() => { secondReady.Set(); lock (gate) Assert.True(AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _)); });
        try
        {
            Assert.True(writerAReady.Wait(TimeSpan.FromSeconds(20))); Assert.True(writerBReady.Wait(TimeSpan.FromSeconds(20)));
            Assert.True(readerReady.Wait(TimeSpan.FromSeconds(20))); Assert.True(secondReady.Wait(TimeSpan.FromSeconds(20)));
            var enteredGate = Monitor.TryEnter(gate);
            if (enteredGate) Monitor.Exit(gate);
            Assert.False(enteredGate);
            Assert.False(a.IsCompleted); Assert.False(b.IsCompleted); Assert.False(reader.IsCompleted); Assert.False(second.IsCompleted);
        }
        finally { release.Set(); }
        await Task.WhenAll(trim, a, b, reader, second).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work));
        var recovered = new Disk(disk.Snapshot());
        Assert.All(ReadBufferedFile(ref recovered), value => Assert.Equal(0xA7, value));
        Assert.NotEqual(0u, AmigaSmartFileSystemCore.Resolve(ref recovered, 1, recovered.Name("Pending"), 7, APTR.FromPointer(512), out _));
    }

    [Fact]
    public void All120OrdersOfTwoTrimsTwoWritersAndReaderPreserveViewsAndRemount()
    {
        foreach (var order in Permutations([0, 1, 2, 3, 4]))
        {
            var disk = new Disk(Image()) { Buffered = true }; var work = BufferedWork(ref disk); Combine(ref disk, 128);
            var overwritten = false;
            foreach (var operation in order)
            {
                if (operation is 0 or 1) Assert.True(AmigaSmartFileSystemWriteCore.Trim(ref disk, ref work, APTR.Null, 0, out _));
                else if (operation == 2) { BufferedOverwrite(ref disk, ref work, 0xA7); overwritten = true; }
                else if (operation == 3) TrimMutation(ref disk, ref work, 0);
                else
                {
                    var contents = ReadBufferedFile(ref disk);
                    if (overwritten) Assert.All(contents, b => Assert.Equal(0xA7, b));
                    else Assert.Equal(Enumerable.Range(0, 1300).Select(i => (byte)(i * 17)), contents);
                }
            }
            Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work));
            var recovered = new Disk(disk.Snapshot()); Assert.All(ReadBufferedFile(ref recovered), b => Assert.Equal(0xA7, b));
            Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(ReadBufferedFile(ref recovered, 4, 5)));
        }

        static IEnumerable<int[]> Permutations(int[] values)
        {
            if (values.Length == 0) { yield return []; yield break; }
            foreach (var value in values)
            foreach (var suffix in Permutations(values.Where(x => x != value).ToArray()))
                yield return new[] { value }.Concat(suffix).ToArray();
        }
    }

    private static AmigaSmartFileSystemWriteCore.Work TrimWork(ref Disk disk, bool buffered, uint capacity)
    {
        var work = buffered ? BufferedWork(ref disk) : BatchWork(ref disk); Combine(ref disk, capacity); return work;
    }

    private static void TrimMutation(ref Disk disk, ref AmigaSmartFileSystemWriteCore.Work work, int operation)
    {
        var r = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Directory = 1 };
        switch (operation)
        {
            case 0:
                r.Name = disk.Name("Pending"); r.Length = 7;
                Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref r, false));
                break;
            case 1: BufferedOverwrite(ref disk, ref work, 0xA7); break;
            case 2: Assert.True(AmigaSmartFileSystemWriteCore.Truncate(ref disk, ref work, 3)); break;
            case 3:
                r.Name = disk.Name("Renamed"); r.Length = 7;
                Assert.True(AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref r)); break;
            case 4: Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3)); break;
        }
    }
}
