using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(1040, false)] [InlineData(1040, true)]
    [InlineData(1041, false)] [InlineData(1041, true)]
    [InlineData(2000, false)] [InlineData(2000, true)]
    public void TrstPlacementPrefersLongerHoleButPreservesSufficientFirstFit(int first, bool scratch)
    {
        var seed = ContinuationSeed(first); var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var replacement = first == 2000 ? 1000u : ((uint)first + 128 + 15) & ~15u;
        APTR active = default;
        for (var i = 0; i < 3; i++)
        {
            var payload = Enumerable.Repeat((byte)(0xD1 + i), 8192).ToArray();
            Assert.True(GroupWrite(ref disk, 3, (uint)i * 8192, payload)); payload.CopyTo(expected, i * 8192);
            Assert.Equal((replacement + (uint)i * 16, 16u), disk.DataWrites[^1]);
            if (i != 0) Assert.Equal(active, disk.RedoOverlay);
            active = disk.RedoOverlay;
        }
        Assert.Equal(seed.Bytes, disk.Snapshot().AsSpan(first * Bytes, seed.Bytes.Length).ToArray());
        Assert.Equal(expected, GroupRead(ref disk, 3));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var final = disk.Snapshot(); var remount = new Disk(final);
        Assert.Equal(expected, GroupRead(ref remount, 3)); Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
        var expectedUsed = seed.Used.ToHashSet();
        foreach (var block in Enumerable.Range(first, 48)) Assert.True(expectedUsed.Remove(block));
        foreach (var block in Enumerable.Range((int)replacement, 48)) Assert.True(expectedUsed.Add(block));
        for (var block = 0; block < 16384; block++)
        {
            var map = Block(final, 12 + block / 4000); var bit = block % 4000;
            Assert.Equal(!expectedUsed.Contains(block), (map[12 + bit / 8] & (128 >> (bit & 7))) != 0);
        }
        var records = PrefixRecords(final);
        Assert.Equal(((uint)first + 48, 0x80000003u, 48), records[replacement]);
        Assert.Equal((0u, replacement, 80), records[(uint)first + 48]);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstPlacementNoLongHoleRetainsFirstFit(bool scratch)
    {
        var seed = ContinuationSeed(1040, constrainReplacementRuns: true);
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        Assert.True(GroupWrite(ref disk, 3, 0, Enumerable.Repeat((byte)0xDA, 8192).ToArray()));
        Assert.Equal((1000u, 16u), disk.DataWrites.Single());
        Assert.Equal(seed.Bytes, disk.Snapshot().AsSpan(1040 * Bytes, seed.Bytes.Length).ToArray());
    }
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void TrstPlacementOptionalLookaheadFailurePreservesValidFirstFit(bool corruptMap, bool scratch)
    {
        var seed = ContinuationSeed(1040, constrainReplacementRuns: true);
        var image = seed.Image.ToArray();
        var disk = new Disk(image) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        if (corruptMap) image[13 * Bytes + 20] ^= 1;
        else disk.FailReadBlock = 13;
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var payload = Enumerable.Repeat((byte)0xDB, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 0, payload));
        Assert.Equal((1000u, 16u), disk.DataWrites.Single());
        disk.FailReadBlock = uint.MaxValue;
        if (corruptMap) image[13 * Bytes + 20] ^= 1;
        var expected = seed.Bytes.ToArray(); payload.CopyTo(expected, 0);
        Assert.Equal(expected, GroupRead(ref disk, 3));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstPlacementLongerHoleCrashCutsAndPendingSubsetsRecoverWholeGroup(bool scratch)
    {
        var seed = ContinuationSeed(1040); var expected = seed.Bytes.ToArray();
        var payloads = Enumerable.Range(0, 3).Select(i => Enumerable.Repeat((byte)(0xE1 + i), 512).ToArray()).ToArray();
        for (var i = 0; i < 3; i++) payloads[i].CopyTo(expected, i * 512);
        void Write(ref Disk disk)
        {
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            for (var i = 0; i < 3; i++) Assert.True(GroupWrite(ref disk, 3, (uint)i * 512, payloads[i]));
            Assert.Equal((1168u, 1u), disk.DataWrites[0]);
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        }
        var baseline = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Write(ref baseline);
        for (var cut = 1; cut <= baseline.IoEvents; cut++)
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CutAfterIo = cut, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
            Assert.Throws<PowerCut>(() => Write(ref disk));
            foreach (var image in disk.CrashImages())
            {
                var remount = new Disk(image); var work = TrstWork();
                Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref remount, ref work));
                var actual = GroupRead(ref remount, 3);
                Assert.True(actual.SequenceEqual(seed.Bytes) || actual.SequenceEqual(expected), $"Torn placement group at cut {cut}");
                Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4)); PrefixRecords(remount.Snapshot());
            }
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstPlacementKeepsAnExtentWithinOneBitmapWhenALaterHoleFits(bool scratch)
    {
        // The first fit is 40 blocks. The next free hole begins at 4622, but
        // only 3378 of its blocks lie in that bitmap. The source's complete
        // 3582-block extent fits in the following bitmap, beginning at 8000.
        var seed = ContinuationSeed(1040, length: 3582); var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var payload = Enumerable.Repeat((byte)0xF1, 8192).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 0, payload)); payload.CopyTo(expected, 0);
        Assert.Equal((8000u, 16u), disk.DataWrites.Single());
        Assert.Equal(seed.Bytes, disk.Snapshot().AsSpan(1040 * Bytes, seed.Bytes.Length).ToArray());
        Assert.Equal(expected, ReadSafeRunContents(ref disk, expected.Length));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, ReadSafeRunContents(ref remount, expected.Length));
        Assert.True(AmigaSmartFileSystemCore.CountUsed(ref remount, work.Meta, out var used));
        Assert.Equal((uint)seed.Used.Count - 2, used);
    }
}
