using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private static bool PendingWrite(ref Disk disk, uint position, byte[] bytes, out bool handled, out DOS.Error error)
    {
        bytes.CopyTo(disk.Ram, 4000);
        var r = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Position = position,
            Buffer = APTR.FromPointer(4000), Length = (uint)bytes.Length };
        var meta = TrstWork().Meta;
        return AmigaSmartFileSystemWriteCore.TryContinueTrstGroup(ref disk, GroupAddress, meta,
            APTR.Null, ref r, out handled, out error);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstPendingEntryReusesOwnedImagesAndReadsAcceptedWrites(bool scratchView)
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected, scratchView: scratchView);
        var active = disk.RedoOverlay; var bytes = disk.ReadUInt32(GroupAddress, 8);
        for (var i = 3; i < 8; i++)
        {
            var payload = Enumerable.Repeat((byte)(0xC0 + i), 1024).ToArray();
            Assert.True(PendingWrite(ref disk, (uint)(i * 1024), payload, out var handled, out var error));
            Assert.True(handled); Assert.Equal(DOS.Error.None, error);
            payload.CopyTo(expected, i * 1024);
            Assert.Equal(active, disk.RedoOverlay); Assert.Equal(bytes, disk.ReadUInt32(GroupAddress, 8));
            Assert.Equal(expected, GroupRead(ref disk, 3));
        }
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
    }

    [Theory]
    [InlineData(4)] [InlineData(8)] [InlineData(12)] [InlineData(16)] [InlineData(20)] [InlineData(24)]
    [InlineData(28)] [InlineData(32)] [InlineData(36)] [InlineData(40)] [InlineData(44)] [InlineData(48)]
    [InlineData(52)] [InlineData(56)] [InlineData(60)] [InlineData(64)] [InlineData(68)] [InlineData(72)]
    public void TrstPendingEntryRejectsCorruptProofBeforeIo(int field)
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected); var active = disk.RedoOverlay;
        var checkpoint = disk.Ram.AsSpan((int)active.Raw, (int)disk.ReadUInt32(GroupAddress, 12)).ToArray();
        var writes = disk.Writes;
        disk.WriteUInt32(GroupAddress, 124 + field, disk.ReadUInt32(GroupAddress, 124 + field) ^ 1);
        Assert.False(PendingWrite(ref disk, 3072, new byte[1024], out var handled, out var error));
        Assert.False(handled); Assert.Equal(DOS.Error.DiskNotValidated, error);
        Assert.Equal(writes, disk.Writes); Assert.Equal(active, disk.RedoOverlay);
        Assert.Equal(checkpoint, disk.Ram.AsSpan((int)active.Raw, checkpoint.Length).ToArray());
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 124));
        Assert.Equal(expected, GroupRead(ref disk, 3));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstPendingEntryDataFailuresPreserveEarlierWritesAndAllowFullRetry(bool scratchView)
    {
        var seed = ContinuationSeed(nextExtent: true);
        for (var sector = 1; sector <= 16; sector++)
        {
            var expected = seed.Bytes.ToArray(); var disk = StageTrstProof(seed.Image, expected, 8192, scratchView);
            var active = disk.RedoOverlay;
            var checkpoint = disk.Ram.AsSpan((int)active.Raw, (int)disk.ReadUInt32(GroupAddress, 12)).ToArray();
            disk.FailAtWrite = disk.Writes + sector; var payload = Enumerable.Repeat((byte)0xD7, 8192).ToArray();
            Assert.False(PendingWrite(ref disk, 24576, payload, out var handled, out var error));
            Assert.False(handled); Assert.Equal(DOS.Error.DiskNotValidated, error);
            Assert.Equal(active, disk.RedoOverlay);
            Assert.Equal(checkpoint, disk.Ram.AsSpan((int)active.Raw, checkpoint.Length).ToArray());
            Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 124)); Assert.Equal(expected, GroupRead(ref disk, 3));
            disk.FailAtWrite = 0;
            Assert.True(PendingWrite(ref disk, 24576, payload, out handled, out error)); Assert.False(handled);
            Assert.True(GroupWrite(ref disk, 3, 24576, payload)); payload.CopyTo(expected, 24576);
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
            var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    public void TrstPendingEntryDeclinesChangedOrUnavailableContinuationWithoutDataIo(int change)
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected); var active = disk.RedoOverlay;
        var position = 3072u; var length = 1024;
        if (change == 0) disk.WriteUInt32(GroupAddress, 124, 0); // No durable window proof.
        if (change == 1) disk.WriteUInt32(GroupAddress, 4, 3); // Recovery pending.
        if (change == 2) position = 8192; // Seek.
        if (change == 3) disk.WriteUInt32(GroupAddress, 60, 0); // Admission bound changed.
        if (change == 4) length = 65536; // Request exceeds file/proof window.
        if (change == 5) position++;
        if (change == 6) disk.WriteUInt32(GroupAddress, 76, 4); // Another file's cursor.
        var writes = disk.Writes;
        var checkpoint = disk.Ram.AsSpan((int)GroupAddress.Raw, (int)disk.ReadUInt32(GroupAddress, 8)).ToArray();
        Assert.True(PendingWrite(ref disk, position, new byte[length], out var handled, out var error));
        Assert.False(handled); Assert.Equal(DOS.Error.None, error); Assert.Equal(writes, disk.Writes);
        Assert.Equal(active, disk.RedoOverlay);
        Assert.Equal(checkpoint, disk.Ram.AsSpan((int)GroupAddress.Raw, checkpoint.Length).ToArray());
    }

    [Theory]
    [InlineData(0, DOS.Error.NoFreeStore)] [InlineData(1, DOS.Error.NoFreeStore)]
    [InlineData(2, DOS.Error.NoFreeStore)] [InlineData(3, DOS.Error.DiskWriteProtected)]
    [InlineData(4, DOS.Error.ActionNotKnown)] [InlineData(5, DOS.Error.BadNumber)]
    public void TrstPendingEntryChecksScratchRequestProtectionAndOverlay(int change, DOS.Error expectedError)
    {
        var seed = ContinuationSeed(); var expected = seed.Bytes.ToArray(); var disk = StageTrstProof(seed.Image, expected);
        var r = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Position = 3072, Buffer = APTR.FromPointer(4000), Length = 1024 };
        var meta = TrstWork().Meta;
        if (change == 0) meta = APTR.Null;
        if (change == 1) meta = GroupAddress;
        if (change == 2) r.Buffer = GroupAddress;
        if (change == 3) disk.Protected = true;
        if (change == 4) disk.RedoOverlay = APTR.Null;
        if (change == 5) r.Buffer = APTR.FromPointer((uint)disk.Ram.Length);
        var writes = disk.Writes;
        Assert.False(AmigaSmartFileSystemWriteCore.TryContinueTrstGroup(ref disk, GroupAddress, meta, APTR.Null,
            ref r, out var handled, out var error));
        Assert.False(handled); Assert.Equal(expectedError, error); Assert.Equal(writes, disk.Writes);
    }

    [Fact]
    public void TrstPendingEntryCutsAndReorderedSectorsKeepWholeAcceptedGroup()
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var payloads = Enumerable.Range(0, 4).Select(n => Enumerable.Repeat((byte)(0xE0 + n), 512).ToArray()).ToArray();
        for (var i = 0; i < payloads.Length; i++) payloads[i].CopyTo(expected, i * 512);
        void Write(ref Disk disk)
        {
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            for (var i = 0; i < 3; i++) Assert.True(GroupWrite(ref disk, 3, (uint)(i * 512), payloads[i]));
            Assert.True(PendingWrite(ref disk, 1536, payloads[3], out var handled, out var error));
            Assert.True(handled); Assert.Equal(DOS.Error.None, error);
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
                Assert.True(contents.SequenceEqual(seed.Bytes) || contents.SequenceEqual(expected), $"Partial pending group at cut {cut}");
                Assert.Equal("hello"u8.ToArray(), GroupRead(ref recovered, 4));
            }
        }
    }
}
