using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(14, 2000, false)] [InlineData(14, 2000, true)]
    [InlineData(18, 2000, false)] [InlineData(18, 2000, true)]
    [InlineData(14, 1040, false)] [InlineData(14, 1040, true)]
    [InlineData(18, 1040, false)] [InlineData(18, 1040, true)]
    public void TrstRootInstallPreservesLinksAndExtendedRecordsBeforeAndAfterTail(int stride, int first, bool scratch)
    {
        var seed = ContinuationSeed(first, nextExtent: true, constrainReplacementRuns: first == 1040); var expected = seed.Bytes.ToArray();
        var tree = Block(seed.Image, 5); var old = tree.ToArray(); tree.Slice(16).Clear(); tree[15] = (byte)stride;
        for (var i = 0; i < 3; i++)
        {
            old.AsSpan(16 + i * 14, 14).CopyTo(tree.Slice(16 + i * stride));
            tree.Slice(16 + i * stride + 14, stride - 14).Fill((byte)(0xE0 + i));
        }
        Sum(tree);
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        for (var i = 0; i < 3; i++)
        {
            var bytes = Enumerable.Repeat((byte)(0xC1 + i), 8192).ToArray();
            Assert.True(GroupWrite(ref disk, 3, (uint)(i * 8192), bytes)); bytes.CopyTo(expected, i * 8192);
        }
        Assert.Equal(expected, GroupRead(ref disk, 3));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var final = disk.Snapshot(); var remount = new Disk(final);
        Assert.Equal(expected, GroupRead(ref remount, 3)); Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
        var records = PrefixRecords(final);
        Assert.Equal((0u, (uint)first + 48, 32), records[6000]);
        var key = 1000u; var previous = 0x80000003u; var length = 0;
        while (key != 0)
        {
            var record = records[key]; Assert.Equal(previous, record.Previous);
            length += record.Length; Assert.InRange(length, 1, 128); previous = key; key = record.Next;
        }
        Assert.Equal(128, length);
        tree = Block(final, 5); var count = (int)System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(tree[12..]);
        for (var i = 0; i < count; i++)
        {
            key = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(tree[(16 + i * stride)..]);
            if (key == 24 || key == (uint)first + 48 || key == 6000)
                Assert.All(tree.Slice(16 + i * stride + 14, stride - 14).ToArray(), b => Assert.Equal(key == 24 ? (byte)0xE0 : key == 6000 ? (byte)0xE2 : (byte)0xE1, b));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstRootInstallFailureAfterPayloadLeavesTheOriginalFileAndCanRetry(bool scratch)
    {
        var seed = ContinuationSeed(4500, nextExtent: true);
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var payload = Enumerable.Repeat((byte)0xCA, 8192).ToArray(); disk.FailReadBlock = 13;
        Assert.False(GroupWrite(ref disk, 3, 0, payload)); Assert.True(disk.RedoOverlay.IsNull);
        Assert.True(disk.Writes >= 16); disk.FailReadBlock = uint.MaxValue;
        Assert.Equal(seed.Bytes, GroupRead(ref disk, 3));
        Assert.True(GroupWrite(ref disk, 3, 0, payload));
        var expected = seed.Bytes.ToArray(); payload.CopyTo(expected, 0);
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Fact]
    public void TrstRootInstallSecondReplacementCrashCutsRecoverCompleteWrites()
    {
        var seed = ContinuationSeed(1002, nextExtent: true, constrainReplacementRuns: true); var expected = seed.Bytes.ToArray();
        var payloads = Enumerable.Range(0, 3).Select(i => Enumerable.Repeat((byte)(0xCB + i), 512).ToArray()).ToArray();
        for (var i = 0; i < 3; i++) payloads[i].CopyTo(expected, i * 512);
        void Run(ref Disk disk)
        {
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            for (var i = 0; i < 3; i++) Assert.True(GroupWrite(ref disk, 3, (uint)i * 512, payloads[i]));
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
                Assert.True(actual.SequenceEqual(seed.Bytes) || actual.SequenceEqual(expected), $"Partial root installation at cut {cut}");
                Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
            }
        }
    }
}
