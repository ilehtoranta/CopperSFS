using System.Buffers.Binary;
using Amiga;
using CopperSFS;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    // Original test data, arranged as a valid tree with the target's extents
    // spanning two leaves. No bytes from the external AROS image are included.
    private static byte[] BranchedExtentImage(int depth)
    {
        var image = Image();
        var original = Block(image, 5).ToArray();
        foreach (var block in new[] { 64, 65 }) Header(image, block, Tree);
        var left = Block(image, 64);
        U16(left, 12, 1); left[14] = 1; left[15] = 14;
        original.AsSpan(16, 14).CopyTo(left[16..]); Sum(left);
        var right = Block(image, 65);
        U16(right, 12, 2); right[14] = 1; right[15] = 14;
        original.AsSpan(30, 28).CopyTo(right[16..]); Sum(right);
        Block(image, 5).Clear(); Header(image, 5, Tree);
        var branch = Block(image, 5);
        U16(branch, 12, 2); branch[15] = 8;
        U32(branch, 16, 20); U32(branch, 20, 64);
        U32(branch, 24, 23); U32(branch, 28, 65); Sum(branch);
        for (var level = 1; level < depth; level++)
        {
            var child = 65 + level;
            Block(image, 5).CopyTo(Block(image, child));
            U32(Block(image, child), 8, (uint)child); Sum(Block(image, child));
            Block(image, 5).Clear(); Header(image, 5, Tree);
            branch = Block(image, 5);
            U16(branch, 12, 1); branch[15] = 8;
            U32(branch, 16, 20); U32(branch, 20, (uint)child); Sum(branch);
        }
        U32(Block(image, 10), 28, uint.MaxValue << (32 - depth - 1)); Sum(Block(image, 10));
        return image;
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void DeleteFileAcrossExtentBranchesPreservesOtherFiles(int depth, bool buffered)
    {
        var image = BranchedExtentImage(depth);
        var disk = new Disk(image);
        var work = buffered ? BufferedWork(ref disk) : BatchWork(ref disk);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref disk, work.Meta));
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3), work.Error.ToString());
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
        var remounted = new Disk(image);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref remounted, work.Meta));
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref remounted, 1, remounted.Name("Hello"), 5, work.Meta, out var error));
        Assert.Equal(DOS.Error.ObjectNotFound, error);
        Assert.Equal(4u, AmigaSmartFileSystemCore.Resolve(ref remounted, 1, remounted.Name("Docs/Nested"), 11, work.Meta, out error));
        Assert.Equal(5, AmigaSmartFileSystemCore.Read(ref remounted, 4, 0, APTR.FromPointer(4000), 5, work.Meta, work.Data, out error));
        Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(remounted.Ram, 4000, 5));
        foreach (var freed in new[] { 20, 21, 23 })
        {
            var map = Block(image, 9);
            Assert.NotEqual(0u, BinaryPrimitives.ReadUInt32BigEndian(map[(12 + freed / 32 * 4)..]) & (1u << (31 - freed % 32)));
        }
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(3, false)]
    [InlineData(1, true)]
    [InlineData(3, true)]
    public void BranchedExtentDeletionRecoversAcrossEveryWriteAndFlush(int depth, bool buffered)
    {
        var original = BranchedExtentImage(depth);
        var before = AllocatedState(original);
        var baseline = new Disk(original.ToArray()) { Buffered = true };
        var work = buffered ? BufferedWork(ref baseline) : BatchWork(ref baseline);
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref baseline, ref work, 3), work.Error.ToString());
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref baseline, ref work), work.Error.ToString());
        var after = AllocatedState(baseline.Snapshot());
        var operations = baseline.IoEvents;
        for (var cut = 1; cut <= operations; cut++)
        {
            var disk = new Disk(original.ToArray()) { Buffered = true };
            work = buffered ? BufferedWork(ref disk) : BatchWork(ref disk);
            disk.CutAfterIo = cut;
            Assert.Throws<PowerCut>(() =>
            {
                Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3), work.Error.ToString());
                Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
            });
            var recovered = new Disk(disk.Snapshot());
            work = BatchWork(ref recovered);
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work), $"cut {cut}: {work.Error}");
            Assert.True(AmigaSmartFileSystemCore.Mount(ref recovered, work.Meta));
            var state = AllocatedState(recovered.Snapshot());
            Assert.True(state.SequenceEqual(before) || state.SequenceEqual(after), $"depth {depth}, buffered {buffered}, cut {cut}/{operations}");
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work));
        }
    }
}
