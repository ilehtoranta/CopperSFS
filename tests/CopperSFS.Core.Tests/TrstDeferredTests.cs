using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private static bool DeferredWrite(ref Disk disk, uint position, byte[] bytes, out bool handled, out DOS.Error error)
    {
        bytes.CopyTo(disk.Ram, 4000);
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Position = position,
            Buffer = APTR.FromPointer(4000), Length = (uint)bytes.Length };
        return AmigaSmartFileSystemWriteCore.TryContinueTrstGroup(ref disk, GroupAddress, TrstWork().Meta,
            APTR.Null, ref request, out handled, out error, true);
    }

    private static byte[] DeferredRead(ref Disk disk, int length)
    {
        var result = new byte[length]; var work = TrstWork();
        for (var position = 0; position < length; position += 65536)
        {
            var count = Math.Min(65536, length - position);
            Assert.Equal(count, AmigaSmartFileSystemCore.Read(ref disk, 3, (uint)position,
                APTR.FromPointer(4000), (uint)count, work.Meta, work.Data, out var error));
            Assert.Equal(DOS.Error.None, error);
            disk.Ram.AsSpan(4000, count).CopyTo(result.AsSpan(position));
        }
        return result;
    }

    [Theory]
    [InlineData(1024, false)] [InlineData(1024, true)]
    [InlineData(8192, false)] [InlineData(65536, true)]
    public void TrstDeferredRunKeepsImagesUntilReadAndPreservesBoundedMemory(int packet, bool scratch)
    {
        var seed = ContinuationSeed(nextExtent: true, length: 1024); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected, packet, scratch); var active = disk.RedoOverlay;
        var span = disk.ReadUInt32(GroupAddress, 8); Assert.InRange(span, 1u, 192u * 1024);
        var slotBase = disk.ReadUInt32(active, 20); var length = disk.ReadUInt32(active) * (512 + 8);
        var images = disk.Ram.AsSpan((int)slotBase, (int)length).ToArray();
        for (var i = 3; i < 6; i++)
        {
            var payload = Enumerable.Repeat((byte)(0xC0 + i), packet).ToArray();
            Assert.True(DeferredWrite(ref disk, (uint)(i * packet), payload, out var handled, out var error));
            Assert.True(handled); Assert.Equal(DOS.Error.None, error); payload.CopyTo(expected, i * packet);
            Assert.Equal(active, disk.RedoOverlay); Assert.Equal(span, disk.ReadUInt32(GroupAddress, 8));
            Assert.Equal(GroupAddress.Raw, disk.ReadUInt32(active, 12));
            Assert.Equal((uint)((i - 2) * packet / Bytes), disk.ReadUInt32(GroupAddress, 200));
            Assert.Equal(images, disk.Ram.AsSpan((int)slotBase, (int)length).ToArray());
        }
        Assert.Equal(expected, DeferredRead(ref disk, expected.Length));
        Assert.Equal(0u, disk.ReadUInt32(active, 12)); Assert.Equal(0x80000200u, disk.ReadUInt32(active, 8));
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 200));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, DeferredRead(ref remount, expected.Length));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    [InlineData(4)] [InlineData(5)] [InlineData(6)] [InlineData(7)]
    public void TrstDeferredRunMaterializesAtEveryAccessAndMutationBoundary(int boundary)
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected); var active = disk.RedoOverlay;
        for (var i = 3; i < 5; i++)
        {
            var payload = Enumerable.Repeat((byte)(0xD0 + i), 1024).ToArray();
            Assert.True(DeferredWrite(ref disk, (uint)i * 1024, payload, out var handled, out _)); Assert.True(handled);
            payload.CopyTo(expected, i * 1024);
        }
        var block = disk.ReadUInt32(GroupAddress, 76 + 28); var work = TrstWork();
        if (boundary <= 1)
        {
            var image = AmigaSmartFileSystemRedoArena.MetadataView(ref disk, active, block, Tree, boundary == 1, out var found);
            Assert.True(found); Assert.True(image.IsNotNull);
        }
        if (boundary == 2) Assert.True(AmigaSmartFileSystemRedoArena.Find(ref disk, active, block).IsNotNull);
        if (boundary == 3)
        {
            // Exercise storing an already-owned image. Materialization must
            // update it before Store uses that same stable owned address.
            var image = APTR.FromPointer(disk.ReadUInt32(active, 20) + disk.ReadUInt32(GroupAddress, 124 + 24) * (512 + 8) + 8);
            Assert.True(AmigaSmartFileSystemRedoArena.Store(ref disk, active, block, image));
        }
        if (boundary == 4)
            Assert.True(AmigaSmartFileSystemRedoArena.Clone(ref disk, active,
                APTR.FromPointer(disk.ReadUInt32(GroupAddress, 20)), disk.ReadUInt32(GroupAddress, 12)));
        if (boundary == 5)
        {
            var payload = Enumerable.Repeat((byte)0xE5, 1024).ToArray();
            Assert.True(GroupWrite(ref disk, 3, 5120, payload)); payload.CopyTo(expected, 5120);
        }
        if (boundary == 6) Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        if (boundary == 7)
        {
            var payload = Enumerable.Repeat((byte)0xE7, 1024).ToArray();
            Assert.True(PendingWrite(ref disk, 5120, payload, out var handled, out _)); Assert.True(handled);
            payload.CopyTo(expected, 5120);
        }
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 200)); Assert.Equal(0u, disk.ReadUInt32(active, 12));
        Assert.Equal(expected, DeferredRead(ref disk, expected.Length));
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, DeferredRead(ref remount, expected.Length));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstDeferredRunFailedPayloadPreservesAcceptedDescriptorAndImages(bool scratch)
    {
        var seed = ContinuationSeed(nextExtent: true);
        for (var sector = 1; sector <= 16; sector++)
        {
            var expected = seed.Bytes.ToArray(); var disk = StageTrstProof(seed.Image, expected, 8192, scratch);
            var payload = Enumerable.Repeat((byte)0xD9, 8192).ToArray();
            Assert.True(DeferredWrite(ref disk, 24576, payload, out var handled, out _)); Assert.True(handled);
            payload.CopyTo(expected, 24576);
            var before = disk.Ram.AsSpan((int)GroupAddress.Raw, (int)disk.ReadUInt32(GroupAddress, 8)).ToArray();
            var failed = Enumerable.Repeat((byte)0xEA, 8192).ToArray(); disk.FailAtWrite = disk.Writes + sector;
            Assert.False(DeferredWrite(ref disk, 32768, failed, out handled, out var error));
            Assert.False(handled); Assert.Equal(DOS.Error.DiskNotValidated, error);
            Assert.Equal(before, disk.Ram.AsSpan((int)GroupAddress.Raw, before.Length).ToArray());
            disk.FailAtWrite = 0; Assert.Equal(expected, DeferredRead(ref disk, expected.Length));
            Assert.True(DeferredWrite(ref disk, 32768, failed, out handled, out _)); Assert.True(handled);
            failed.CopyTo(expected, 32768);
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
            var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, DeferredRead(ref remount, expected.Length));
        }
    }

    [Theory]
    [InlineData(4)] [InlineData(8)] [InlineData(12)] [InlineData(16)] [InlineData(52)] [InlineData(60)]
    [InlineData(76)] [InlineData(80)] [InlineData(124)] [InlineData(144)] [InlineData(196)]
    [InlineData(200)] [InlineData(204)] [InlineData(208)] [InlineData(212)] [InlineData(216)]
    public void TrstDeferredRunRejectsDamagedPrivateStateBeforeFurtherIo(int offset)
    {
        var seed = ContinuationSeed(); var expected = seed.Bytes.ToArray(); var disk = StageTrstProof(seed.Image, expected);
        var payload = Enumerable.Repeat((byte)0xDF, 1024).ToArray();
        Assert.True(DeferredWrite(ref disk, 3072, payload, out var handled, out _)); Assert.True(handled);
        payload.CopyTo(expected, 3072); var writes = disk.Writes;
        var old = disk.ReadUInt32(GroupAddress, offset); disk.WriteUInt32(GroupAddress, offset, old ^ 1);
        Assert.False(DeferredWrite(ref disk, 4096, payload, out handled, out var error));
        Assert.False(handled); Assert.NotEqual(DOS.Error.None, error); Assert.Equal(writes, disk.Writes);
        disk.WriteUInt32(GroupAddress, offset, old);
        Assert.Equal(expected, DeferredRead(ref disk, expected.Length));
    }

    [Theory]
    [InlineData(0, 1u)] [InlineData(4, 1u)] [InlineData(8, 0x40000000u)] [InlineData(8, 0x80000000u)]
    [InlineData(12, 200000u)] [InlineData(16, 1u)] [InlineData(20, 4u)]
    public void TrstDeferredRunRejectsDamagedArenaAndDoesNotReadOldDiskState(int offset, uint xor)
    {
        var seed = ContinuationSeed(); var expected = seed.Bytes.ToArray(); var disk = StageTrstProof(seed.Image, expected);
        var payload = Enumerable.Repeat((byte)0xDF, 1024).ToArray();
        Assert.True(DeferredWrite(ref disk, 3072, payload, out var handled, out _)); Assert.True(handled);
        payload.CopyTo(expected, 3072); var writes = disk.Writes; var active = disk.RedoOverlay;
        var old = disk.ReadUInt32(active, offset); disk.WriteUInt32(active, offset, old ^ xor);
        Assert.False(DeferredWrite(ref disk, 4096, payload, out handled, out var error));
        Assert.False(handled); Assert.NotEqual(DOS.Error.None, error); Assert.Equal(writes, disk.Writes);
        Assert.False(disk.ReadBlock(7, TrstWork().Meta));
        disk.WriteUInt32(active, offset, old); Assert.Equal(expected, DeferredRead(ref disk, expected.Length));
    }

    [Fact]
    public void TrstDeferredRunCutsAndReorderedSectorsKeepWholeAcceptedGroup()
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var payloads = Enumerable.Range(0, 6).Select(n => Enumerable.Repeat((byte)(0xE0 + n), 512).ToArray()).ToArray();
        for (var i = 0; i < payloads.Length; i++) payloads[i].CopyTo(expected, i * 512);
        void Write(ref Disk disk)
        {
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            for (var i = 0; i < 3; i++) Assert.True(GroupWrite(ref disk, 3, (uint)(i * 512), payloads[i]));
            for (var i = 3; i < 6; i++)
            {
                Assert.True(DeferredWrite(ref disk, (uint)(i * 512), payloads[i], out var handled, out _)); Assert.True(handled);
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
                Assert.True(contents.SequenceEqual(seed.Bytes) || contents.SequenceEqual(expected), $"Partial deferred group at cut {cut}");
                Assert.Equal("hello"u8.ToArray(), GroupRead(ref recovered, 4));
            }
        }
    }

    [Theory]
    [InlineData(2000)] [InlineData(4500)]
    public void TrstDeferredRunStopsBeforeLaterActiveAllocation(int first)
    {
        var seed = ContinuationSeed(first, nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected, 8192); var active = disk.RedoOverlay;
        var bitmapBlock = disk.ReadUInt32(GroupAddress, 124 + 60);
        var bit = disk.ReadUInt32(GroupAddress, 124 + 68) +
            disk.ReadUInt32(GroupAddress, 76 + 8) - disk.ReadUInt32(GroupAddress, 124 + 12) + 20;
        var offset = 12 + (int)(bit >> 5) * 4; var mask = 0x80000000u >> (int)(bit & 31);
        var bitmap = AmigaSmartFileSystemRedoArena.MetadataView(ref disk, active, bitmapBlock, Bitmap, true, out var found);
        Assert.True(found); Assert.True(bitmap.IsNotNull); Assert.NotEqual(0u, disk.ReadUInt32(bitmap, offset) & mask);
        disk.WriteUInt32(bitmap, offset, disk.ReadUInt32(bitmap, offset) & ~mask);
        Sum(disk.Ram.AsSpan((int)bitmap.Raw, Bytes));
        var payload = Enumerable.Repeat((byte)0xF1, 8192).ToArray();
        Assert.True(DeferredWrite(ref disk, 24576, payload, out var handled, out _)); Assert.True(handled);
        payload.CopyTo(expected, 24576); Assert.Equal(20u, disk.ReadUInt32(GroupAddress, 212));
        var writes = disk.Writes;
        Assert.True(DeferredWrite(ref disk, 32768, payload, out handled, out var error));
        Assert.False(handled); Assert.Equal(DOS.Error.None, error); Assert.Equal(writes, disk.Writes);
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 200));
        // Remove the synthetic allocation without undoing the accepted run's
        // newly allocated bits. No invalid fixture metadata is committed.
        bitmap = AmigaSmartFileSystemRedoArena.MetadataView(ref disk, active, bitmapBlock, Bitmap, true, out found);
        disk.WriteUInt32(bitmap, offset, disk.ReadUInt32(bitmap, offset) | mask);
        Sum(disk.Ram.AsSpan((int)bitmap.Raw, Bytes));
        Assert.Equal(expected, DeferredRead(ref disk, expected.Length));
        Assert.True(GroupWrite(ref disk, 3, 32768, payload)); payload.CopyTo(expected, 32768);
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, DeferredRead(ref remount, expected.Length));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)]
    public void TrstDeferredRunMaterializesBeforeDecliningChangedRequest(int change)
    {
        var seed = ContinuationSeed(); var expected = seed.Bytes.ToArray(); var disk = StageTrstProof(seed.Image, expected);
        var payload = Enumerable.Repeat((byte)0xF2, 1024).ToArray();
        Assert.True(DeferredWrite(ref disk, 3072, payload, out var handled, out _)); Assert.True(handled);
        payload.CopyTo(expected, 3072); var writes = disk.Writes;
        var request = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Position = 4096, Length = 1024, Buffer = APTR.FromPointer(4000) };
        if (change == 0) request.Position += 1024;
        if (change == 1) request.Length = 513;
        if (change == 2) request.Node = 4;
        if (change == 3) request.Length = 0;
        if (change == 4) request.Length = 65537;
        if (change == 5) request.Buffer = APTR.FromPointer(4001);
        Assert.True(AmigaSmartFileSystemWriteCore.TryContinueTrstGroup(ref disk, GroupAddress,
            TrstWork().Meta, APTR.Null, ref request, out handled, out var error, true));
        Assert.False(handled); Assert.Equal(DOS.Error.None, error); Assert.Equal(writes, disk.Writes);
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 200)); Assert.Equal(expected, DeferredRead(ref disk, expected.Length));
    }

    [Fact]
    public void TrstDeferredRunRejectsChangedProofEvenWithRecomputedProofChecksum()
    {
        var seed = ContinuationSeed(); var expected = seed.Bytes.ToArray(); var disk = StageTrstProof(seed.Image, expected);
        var payload = Enumerable.Repeat((byte)0xF3, 1024).ToArray();
        Assert.True(DeferredWrite(ref disk, 3072, payload, out var handled, out _)); Assert.True(handled);
        payload.CopyTo(expected, 3072); var writes = disk.Writes;
        var old = disk.ReadUInt32(GroupAddress, 124 + 16); var seal = disk.ReadUInt32(GroupAddress, 124 + 72);
        disk.WriteUInt32(GroupAddress, 124 + 16, old + 1);
        disk.WriteUInt32(GroupAddress, 124 + 72, unchecked(seal - 1));
        Assert.False(DeferredWrite(ref disk, 4096, payload, out handled, out var error));
        Assert.False(handled); Assert.Equal(DOS.Error.DiskNotValidated, error); Assert.Equal(writes, disk.Writes);
        disk.WriteUInt32(GroupAddress, 124 + 16, old); disk.WriteUInt32(GroupAddress, 124 + 72, seal);
        Assert.Equal(expected, DeferredRead(ref disk, expected.Length));
    }
}
