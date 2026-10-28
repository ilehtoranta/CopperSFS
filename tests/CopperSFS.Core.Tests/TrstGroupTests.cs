using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private static readonly APTR GroupAddress = APTR.FromPointer(200000);

    private static (byte[] Image, uint Node, byte[] Bytes) GroupSeed(uint length)
    {
        var disk = new Disk(Image()); var work = TrstWork();
        var create = new AmigaSmartFileSystemWriteCore.Request { Directory = 1, Name = disk.Name("Group"), Length = 5 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref create, false));
        var original = Enumerable.Range(0, (int)length).Select(i => (byte)(i * 13 + 3)).ToArray();
        original.CopyTo(disk.Ram, 4000);
        var write = new AmigaSmartFileSystemWriteCore.Request { Node = create.Node, Buffer = APTR.FromPointer(4000), Length = length };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref write, out _));
        return (disk.Snapshot(), create.Node, original);
    }

    private static bool GroupWrite(ref Disk disk, uint node, uint position, byte[] bytes)
    {
        var work = TrstWork(); bytes.CopyTo(disk.Ram, 4000);
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = node, Position = position, Buffer = APTR.FromPointer(4000), Length = (uint)bytes.Length };
        var success = AmigaSmartFileSystemWriteCore.WriteTrstGroup(ref disk, ref work, ref request, GroupAddress, out var actual);
        Assert.Equal(success ? (uint)bytes.Length : 0u, actual);
        return success;
    }

    private static byte[] GroupRead(ref Disk disk, uint node)
    {
        var work = TrstWork();
        Assert.True(AmigaSmartFileSystemCore.Describe(ref disk, node, work.Meta, out var length, out _, out _));
        Assert.Equal((int)length, AmigaSmartFileSystemCore.Read(ref disk, node, 0, APTR.FromPointer(4000), length,
            work.Meta, work.Data, out var error));
        Assert.Equal(DOS.Error.None, error);
        return disk.Ram.AsSpan(4000, (int)length).ToArray();
    }

    [Theory]
    [InlineData(0u, 0u, 512u, 512)]
    [InlineData(2048u, 0u, 512u, 512)]
    [InlineData(2048u, 0u, 0u, 512)]
    [InlineData(2048u, 17u, 53u, 173)]
    public void TrstGroupCutsRecoverAllAcceptedWritesTogether(uint length, uint firstPosition, uint secondPosition, int writeBytes)
    {
        var seed = GroupSeed(length);
        var first = Enumerable.Range(0, writeBytes).Select(i => (byte)(i * 17 + 9)).ToArray();
        var second = Enumerable.Range(0, writeBytes).Select(i => (byte)(i * 23 + 7)).ToArray();
        var final = new byte[Math.Max(length, Math.Max(firstPosition, secondPosition) + writeBytes)];
        seed.Bytes.CopyTo(final, 0); first.CopyTo(final, (int)firstPosition); second.CopyTo(final, (int)secondPosition);
        void Run(ref Disk disk)
        {
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            Assert.True(GroupWrite(ref disk, seed.Node, firstPosition, first));
            Assert.True(GroupWrite(ref disk, seed.Node, secondPosition, second));
            Assert.True(AmigaSmartFileSystemWriteCore.TrstGroupPending(ref disk, GroupAddress));
            Assert.Equal(final, GroupRead(ref disk, seed.Node));
            var work = TrstWork();
            Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
            Assert.False(AmigaSmartFileSystemWriteCore.TrstGroupPending(ref disk, GroupAddress));
            Assert.True(disk.RedoOverlay.IsNull);
        }
        void Check(byte[] image, bool complete = false)
        {
            var disk = new Disk(image); var work = TrstWork();
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work));
            var read = GroupRead(ref disk, seed.Node);
            Assert.True(read.SequenceEqual(final) || !complete && read.SequenceEqual(seed.Bytes), "Group must recover entirely old or final");
        }
        var baseline = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true };
        Run(ref baseline); Check(baseline.Snapshot(), complete: true);
        for (var cut = 1; cut <= baseline.IoEvents; cut++)
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CutAfterIo = cut };
            Assert.Throws<PowerCut>(() => Run(ref disk));
            foreach (var image in disk.CrashImages()) Check(image);
        }
        // Reuse the same bounded allocation after its successful commit.
        Assert.True(GroupWrite(ref baseline, seed.Node, 0, first));
        first.CopyTo(final, 0);
        var reuseWork = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref baseline, ref reuseWork, GroupAddress));
        Check(baseline.Snapshot(), complete: true);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void TrstGroupCommitFailuresRetainAcceptedViewAndRecoverBeforeRetry(bool flushFailure)
    {
        var seed = GroupSeed(2048);
        var bytes = Enumerable.Range(0, 512).Select(i => (byte)(i * 29 + 1)).ToArray();
        var final = seed.Bytes.ToArray(); bytes.CopyTo(final, 0);
        Disk Stage()
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true };
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            Assert.True(GroupWrite(ref disk, seed.Node, 0, bytes));
            return disk;
        }
        var baseline = Stage(); var work = TrstWork();
        var start = flushFailure ? baseline.Flushes : baseline.Writes;
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref baseline, ref work, GroupAddress));
        var count = (flushFailure ? baseline.Flushes : baseline.Writes) - start;
        for (var failure = 1; failure <= count; failure++)
        {
            var disk = Stage(); work = TrstWork();
            if (flushFailure) disk.FailAtFlush = disk.Flushes + failure;
            else disk.FailAtWrite = disk.Writes + failure;
            Assert.False(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
            Assert.Equal(final, GroupRead(ref disk, seed.Node));
            foreach (var image in disk.CrashImages())
            {
                var recovered = new Disk(image); var recovery = TrstWork();
                Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref recovery));
                var read = GroupRead(ref recovered, seed.Node);
                Assert.True(read.SequenceEqual(seed.Bytes) || read.SequenceEqual(final));
            }
            disk.FailAtFlush = disk.FailAtWrite = 0;
            Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
            var remount = new Disk(disk.Snapshot());
            Assert.Equal(final, GroupRead(ref remount, seed.Node));
        }
    }

    [Fact]
    public void TrstGroupAcceptedWritesRemainFlushableWhenTheVolumeFills()
    {
        var seed = GroupSeed(0);
        var disk = new Disk(seed.Image.ToArray()) { Buffered = true };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        var bytes = Enumerable.Repeat((byte)0x9D, 512).ToArray();
        var accepted = 0u;
        while (accepted < Blocks * Bytes && GroupWrite(ref disk, seed.Node, accepted, bytes)) accepted += 512;
        Assert.InRange(accepted, 512u, (uint)(Blocks * Bytes - 512));
        Assert.Equal(Enumerable.Repeat((byte)0x9D, (int)accepted).ToArray(), GroupRead(ref disk, seed.Node));
        var work = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress), work.Error.ToString());
        var remount = new Disk(disk.Snapshot());
        Assert.Equal(Enumerable.Repeat((byte)0x9D, (int)accepted).ToArray(), GroupRead(ref remount, seed.Node));
    }

    [Fact]
    public void TrstGroupCapacityAndDataWriteFailuresPreserveEarlierAcceptedWrites()
    {
        var seed = GroupSeed(8192);
        foreach (var capacityFailure in new[] { false, true })
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true };
            const uint budget = 64 + 2 * (24 + 64 + 8 * 516);
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, budget));
            var first = Enumerable.Repeat((byte)0xA7, 512).ToArray();
            Assert.True(GroupWrite(ref disk, seed.Node, 0, first));
            var expected = seed.Bytes.ToArray(); first.CopyTo(expected, 0);
            var active = disk.RedoOverlay;
            if (!capacityFailure) disk.FailAtWrite = disk.Writes + 1;
            Assert.False(GroupWrite(ref disk, seed.Node, capacityFailure ? 1u : 512u,
                Enumerable.Repeat((byte)0xE5, capacityFailure ? 6144 : 512).ToArray()));
            Assert.Equal(active, disk.RedoOverlay);
            Assert.Equal(expected, GroupRead(ref disk, seed.Node));
            disk.FailAtWrite = 0;
            var work = TrstWork();
            Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
            var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, seed.Node));
        }
    }
}
