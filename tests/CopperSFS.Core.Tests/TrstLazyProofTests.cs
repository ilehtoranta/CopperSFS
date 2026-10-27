using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void LazyProofPreservesAcceptedWritesAcrossNextWriteFailure(bool scratchView, bool failProofRead)
    {
        var seed = ContinuationSeed();
        var disk = new Disk(seed.Image.ToArray())
        { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratchView };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var expected = seed.Bytes.ToArray();
        static bool Write(ref Disk disk, uint position, byte value)
        {
            disk.Ram.AsSpan(4000, 8192).Fill(value);
            var work = TrstWork();
            var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = 3, Position = position, Buffer = APTR.FromPointer(4000), Length = 8192 };
            var success = AmigaSmartFileSystemWriteCore.WriteTrstGroup(ref disk, ref work,
                ref request, GroupAddress, out var actual, eagerContinuation: false);
            Assert.Equal(success ? 8192u : 0u, actual);
            return success;
        }
        Assert.True(Write(ref disk, 0, 0x51));
        expected.AsSpan(0, 8192).Fill(0x51);
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 124));
        Assert.Equal(expected, GroupRead(ref disk, 3));
        if (failProofRead)
        {
            disk.FailReadBlock = 12;
            Assert.False(Write(ref disk, 8192, 0x82));
            disk.FailReadBlock = uint.MaxValue;
            Assert.Equal(expected, GroupRead(ref disk, 3));
        }
        Assert.True(Write(ref disk, 8192, 0x82));
        expected.AsSpan(8192, 8192).Fill(0x82);
        Assert.Equal(expected, GroupRead(ref disk, 3));
        var flush = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref flush, GroupAddress));
        var remount = new Disk(disk.Snapshot());
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref remount, ref flush));
        Assert.Equal(expected, GroupRead(ref remount, 3));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
    }
}
