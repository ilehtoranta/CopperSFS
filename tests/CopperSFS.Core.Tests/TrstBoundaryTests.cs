using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(2000, 3999, false)] [InlineData(2000, 3999, true)]
    [InlineData(3999, 11999, false)] [InlineData(3999, 11999, true)]
    public void TrstBitmapBoundaryCanAdmitDestinationAndBothMissingMaps(int first, int replacement, bool scratch)
    {
        var seed = ContinuationSeed(first, nextExtent: true); var expected = seed.Bytes.ToArray();
        // Reserve the lower free range so the first replacement ends at the
        // selected destination-map boundary. Preserve every existing used bit.
        for (var block = 1000; block < replacement; block++)
        {
            var bit = block % 4000;
            Block(seed.Image, 12 + block / 4000)[12 + bit / 8] &= (byte)~(128 >> (bit & 7));
            seed.Used.Add(block);
        }
        // Keep later holes shorter than the 96-block source extent. The
        // placement preference must fall back to the selected last block of
        // this bitmap so the next Write really exercises missing-map admission.
        for (var block = replacement + 64; block < 16383; block += 64)
        {
            var bit = block % 4000;
            Block(seed.Image, 12 + block / 4000)[12 + bit / 8] &= (byte)~(128 >> (bit & 7));
            seed.Used.Add(block);
        }
        for (var map = 12; map < 17; map++) Sum(Block(seed.Image, map));
        U32(Block(seed.Image, 2), Bytes - 28, (uint)(16384 - seed.Used.Count)); Sum(Block(seed.Image, 2));
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var payload = Enumerable.Repeat((byte)0xA7, 512).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 0, payload)); payload.CopyTo(expected, 0);
        Assert.Equal((uint)replacement, disk.ReadUInt32(GroupAddress, 92));
        payload = Enumerable.Repeat((byte)0xA8, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 512, payload)); payload.CopyTo(expected, 512);
        Assert.Equal(expected, GroupRead(ref disk, 3));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstBitmapBoundaryUnprovenJournalBoundFallsBackWithoutLosingWrites(bool scratch)
    {
        var seed = ContinuationSeed(3984, nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var first = Enumerable.Repeat((byte)0xA9, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 0, first)); first.CopyTo(expected, 0);
        // The conservative proof cannot establish admission with this lower
        // bound; the general writer must search actual journal space instead.
        disk.WriteUInt32(GroupAddress, 60, 0);
        var second = Enumerable.Repeat((byte)0xAA, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 8192, second)); second.CopyTo(expected, 8192);
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 124));
        Assert.Equal(expected, GroupRead(ref disk, 3));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Theory]
    [InlineData(3968, false)] [InlineData(3968, true)]
    [InlineData(3984, false)] [InlineData(3984, true)]
    [InlineData(3992, false)] [InlineData(3992, true)]
    public void TrstBitmapBoundaryAndSpanningWritesKeepTheWholeFile(int first, bool scratch)
    {
        var seed = ContinuationSeed(first, nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        for (var i = 0; i < 4; i++)
        {
            var payload = Enumerable.Repeat((byte)(0xB1 + i), 8192).ToArray();
            var position = (uint)i * 8192;
            if (i == 1 && first == 3968)
            {
                Assert.True(DeferredWrite(ref disk, position, payload, out var handled, out _));
                Assert.True(handled); Assert.NotEqual(0u, disk.ReadUInt32(GroupAddress, 200));
            }
            else Assert.True(GroupWrite(ref disk, 3, position, payload));
            payload.CopyTo(expected, (int)position);
        }
        Assert.InRange(disk.ReadUInt32(GroupAddress, 8), 1u, 192u * 1024);
        Assert.Equal(expected, GroupRead(ref disk, 3));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot());
        Assert.Equal(expected, GroupRead(ref remount, 3));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstBitmapBoundarySectorFailuresRetainAcceptedArenaAndRetry(bool scratch)
    {
        var seed = ContinuationSeed(3984, nextExtent: true);
        for (var sector = 1; sector <= 16; sector++)
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            var expected = seed.Bytes.ToArray();
            var first = Enumerable.Repeat((byte)0xB5, 8192).ToArray();
            Assert.True(GroupWrite(ref disk, 3, 0, first)); first.CopyTo(expected, 0);
            var active = disk.RedoOverlay; var span = (int)disk.ReadUInt32(GroupAddress, 12);
            var before = disk.Ram.AsSpan((int)active.Raw, span).ToArray();
            var second = Enumerable.Repeat((byte)0xB6, 8192).ToArray();
            disk.FailAtWrite = disk.Writes + sector;
            Assert.False(GroupWrite(ref disk, 3, 8192, second));
            Assert.Equal(active, disk.RedoOverlay);
            Assert.Equal(before, disk.Ram.AsSpan((int)active.Raw, span).ToArray());
            disk.FailAtWrite = 0; Assert.Equal(expected, GroupRead(ref disk, 3));
            Assert.True(GroupWrite(ref disk, 3, 8192, second)); second.CopyTo(expected, 8192);
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
            var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstBitmapBoundaryMissingMapReadFailureKeepsPreviousWriteFlushable(bool scratch)
    {
        var seed = ContinuationSeed(3984, nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var first = Enumerable.Repeat((byte)0xB7, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 0, first)); first.CopyTo(expected, 0);
        var active = disk.RedoOverlay; var span = (int)disk.ReadUInt32(GroupAddress, 12);
        var before = disk.Ram.AsSpan((int)active.Raw, span).ToArray();
        disk.FailReadBlock = 13;
        Assert.False(GroupWrite(ref disk, 3, 8192, Enumerable.Repeat((byte)0xB8, 8192).ToArray()));
        Assert.Equal(active, disk.RedoOverlay);
        Assert.Equal(before, disk.Ram.AsSpan((int)active.Raw, span).ToArray());
        disk.FailReadBlock = uint.MaxValue;
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstBitmapBoundaryProofReadFailureCannotPublishTheCandidate(bool scratch)
    {
        var seed = ContinuationSeed(3984, nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var first = Enumerable.Repeat((byte)0xB9, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 0, first)); first.CopyTo(expected, 0);
        var active = disk.RedoOverlay; var span = (int)disk.ReadUInt32(GroupAddress, 12);
        var before = disk.Ram.AsSpan((int)active.Raw, span).ToArray();
        disk.FailAtDurableRead = disk.DurableReads + 1;
        Assert.False(GroupWrite(ref disk, 3, 8192, Enumerable.Repeat((byte)0xBA, 8192).ToArray()));
        Assert.Equal(disk.FailAtDurableRead, disk.DurableReads);
        Assert.Equal(active, disk.RedoOverlay);
        Assert.Equal(before, disk.Ram.AsSpan((int)active.Raw, span).ToArray());
        disk.FailAtDurableRead = 0;
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Fact]
    public void TrstBitmapBoundaryCrashCutsAndPendingSectorSubsetsRecoverTheWholeGroup()
    {
        var seed = ContinuationSeed(3999, nextExtent: true); var expected = seed.Bytes.ToArray();
        var first = Enumerable.Repeat((byte)0xBB, 512).ToArray(); var second = Enumerable.Repeat((byte)0xBC, 512).ToArray();
        first.CopyTo(expected, 0); second.CopyTo(expected, 512);
        void Run(ref Disk disk)
        {
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            Assert.True(GroupWrite(ref disk, 3, 0, first)); Assert.True(GroupWrite(ref disk, 3, 512, second));
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        }
        var baseline = new Disk(seed.Image.ToArray()) { Buffered = true }; Run(ref baseline);
        for (var cut = 1; cut <= baseline.IoEvents; cut++)
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CutAfterIo = cut };
            Assert.Throws<PowerCut>(() => Run(ref disk));
            foreach (var image in disk.CrashImages())
            {
                var remount = new Disk(image); var work = TrstWork();
                Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref remount, ref work));
                var actual = GroupRead(ref remount, 3);
                Assert.True(actual.SequenceEqual(seed.Bytes) || actual.SequenceEqual(expected), $"Partial bitmap-boundary group at cut {cut}");
                Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
            }
        }
    }
}
