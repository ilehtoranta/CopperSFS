using System.Buffers.Binary;
using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private static (byte[] Image, byte[] Bytes, uint Replacement) ExtentBoundaryFixture(int layout)
    {
        var image = Image();
        var reversed = layout == 3;
        var before = reversed ? 60 : 20;
        var source = reversed ? 30 : 40;
        var after = reversed ? 40 : 60;
        var contents = Block(image, 7);
        var at = Object(contents, 24, 2, 0, 8, 128, "Docs");
        Object(contents, at, 3, (uint)before, 12 * Bytes, 0, "Hello", "greeting");
        Sum(contents);
        var bytes = Enumerable.Range(0, 12 * Bytes).Select(i => (byte)(i * 13 + i / Bytes + 3)).ToArray();
        bytes.AsSpan(0, 2 * Bytes).CopyTo(image.AsSpan(before * Bytes));
        bytes.AsSpan(2 * Bytes, 8 * Bytes).CopyTo(image.AsSpan(source * Bytes));
        bytes.AsSpan(10 * Bytes, 2 * Bytes).CopyTo(image.AsSpan(after * Bytes));
        var bitmap = Block(image, 9);
        foreach (var block in new[] { 20, 21, 23 })
        {
            var offset = 12 + block / 32 * 4;
            U32(bitmap, offset, BinaryPrimitives.ReadUInt32BigEndian(bitmap[offset..]) | (1u << (31 - block % 32)));
        }
        foreach (var block in Enumerable.Range(before, 2).Concat(Enumerable.Range(source, 8)).Concat(Enumerable.Range(after, 2)))
        {
            var offset = 12 + block / 32 * 4;
            U32(bitmap, offset, BinaryPrimitives.ReadUInt32BigEndian(bitmap[offset..]) & ~(1u << (31 - block % 32)));
        }
        Sum(bitmap);
        U32(Block(image, 2), Bytes - 28, 73); Sum(Block(image, 2));
        var records = new Dictionary<int, (uint Next, uint Previous, ushort Length)>
        {
            [before] = ((uint)source, 0, 2), [source] = ((uint)after, (uint)before, 8),
            [after] = (0, (uint)source, 2), [24] = (0, 0, 1),
        };
        var leaves = layout switch
        {
            0 => new[] { new[] { 20, 24, 40, 60 } },
            1 => new[] { new[] { 20, 24, 40 }, new[] { 60 } },
            2 => new[] { new[] { 20, 24 }, new[] { 40, 60 } },
            3 => new[] { new[] { 24, 30 }, new[] { 40, 60 } },
            _ => new[] { new[] { 20, 24 }, new[] { 40 }, new[] { 60 } },
        };
        for (var leaf = 0; leaf < leaves.Length; leaf++)
        {
            var block = leaves.Length == 1 ? 5 : 64 + leaf;
            var data = Block(image, block); data.Clear(); Header(image, block, Tree);
            U16(data, 12, (ushort)leaves[leaf].Length); data[14] = 1; data[15] = 14;
            for (var i = 0; i < leaves[leaf].Length; i++)
            {
                var key = leaves[leaf][i]; var record = records[key]; var offset = 16 + i * 14;
                U32(data, offset, (uint)key); U32(data, offset + 4, record.Next);
                U32(data, offset + 8, record.Previous); U16(data, offset + 12, record.Length);
            }
            Sum(data);
        }
        if (leaves.Length != 1)
        {
            var tree = Block(image, 5); tree.Clear(); Header(image, 5, Tree);
            U16(tree, 12, (ushort)leaves.Length); tree[15] = 8;
            for (var i = 0; i < leaves.Length; i++)
            { U32(tree, 16 + i * 8, i == 0 ? 0u : (uint)leaves[i][0]); U32(tree, 20 + i * 8, (uint)(64 + i)); }
            Sum(tree);
            U32(Block(image, 10), 28, uint.MaxValue << (32 - leaves.Length)); Sum(Block(image, 10));
        }
        return (image, bytes, (uint)before + 2);
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    [InlineData(3, false)] [InlineData(3, true)]
    [InlineData(4, false)] [InlineData(4, true)]
    public void TrstContinuationLeafLayoutsRecoverWholeWrite(int layout, bool grouped)
    {
        var seed = ExtentBoundaryFixture(layout);
        var payload = Enumerable.Range(0, 2 * Bytes).Select(i => (byte)(i * 17 + 5)).ToArray();
        var expected = seed.Bytes.ToArray(); payload.CopyTo(expected, 2 * Bytes);
        void Run(ref Disk disk)
        {
            var work = TrstWork();
            if (grouped)
            {
                Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
                Assert.True(GroupWrite(ref disk, 3, 2 * Bytes, payload));
                Assert.Equal(expected, GroupRead(ref disk, 3));
                Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
            }
            else
            {
                payload.CopyTo(disk.Ram, 4000);
                var request = new AmigaSmartFileSystemWriteCore.Request
                { Node = 3, Position = 2 * Bytes, Buffer = APTR.FromPointer(4000), Length = 2 * Bytes };
                Assert.True(AmigaSmartFileSystemWriteCore.ExecuteTrst(ref disk, ref work, ref request,
                    AmigaSmartFileSystemWriteCore.TrstOperation.Write, GroupAddress, 192 * 1024, out var actual));
                Assert.Equal((uint)payload.Length, actual);
            }
        }
        void Check(byte[] image, bool complete = false)
        {
            var disk = new Disk(image); var work = TrstWork();
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work));
            var bytes = GroupRead(ref disk, 3);
            Assert.True(bytes.SequenceEqual(expected) || !complete && bytes.SequenceEqual(seed.Bytes), "Torn extent-boundary update");
            Assert.True(AmigaSmartFileSystemCore.CountUsed(ref disk, work.Meta, out var used));
            Assert.Equal((uint)(Blocks - 2 - 73), used);
            Assert.Equal(5, AmigaSmartFileSystemCore.Read(ref disk, 4, 0, APTR.FromPointer(4000), 5,
                work.Meta, work.Data, out var error));
            Assert.Equal(DOS.Error.None, error);
            Assert.Equal("hello"u8.ToArray(), disk.Ram.AsSpan(4000, 5).ToArray());
        }
        Check(seed.Image);
        var baseline = new Disk(seed.Image.ToArray()) { Buffered = true, CheckReadOnlyViews = true };
        Run(ref baseline); Check(baseline.Snapshot(), complete: true);
        Assert.Contains(baseline.DataWrites, run => run.Block == seed.Replacement && run.Count == 2);
        for (var cut = 1; cut <= baseline.IoEvents; cut++)
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CutAfterIo = cut, CheckReadOnlyViews = true };
            Assert.Throws<PowerCut>(() => Run(ref disk));
            foreach (var image in disk.CrashImages()) Check(image);
        }
    }
}
