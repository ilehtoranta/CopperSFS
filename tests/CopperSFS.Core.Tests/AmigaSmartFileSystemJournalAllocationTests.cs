using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData("delete", false)]
    [InlineData("truncate", false)]
    [InlineData("shrink", false)]
    [InlineData("delete", true)]
    [InlineData("truncate", true)]
    [InlineData("shrink", true)]
    public void PackedFileBlocksStayIntactUntilDeleteOrShrinkCommits(string operation, bool writeBack)
    {
        byte[] PackedImage()
        {
            var image = Image();
            foreach (var (source, target) in new[] { (20, 11), (21, 12), (23, 14), (24, 15) })
                Block(image, source).CopyTo(Block(image, target));
            var tree = Block(image, 5);
            U32(tree, 16, 11); U32(tree, 20, 14); U32(tree, 30, 14); U32(tree, 38, 11); U32(tree, 44, 15); Sum(tree);
            U32(Block(image, 7), 68, 11); Sum(Block(image, 7));
            U32(Block(image, 8), 36, 15); Sum(Block(image, 8));
            var bitmap = Block(image, 9);
            foreach (var number in new[] { 20, 21, 23, 24 }) bitmap[12 + number / 8] |= (byte)(0x80 >> (number % 8));
            foreach (var number in new[] { 11, 12, 14, 15 }) bitmap[12 + number / 8] &= (byte)~(0x80 >> (number % 8));
            Sum(bitmap); return image;
        }
        AmigaSmartFileSystemWriteCore.Work Setup(ref Disk disk)
        {
            var work = new AmigaSmartFileSystemWriteCore.Work
            { Meta = APTR.FromPointer(512), Data = APTR.FromPointer(1024), Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048) };
            if (writeBack)
            {
                disk.WriteBack = work.Buffered = APTR.FromPointer(65536);
                Assert.True(AmigaSmartFileSystemWriteBuffer.Initialize(ref disk, work.Buffered, Bytes, 32));
            }
            return work;
        }
        void Mutate(ref Disk disk)
        {
            var work = Setup(ref disk);
            Assert.True(operation switch
            {
                "delete" => AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3),
                "truncate" => AmigaSmartFileSystemWriteCore.Truncate(ref disk, ref work, 3),
                _ => AmigaSmartFileSystemWriteCore.Resize(ref disk, ref work, 3, 513)
            }, work.Error.ToString());
            if (writeBack) Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
        }
        var probe = new Disk(PackedImage()) { Buffered = true }; Mutate(ref probe);
        for (var cut = 1; cut <= probe.IoEvents; cut++)
        {
            var disk = new Disk(PackedImage()) { Buffered = true, CutAfterIo = cut };
            Assert.Throws<PowerCut>(() => Mutate(ref disk));
            foreach (var image in disk.CrashImages())
            {
                var restarted = new Disk(image); var work = Setup(ref restarted);
                // Recovery always starts without the lost RAM write-back buffer.
                restarted.WriteBack = APTR.Null; work.Buffered = APTR.Null;
                Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref restarted, ref work), $"{operation}, cut {cut}");
                var node = AmigaSmartFileSystemCore.Resolve(ref restarted, 1, restarted.Name("Hello"), 5, work.Meta, out var error);
                if (node != 0)
                {
                    Assert.True(AmigaSmartFileSystemCore.Describe(ref restarted, node, work.Meta, out var size, out _, out _));
                    Assert.True(size == 1300 || (operation == "truncate" && size == 0) || (operation == "shrink" && size == 513));
                    Assert.Equal((int)size, AmigaSmartFileSystemCore.Read(ref restarted, node, 0, APTR.FromPointer(4000), size,
                        work.Meta, work.Data, out error));
                    Assert.Equal(Enumerable.Range(0, (int)size).Select(i => (byte)(i * 17)), restarted.Ram.AsSpan(4000, (int)size).ToArray());
                }
                else { Assert.Equal("delete", operation); Assert.Equal(DOS.Error.ObjectNotFound, error); }
                Assert.Equal(5, AmigaSmartFileSystemCore.Read(ref restarted, 4, 0, APTR.FromPointer(4000), 5,
                    work.Meta, work.Data, out error));
                Assert.Equal("hello", System.Text.Encoding.ASCII.GetString(restarted.Ram, 4000, 5));
            }
        }
    }
}
