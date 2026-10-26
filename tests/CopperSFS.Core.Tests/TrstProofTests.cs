using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private static Disk StageTrstProof(byte[] image, byte[] expected, int packetBytes = 1024, bool scratchView = false)
    {
        var disk = StageContinuation(image, expected, packetBytes, scratchView);
        var payload = Enumerable.Repeat((byte)0xB7, packetBytes).ToArray();
        Assert.True(GroupWrite(ref disk, 3, (uint)(packetBytes * 2), payload));
        payload.CopyTo(expected, packetBytes * 2);
        Assert.Equal(disk.RedoOverlay.Raw, disk.ReadUInt32(GroupAddress, 124));
        return disk;
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstProofReusesDurableFreeWindowWithoutReadingTheOriginalBitmap(bool scratchView)
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected, scratchView: scratchView);
        var active = disk.RedoOverlay;
        disk.FailReadBlock = disk.ReadUInt32(GroupAddress, 124 + 60);
        var payload = Enumerable.Repeat((byte)0xC8, 1024).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 3072, payload)); payload.CopyTo(expected, 3072);
        Assert.Equal(active, disk.RedoOverlay);
        Assert.Equal(expected, GroupRead(ref disk, 3));
        disk.FailReadBlock = uint.MaxValue;
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 124));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Theory]
    [InlineData(4)] [InlineData(8)] [InlineData(12)] [InlineData(16)] [InlineData(20)] [InlineData(24)]
    [InlineData(28)] [InlineData(32)] [InlineData(36)] [InlineData(40)] [InlineData(44)] [InlineData(48)]
    [InlineData(52)] [InlineData(56)] [InlineData(60)] [InlineData(64)] [InlineData(68)] [InlineData(72)]
    public void TrstProofRejectsCorruptPrivateCertificateBeforeDataIo(int field)
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected);
        var active = disk.RedoOverlay; var writes = disk.Writes;
        disk.WriteUInt32(GroupAddress, 124 + field, disk.ReadUInt32(GroupAddress, 124 + field) ^ 1);
        Assert.False(GroupWrite(ref disk, 3, 3072, new byte[1024]));
        Assert.Equal(writes, disk.Writes); Assert.Equal(active, disk.RedoOverlay);
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 124));
        Assert.Equal(expected, GroupRead(ref disk, 3));
    }

    [Fact]
    public void TrstProofFallsBackWhenTheOwnedSlotSetChanges()
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected);
        var active = disk.RedoOverlay; var work = TrstWork();
        // Add an unchanged valid bitmap image through the arena API. The
        // certificate may no longer reuse its old journal-space admission.
        Assert.True(disk.ReadBlock(13, work.Journal));
        Assert.True(AmigaSmartFileSystemRedoArena.Store(ref disk, active, 13, work.Journal));
        var payload = Enumerable.Repeat((byte)0xC9, 1024).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 3072, payload)); payload.CopyTo(expected, 3072);
        Assert.NotEqual(active, disk.RedoOverlay);
        Assert.Equal(disk.RedoOverlay.Raw, disk.ReadUInt32(GroupAddress, 124));
        Assert.Equal(disk.ReadUInt32(disk.RedoOverlay), disk.ReadUInt32(GroupAddress, 124 + 8));
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Fact]
    public void TrstProofFallsBackWhenTheOriginalFreeBoundChanges()
    {
        var seed = ContinuationSeed(); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected); var active = disk.RedoOverlay;
        disk.WriteUInt32(GroupAddress, 60, 0);
        var payload = Enumerable.Repeat((byte)0xCB, 1024).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 3072, payload)); payload.CopyTo(expected, 3072);
        Assert.NotEqual(active, disk.RedoOverlay);
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Fact]
    public void TrstProofWindowStopsBeforeRetiredButDurableReachableBlocks()
    {
        var seed = ContinuationSeed(1040, constrainReplacementRuns: true); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected); var active = disk.RedoOverlay;
        var payload = Enumerable.Repeat((byte)0xCE, 36 * 512).ToArray();
        // The proof has only 34 originally-free blocks left. The current
        // bitmap has 40 free blocks because earlier Writes retired source data.
        Assert.True(GroupWrite(ref disk, 3, 3072, payload)); payload.CopyTo(expected, 3072);
        Assert.NotEqual(active, disk.RedoOverlay);
        Assert.Equal(seed.Bytes, disk.Snapshot().AsSpan(1040 * Bytes, seed.Bytes.Length).ToArray());
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstProofCachedDataFailurePreservesAcceptedArenaAndInvalidatesProof(bool scratchView)
    {
        var seed = ContinuationSeed(nextExtent: true);
        for (var failure = 1; failure <= 16; failure++)
        {
            var expected = seed.Bytes.ToArray(); var disk = StageTrstProof(seed.Image, expected, 8192, scratchView);
            var active = disk.RedoOverlay; var span = (int)disk.ReadUInt32(GroupAddress, 12);
            var checkpoint = disk.Ram.AsSpan((int)active.Raw, span).ToArray();
            disk.FailAtWrite = disk.Writes + failure;
            var payload = Enumerable.Repeat((byte)0xF9, 8192).ToArray();
            Assert.False(GroupWrite(ref disk, 3, 24576, payload));
            Assert.Equal(active, disk.RedoOverlay); Assert.Equal(checkpoint, disk.Ram.AsSpan((int)active.Raw, span).ToArray());
            Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 124));
            Assert.Equal(expected, GroupRead(ref disk, 3));
            disk.FailAtWrite = 0;
            Assert.True(GroupWrite(ref disk, 3, 24576, payload)); payload.CopyTo(expected, 24576);
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
            var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
        }
    }

    [Fact]
    public void TrstProofCachedWriteCutsAndReorderedSectorsRecoverWholeGroup()
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var payloads = Enumerable.Range(0, 4).Select(n => Enumerable.Repeat((byte)(0xE0 + n), 512).ToArray()).ToArray();
        for (var i = 0; i < payloads.Length; i++) payloads[i].CopyTo(expected, i * 512);
        void Write(ref Disk disk)
        {
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            APTR accepted = APTR.Null;
            for (var i = 0; i < 4; i++)
            {
                Assert.True(GroupWrite(ref disk, 3, (uint)(i * 512), payloads[i]));
                if (i == 2) accepted = disk.RedoOverlay;
                if (i == 3) Assert.Equal(accepted, disk.RedoOverlay);
            }
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
                Assert.True(contents.SequenceEqual(seed.Bytes) || contents.SequenceEqual(expected), $"Partial cached group after cut {cut}");
                Assert.Equal("hello"u8.ToArray(), GroupRead(ref recovered, 4));
            }
        }
    }
}
