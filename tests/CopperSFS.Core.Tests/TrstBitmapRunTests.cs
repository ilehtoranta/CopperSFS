using System.Buffers.Binary;
using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private static (byte[] Image, HashSet<int> Used, int ObjectAt) BitmapRunFixture(int start, int blocks, bool blocked)
    {
        const int total = 8192;
        var image = new byte[total * Bytes]; Image().CopyTo(image, 0);
        var first = start - 1;
        var neighbor = blocked ? start + blocks / 2 : 24;
        var used = new HashSet<int> { 0, 2, 4, 5, 6, 7, 8, 10, 12, 13, 14, first, neighbor, total - 1 };
        used.UnionWith(Enumerable.Range(64, 32));
        var root = Block(image, 0);
        U32(root, 44, total * Bytes); U32(root, 48, total); U32(root, 96, 12); Sum(root);
        root.CopyTo(Block(image, total - 1));
        U32(Block(image, total - 1), 8, total - 1); Sum(Block(image, total - 1));
        for (var index = 0; index < 3; index++)
        {
            var map = Block(image, 12 + index); map.Clear(); Header(image, 12 + index, Bitmap);
            for (var at = 12; at < Bytes; at += 4) U32(map, at, uint.MaxValue);
        }
        foreach (var block in used)
        {
            var map = Block(image, 12 + block / 4000); var bit = block % 4000;
            var at = 12 + bit / 32 * 4;
            U32(map, at, BinaryPrimitives.ReadUInt32BigEndian(map[at..]) & ~(1u << (31 - bit % 32)));
        }
        for (var map = 12; map < 15; map++) Sum(Block(image, map));
        U32(Block(image, 2), Bytes - 28, (uint)(total - used.Count)); Sum(Block(image, 2));
        var contents = Block(image, 7); contents[24..].Clear();
        var objectAt = Object(contents, 24, 2, 0, 8, 128, "Docs");
        Object(contents, objectAt, 3, (uint)first, Bytes, 0, "Hello", "greeting"); Sum(contents);
        var nested = Block(image, 8); nested[24..].Clear();
        Object(nested, 24, 4, (uint)neighbor, 5, 0, "Nested"); Sum(nested);
        Block(image, first).Fill(0x41); "hello"u8.CopyTo(Block(image, neighbor));
        var tree = Block(image, 5); tree.Clear(); Header(image, 5, Tree);
        U16(tree, 12, 2); tree[14] = 1; tree[15] = 14;
        var keys = new[] { first, neighbor }.Order().ToArray();
        for (var i = 0; i < keys.Length; i++)
        { U32(tree, 16 + i * 14, (uint)keys[i]); U16(tree, 28 + i * 14, 1); }
        Sum(tree);
        return (image, used, objectAt);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)]
    [InlineData(true, false)] [InlineData(true, true)]
    public void TrstBitmapRunsPreserveEveryAllocationBitAndNeighbor(bool grouped, bool blocked)
    {
        foreach (var start in new[] { 256, 257, 271, 287, 3999, 4000, 4001, 4031 })
        foreach (var blocks in new[] { 2, 31, 32, 33, 64, 65, 127, 128 })
        {
            var seed = BitmapRunFixture(start, blocks, blocked);
            var disk = new Disk(seed.Image) { Buffered = true, CheckReadOnlyViews = true };
            if (grouped) Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            var expected = new byte[(blocks + 1) * Bytes]; Array.Fill(expected, (byte)0x41, 0, Bytes);
            for (var turn = 0; turn < 2; turn++)
            {
                var payload = Enumerable.Range(0, blocks * Bytes).Select(i => (byte)(i * 17 + 5 + turn)).ToArray();
                payload.CopyTo(expected, Bytes); payload.CopyTo(disk.Ram, 4000);
                var work = TrstWork();
                var request = new AmigaSmartFileSystemWriteCore.Request
                { Node = 3, Position = Bytes, Buffer = APTR.FromPointer(4000), Length = (uint)payload.Length };
                if (grouped)
                {
                    Assert.True(AmigaSmartFileSystemWriteCore.WriteTrstGroup(ref disk, ref work, ref request, GroupAddress, out var actual),
                        $"start={start}, blocks={blocks}, turn={turn}: {work.Error}");
                    Assert.Equal((uint)payload.Length, actual);
                    Assert.Equal(expected, GroupRead(ref disk, 3));
                    Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
                }
                else
                {
                    Assert.True(AmigaSmartFileSystemWriteCore.ExecuteTrst(ref disk, ref work, ref request,
                        AmigaSmartFileSystemWriteCore.TrstOperation.Write, GroupAddress, 192 * 1024, out var actual),
                        $"start={start}, blocks={blocks}, turn={turn}: {work.Error}");
                    Assert.Equal((uint)payload.Length, actual);
                }
                var image = disk.Snapshot(); var remount = new Disk(image);
                Assert.Equal(expected, GroupRead(ref remount, 3));
                Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
                // Reconstruct ownership from the final extent chain, then
                // compare every allocation bit, including both word edges and
                // untouched maps. This catches masks that cover adjacent bits.
                var expectedUsed = seed.Used.ToHashSet(); expectedUsed.Remove(start - 1);
                var key = BinaryPrimitives.ReadUInt32BigEndian(Block(image, 7)[(seed.ObjectAt + 12)..]);
                var fileBlocks = 0;
                while (key != 0)
                {
                    var tree = Block(image, 5); var records = BinaryPrimitives.ReadUInt16BigEndian(tree[12..]);
                    Assert.Equal(1, tree[14]); var at = 16;
                    while (at < 16 + records * 14 && BinaryPrimitives.ReadUInt32BigEndian(tree[at..]) != key) at += 14;
                    Assert.True(at < 16 + records * 14);
                    var length = BinaryPrimitives.ReadUInt16BigEndian(tree[(at + 12)..]);
                    Assert.InRange(length, 1, ushort.MaxValue);
                    for (var i = 0; i < length; i++) Assert.True(expectedUsed.Add((int)key + i));
                    fileBlocks += length; Assert.InRange(fileBlocks, 1, blocks + 1);
                    key = BinaryPrimitives.ReadUInt32BigEndian(tree[(at + 4)..]);
                }
                Assert.Equal(blocks + 1, fileBlocks);
                for (var block = 0; block < 8192; block++)
                {
                    var bit = block % 4000; var map = Block(image, 12 + block / 4000);
                    var word = BinaryPrimitives.ReadUInt32BigEndian(map[(12 + bit / 32 * 4)..]);
                    Assert.Equal(expectedUsed.Contains(block), (word & (1u << (31 - bit % 32))) == 0);
                }
                Assert.True(AmigaSmartFileSystemCore.CountUsed(ref remount, work.Meta, out var used));
                Assert.Equal((uint)(expectedUsed.Count - 2), used);
            }
        }
    }
}
