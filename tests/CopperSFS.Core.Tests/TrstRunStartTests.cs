using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void LazyProofAndBitmapBoundaryStartASealedRunBeforeTheNextWrite(bool boundary, bool scratch)
    {
        var seed = ContinuationSeed(boundary ? 3984 : 2000, nextExtent: true);
        // Cut every sector of the first fast Write after the new run starts.
        for (var sector = 1; sector <= 16; sector++)
        {
            var expected = seed.Bytes.ToArray();
            var disk = new Disk(seed.Image.ToArray())
            { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            static void LazyWrite(ref Disk disk, uint position, byte value)
            {
                disk.Ram.AsSpan(4000, 8192).Fill(value);
                var work = TrstWork();
                var request = new AmigaSmartFileSystemWriteCore.Request
                { Node = 3, Position = position, Buffer = APTR.FromPointer(4000), Length = 8192 };
                Assert.True(AmigaSmartFileSystemWriteCore.WriteTrstGroup(ref disk, ref work,
                    ref request, GroupAddress, out var actual, eagerContinuation: false));
                Assert.Equal(8192u, actual);
            }
            LazyWrite(ref disk, 0, 0x61); expected.AsSpan(0, 8192).Fill(0x61);
            var before = disk.RedoOverlay;
            LazyWrite(ref disk, 8192, 0x72); expected.AsSpan(8192, 8192).Fill(0x72);
            var active = disk.RedoOverlay;
            Assert.Equal(boundary, before != active);
            Assert.Equal(GroupAddress.Raw, disk.ReadUInt32(active, 12));
            Assert.Equal(16u, disk.ReadUInt32(GroupAddress, 200));
            var span = disk.ReadUInt32(GroupAddress, 8);
            Assert.InRange(span, 1u, 192u * 1024);
            var accepted = disk.Ram.AsSpan((int)GroupAddress.Raw, (int)span).ToArray();
            var payload = Enumerable.Repeat((byte)0x83, 8192).ToArray();
            disk.FailAtWrite = disk.Writes + sector;
            Assert.False(DeferredWrite(ref disk, 16384, payload, out var handled, out var error));
            Assert.False(handled); Assert.Equal(DOS.Error.DiskNotValidated, error);
            Assert.Equal(accepted, disk.Ram.AsSpan((int)GroupAddress.Raw, (int)span).ToArray());
            disk.FailAtWrite = 0;
            Assert.True(DeferredWrite(ref disk, 16384, payload, out handled, out error));
            Assert.True(handled); Assert.Equal(DOS.Error.None, error);
            payload.CopyTo(expected, 16384);
            Assert.Equal(span, disk.ReadUInt32(GroupAddress, 8));
            Assert.Equal(expected, GroupRead(ref disk, 3));
            var flush = TrstWork();
            Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref flush, GroupAddress));
            var remount = new Disk(disk.Snapshot());
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref remount, ref flush));
            Assert.Equal(expected, GroupRead(ref remount, 3));
            Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
        }
    }
}
