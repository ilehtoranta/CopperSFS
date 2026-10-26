using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstAdmissionCanFlushImmediatelyWithoutModifyingABorrowedView(bool scratch)
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var payload = Enumerable.Repeat((byte)0xDA, 1024).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 0, payload)); payload.CopyTo(expected, 0);
        Assert.Equal(disk.RedoOverlay.Raw, disk.ReadUInt32(GroupAddress, 124));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Theory]
    [InlineData(2000, false)] [InlineData(2000, true)]
    [InlineData(4500, false)] [InlineData(4500, true)]
    public void TrstAdmissionMakesTheNextWriteIndependentOfDurableBitmapReads(int first, bool scratch)
    {
        var seed = ContinuationSeed(first, nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var payload = Enumerable.Repeat((byte)0xD1, 1024).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 0, payload)); payload.CopyTo(expected, 0);
        var active = disk.RedoOverlay;
        Assert.Equal(active.Raw, disk.ReadUInt32(GroupAddress, 124));
        Assert.Equal(disk.ReadUInt32(active), disk.ReadUInt32(GroupAddress, 132));
        Assert.InRange(disk.ReadUInt32(GroupAddress, 8), 1u, 192u * 1024);
        disk.FailReadBlock = disk.ReadUInt32(GroupAddress, 184);
        payload = Enumerable.Repeat((byte)0xD2, 1024).ToArray();
        Assert.True(DeferredWrite(ref disk, 1024, payload, out var handled, out var error));
        Assert.True(handled); Assert.Equal(DOS.Error.None, error);
        Assert.Equal(2u, disk.ReadUInt32(GroupAddress, 200));
        Assert.Equal(active, disk.RedoOverlay); payload.CopyTo(expected, 1024);
        disk.FailReadBlock = uint.MaxValue;
        Assert.Equal(expected, DeferredRead(ref disk, expected.Length));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, DeferredRead(ref remount, expected.Length));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstAdmissionOptionalReadFailureKeepsTheAcceptedWriteFlushable(bool scratch)
    {
        var seed = ContinuationSeed(nextExtent: true); var payload = Enumerable.Repeat((byte)0xD3, 1024).ToArray();
        Disk NewDisk()
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            return disk;
        }
        var complete = NewDisk(); Assert.True(GroupWrite(ref complete, 3, 0, payload));
        Assert.Equal(complete.RedoOverlay.Raw, complete.ReadUInt32(GroupAddress, 124));
        // The final durable lookup belongs solely to optional proof creation.
        var disk = NewDisk(); disk.FailAtDurableRead = complete.DurableReads;
        var work = TrstWork(); payload.CopyTo(disk.Ram, 4000);
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Buffer = APTR.FromPointer(4000), Length = 1024 };
        Assert.True(AmigaSmartFileSystemWriteCore.WriteTrstGroup(ref disk, ref work, ref request, GroupAddress, out var actual));
        Assert.Equal(1024u, actual); Assert.Equal(DOS.Error.None, work.Error);
        Assert.Equal(disk.FailAtDurableRead, disk.DurableReads);
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 124));
        disk.FailAtDurableRead = 0;
        var expected = seed.Bytes.ToArray(); payload.CopyTo(expected, 0);
        Assert.Equal(expected, GroupRead(ref disk, 3));
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstAdmissionDeclinesMissingSourceMapAndRebuildsAfterBoundary(bool scratch)
    {
        var seed = ContinuationSeed(3984, nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var payload = Enumerable.Repeat((byte)0xD4, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 0, payload)); payload.CopyTo(expected, 0);
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 124));
        var active = disk.RedoOverlay;
        Assert.True(GroupWrite(ref disk, 3, 8192, payload)); payload.CopyTo(expected, 8192);
        Assert.NotEqual(active, disk.RedoOverlay);
        Assert.Equal(disk.RedoOverlay.Raw, disk.ReadUInt32(GroupAddress, 124));
        Assert.True(DeferredWrite(ref disk, 16384, payload, out var handled, out _)); Assert.True(handled);
        payload.CopyTo(expected, 16384);
        Assert.Equal(expected, GroupRead(ref disk, 3));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstAdmissionSecondWriteFailurePreservesFirstWriteAndAllowsRetry(bool scratch)
    {
        var seed = ContinuationSeed(nextExtent: true);
        for (var sector = 1; sector <= 16; sector++)
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, ReturnScratchMetadataViews = scratch };
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            var first = Enumerable.Repeat((byte)0xD5, 8192).ToArray();
            Assert.True(GroupWrite(ref disk, 3, 0, first));
            var active = disk.RedoOverlay; var span = (int)disk.ReadUInt32(GroupAddress, 12);
            var before = disk.Ram.AsSpan((int)active.Raw, span).ToArray();
            var second = Enumerable.Repeat((byte)0xD6, 8192).ToArray();
            disk.FailAtWrite = disk.Writes + sector;
            Assert.False(DeferredWrite(ref disk, 8192, second, out var handled, out var error));
            Assert.False(handled); Assert.Equal(DOS.Error.DiskNotValidated, error);
            Assert.Equal(active, disk.RedoOverlay); Assert.Equal(before, disk.Ram.AsSpan((int)active.Raw, span).ToArray());
            var expected = seed.Bytes.ToArray(); first.CopyTo(expected, 0);
            Assert.Equal(expected, GroupRead(ref disk, 3));
            disk.FailAtWrite = 0; Assert.True(GroupWrite(ref disk, 3, 8192, second)); second.CopyTo(expected, 8192);
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
            var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
        }
    }

    [Fact]
    public void TrstAdmissionEarlyDeferredWriteCutsRecoverTheWholeGroup()
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var first = Enumerable.Repeat((byte)0xD7, 512).ToArray(); var second = Enumerable.Repeat((byte)0xD8, 512).ToArray();
        first.CopyTo(expected, 0); second.CopyTo(expected, 512);
        void Run(ref Disk disk)
        {
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            Assert.True(GroupWrite(ref disk, 3, 0, first));
            Assert.True(DeferredWrite(ref disk, 512, second, out var handled, out _)); Assert.True(handled);
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
                Assert.True(actual.SequenceEqual(seed.Bytes) || actual.SequenceEqual(expected), $"Partial admission group at cut {cut}");
            }
        }
    }
}
