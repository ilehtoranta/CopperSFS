using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(false, false, 0)] [InlineData(true, true, 0)]
    [InlineData(false, true, 8192)] [InlineData(true, false, 8192)]
    public void TrstTransitionFirstRelocationRetainsFinalLayoutAndNextWriteReusesArena(bool nextExtent, bool scratch, int position)
    {
        var seed = ContinuationSeed(nextExtent: nextExtent); var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var first = Enumerable.Repeat((byte)0xD1, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, (uint)position, first)); first.CopyTo(expected, position);
        var active = disk.RedoOverlay;
        Assert.Equal(3u, disk.ReadUInt32(GroupAddress, 76));
        Assert.Equal((uint)position + 8192, disk.ReadUInt32(GroupAddress, 80));
        Assert.Equal(active.Raw, disk.ReadUInt32(GroupAddress, 76 + 44));
        // Journal admission now also checks the durable window before return.
        Assert.Equal(active.Raw, disk.ReadUInt32(GroupAddress, 124));
        var second = Enumerable.Repeat((byte)0xD2, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, (uint)position + 8192, second)); second.CopyTo(expected, position + 8192);
        Assert.Equal(active, disk.RedoOverlay);
        Assert.Equal(active.Raw, disk.ReadUInt32(GroupAddress, 124));
        Assert.Equal(expected, GroupRead(ref disk, 3));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
    }

    [Fact]
    public void TrstTransitionReplacementWindowChangeRetainsNewLayout()
    {
        var seed = ContinuationSeed(1040, constrainReplacementRuns: true); var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var arena = APTR.Null;
        for (var i = 0; i < 4; i++)
        {
            var payload = Enumerable.Repeat((byte)(0xD0 + i), 8192).ToArray();
            Assert.True(GroupWrite(ref disk, 3, (uint)i * 8192, payload)); payload.CopyTo(expected, i * 8192);
            if (i == 1 || i == 3) Assert.Equal(arena, disk.RedoOverlay);
            if (i == 2) Assert.NotEqual(arena, disk.RedoOverlay);
            Assert.Equal(3u, disk.ReadUInt32(GroupAddress, 76)); arena = disk.RedoOverlay;
        }
        Assert.Equal(expected, GroupRead(ref disk, 3));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstTransitionSecondWriteSectorFailuresPreserveFirstAndRetry(bool scratch)
    {
        var seed = ContinuationSeed(nextExtent: true);
        for (var sector = 1; sector <= 16; sector++)
        {
            var expected = seed.Bytes.ToArray();
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            var first = Enumerable.Repeat((byte)0xE1, 8192).ToArray();
            Assert.True(GroupWrite(ref disk, 3, 0, first)); first.CopyTo(expected, 0);
            var active = disk.RedoOverlay;
            var image = disk.Ram.AsSpan((int)active.Raw, (int)disk.ReadUInt32(GroupAddress, 12)).ToArray();
            var second = Enumerable.Repeat((byte)0xE2, 8192).ToArray(); disk.FailAtWrite = disk.Writes + sector;
            Assert.False(GroupWrite(ref disk, 3, 8192, second));
            Assert.Equal(active, disk.RedoOverlay);
            Assert.Equal(image, disk.Ram.AsSpan((int)active.Raw, image.Length).ToArray());
            disk.FailAtWrite = 0; Assert.Equal(expected, GroupRead(ref disk, 3));
            Assert.True(GroupWrite(ref disk, 3, 8192, second)); second.CopyTo(expected, 8192);
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
            var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
        }
    }

    [Fact]
    public void TrstTransitionFirstContinuationCutsAndReorderedSectorsPreserveWholeGroup()
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var first = Enumerable.Repeat((byte)0xE1, 512).ToArray(); var second = Enumerable.Repeat((byte)0xE2, 512).ToArray();
        first.CopyTo(expected, 0); second.CopyTo(expected, 512);
        void Write(ref Disk disk)
        {
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            Assert.True(GroupWrite(ref disk, 3, 0, first)); Assert.True(GroupWrite(ref disk, 3, 512, second));
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        }
        var baseline = new Disk(seed.Image.ToArray()) { Buffered = true }; Write(ref baseline);
        for (var cut = 1; cut <= baseline.IoEvents; cut++)
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CutAfterIo = cut };
            Assert.Throws<PowerCut>(() => Write(ref disk));
            foreach (var image in disk.CrashImages())
            {
                var recovered = new Disk(image); var work = TrstWork();
                Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work));
                var contents = GroupRead(ref recovered, 3);
                Assert.True(contents.SequenceEqual(seed.Bytes) || contents.SequenceEqual(expected), $"Partial transition group at cut {cut}");
                Assert.Equal("hello"u8.ToArray(), GroupRead(ref recovered, 4));
            }
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstTransitionAllocationContinuesForwardAndWrapsWhenNecessary(bool wrap)
    {
        var seed = ContinuationSeed(length: 256); var expected = seed.Bytes.ToArray();
        var free = Enumerable.Range(1000, 32).Concat(Enumerable.Range(16000, 128)).ToList();
        if (!wrap) free.AddRange(Enumerable.Range(16200, 16));
        for (var i = 0; i < 5; i++)
        {
            var map = Block(seed.Image, 12 + i); map.Slice(12).Clear();
            foreach (var block in free)
                if (block / 4000 == i) { var bit = block % 4000; map[12 + bit / 8] |= (byte)(128 >> (bit & 7)); }
            Sum(map);
        }
        U32(Block(seed.Image, 2), Bytes - 28, (uint)free.Count); Sum(Block(seed.Image, 2));
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var first = Enumerable.Repeat((byte)0xD1, 65536).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 0, first)); first.CopyTo(expected, 0);
        Assert.Equal(16000u, disk.ReadUInt32(GroupAddress, 76 + 16));
        var second = Enumerable.Repeat((byte)0xD2, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 65536, second)); second.CopyTo(expected, 65536);
        Assert.Equal(wrap ? 1000u : 16200u, disk.ReadUInt32(GroupAddress, 76 + 16));
        Assert.Equal(expected, DeferredRead(ref disk, expected.Length));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, DeferredRead(ref remount, expected.Length));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
    }
}
