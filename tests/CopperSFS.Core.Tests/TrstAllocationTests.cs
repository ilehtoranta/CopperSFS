using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private static byte[] ReadSafeRunContents(ref Disk disk, int length)
    {
        var result = new byte[length]; var work = TrstWork();
        for (var at = 0; at < length; at += 65536)
        {
            var take = Math.Min(65536, length - at);
            Assert.Equal(take, AmigaSmartFileSystemCore.Read(ref disk, 3, (uint)at,
                APTR.FromPointer(4000), (uint)take, work.Meta, work.Data, out var error));
            Assert.Equal(DOS.Error.None, error);
            disk.Ram.AsSpan(4000, take).CopyTo(result.AsSpan(at));
        }
        return result;
    }

    [Theory]
    [InlineData(8192, false)] [InlineData(8192, true)]
    [InlineData(65536, false)] [InlineData(65536, true)]
    public void TrstSmallGroupFindsDurablyFreeReplacementInsteadOfStagingRetiredData(int packetBytes, bool scratchView)
    {
        var first = packetBytes == 8192 ? 1040 : 1300;
        var seed = ContinuationSeed(first, length: packetBytes == 8192 ? 128 : 1024);
        var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratchView };
        const uint groupBytes = AmigaSmartFileSystemWriteCore.TrstGroupHeaderBytes + 2 * (88 + 8 * (512 + 8)); // Eight slots in each arena.
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, groupBytes));
        for (var part = 0; part < 3; part++)
        {
            var payload = Enumerable.Repeat((byte)(0xC0 + part), packetBytes).ToArray();
            Assert.True(GroupWrite(ref disk, 3, (uint)(part * packetBytes), payload));
            payload.CopyTo(expected, part * packetBytes);
        }
        Assert.Equal(8u, disk.ReadUInt32(disk.RedoOverlay, 4));
        Assert.Equal(0u, disk.ReadUInt32(disk.RedoOverlay, 12));
        Assert.Equal(expected, ReadSafeRunContents(ref disk, expected.Length));
        // Every old source block must remain intact until metadata publication.
        Assert.Equal(seed.Bytes, disk.Snapshot().AsSpan(first * Bytes, expected.Length).ToArray());
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref disk, 4));
        var work = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot());
        Assert.Equal(expected, ReadSafeRunContents(ref remount, expected.Length));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
    }

    [Theory]
    [InlineData(8192, false)] [InlineData(8192, true)]
    [InlineData(65536, false)] [InlineData(65536, true)]
    public void TrstSafeRunTransferFailurePreservesAcceptedArenaAndAllowsRetry(int packetBytes, bool scratchView)
    {
        var first = packetBytes == 8192 ? 1040 : 1300;
        var seed = ContinuationSeed(first, length: packetBytes == 8192 ? 128 : 1024);
        var failures = packetBytes == 8192 ? Enumerable.Range(1, 16) : new[] { 1, 64, 128 };
        foreach (var failure in failures)
        {
            var expected = seed.Bytes.ToArray();
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratchView };
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 8696));
            for (var part = 0; part < 2; part++)
            {
                var payload = Enumerable.Repeat((byte)(0xD0 + part), packetBytes).ToArray();
                Assert.True(GroupWrite(ref disk, 3, (uint)(part * packetBytes), payload));
                payload.CopyTo(expected, part * packetBytes);
            }
            var active = disk.RedoOverlay;
            var accepted = disk.Ram.AsSpan((int)active.Raw, (int)disk.ReadUInt32(GroupAddress, 12)).ToArray();
            var next = Enumerable.Repeat((byte)0xF3, packetBytes).ToArray();
            disk.FailAtWrite = disk.Writes + failure;
            Assert.False(GroupWrite(ref disk, 3, (uint)(2 * packetBytes), next));
            Assert.Equal(active, disk.RedoOverlay);
            Assert.Equal(accepted, disk.Ram.AsSpan((int)active.Raw, accepted.Length).ToArray());
            Assert.Equal(expected, ReadSafeRunContents(ref disk, expected.Length));
            Assert.Equal(seed.Bytes, disk.Snapshot().AsSpan(first * Bytes, seed.Bytes.Length).ToArray());
            disk.FailAtWrite = 0;
            Assert.True(GroupWrite(ref disk, 3, (uint)(2 * packetBytes), next));
            next.CopyTo(expected, 2 * packetBytes);
            var work = TrstWork();
            Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
            var remount = new Disk(disk.Snapshot());
            Assert.Equal(expected, ReadSafeRunContents(ref remount, expected.Length));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstSafeRunSearchRetainsJournaledFallbackWhenOnlyRetiredRunFits(bool scratchView)
    {
        var seed = ContinuationSeed(1040);
        // Keep a 40-block initial data hole and three 30-block journal holes.
        // After the prefix only 34 originally-free data blocks remain. A
        // 36-block request fits solely by using retired source blocks in redo.
        var free = Enumerable.Range(1000, 40).Concat(Enumerable.Range(8000, 30))
            .Concat(Enumerable.Range(10000, 30)).Concat(Enumerable.Range(12000, 30)).ToHashSet();
        for (var mapIndex = 0; mapIndex < 5; mapIndex++)
        {
            var map = Block(seed.Image, 12 + mapIndex); map[12..].Clear();
            for (var bit = 0; bit < 4000; bit++)
                if (free.Contains(mapIndex * 4000 + bit)) map[12 + bit / 8] |= (byte)(128 >> (bit & 7));
            Sum(map);
        }
        U32(Block(seed.Image, 2), Bytes - 28, (uint)free.Count); Sum(Block(seed.Image, 2));
        var expected = seed.Bytes.ToArray();
        var disk = new Disk(seed.Image) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratchView };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        for (var part = 0; part < 3; part++)
        {
            var payload = Enumerable.Repeat((byte)(0xA0 + part), 1024).ToArray();
            Assert.True(GroupWrite(ref disk, 3, (uint)(part * 1024), payload));
            payload.CopyTo(expected, part * 1024);
        }
        var writes = disk.Writes; var next = Enumerable.Repeat((byte)0xBC, 36 * Bytes).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 3072, next)); next.CopyTo(expected, 3072);
        Assert.Equal(writes, disk.Writes);
        Assert.Equal(expected, GroupRead(ref disk, 3));
        Assert.Equal(seed.Bytes, disk.Snapshot().AsSpan(1040 * Bytes, seed.Bytes.Length).ToArray());
        var work = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot());
        Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Fact]
    public void TrstSafeReplacementCutsAndReorderedWritesRecoverWholeGroup()
    {
        var seed = ContinuationSeed(1003, constrainReplacementRuns: true); var expected = seed.Bytes.ToArray();
        var payloads = Enumerable.Range(0, 4).Select(n => Enumerable.Repeat((byte)(0xD0 + n), Bytes).ToArray()).ToArray();
        for (var i = 0; i < payloads.Length; i++) payloads[i].CopyTo(expected, i * Bytes);
        void Write(ref Disk disk)
        {
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 8696));
            foreach (var i in Enumerable.Range(0, 4)) Assert.True(GroupWrite(ref disk, 3, (uint)(i * Bytes), payloads[i]));
            var work = TrstWork();
            Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
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
                var actual = GroupRead(ref recovered, 3);
                Assert.True(actual.SequenceEqual(seed.Bytes) || actual.SequenceEqual(expected), $"Partial safe-run group after cut {cut}");
                Assert.Equal("hello"u8.ToArray(), GroupRead(ref recovered, 4));
            }
        }
    }
}
