using Amiga;
using System.Numerics;
using System.Reflection;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(512, false)] [InlineData(512, true)]
    [InlineData(1024, false)] [InlineData(1024, true)]
    [InlineData(4096, false)] [InlineData(4096, true)]
    [InlineData(32768, false)] [InlineData(32768, true)]
    public void TrstBitmapWordsIntersectEveryAllocationViewAndPreserveInputs(int bytes, bool checkpoint)
    {
        var method = typeof(AmigaSmartFileSystemWriteCore).GetMethod("IntersectTrstBitmapWords", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(Disk));
        var output = APTR.FromPointer(1024); var durable = APTR.FromPointer(100000); var accepted = APTR.FromPointer(200000);
        for (var pattern = 0; pattern < 5; pattern++)
        {
            var disk = new Disk(Image()); var random = new Random(0x52B0 + pattern);
            var candidate = new byte[bytes]; var original = new byte[bytes]; var prior = new byte[bytes];
            random.NextBytes(candidate); random.NextBytes(original); random.NextBytes(prior);
            // Each input independently vetoes a free bit. In particular,
            // accepted unpublished allocations must survive candidate frees.
            if (pattern == 0) candidate.AsSpan(12).Clear();
            if (pattern == 1) original.AsSpan(12).Clear();
            if (pattern == 2) prior.AsSpan(12).Clear();
            if (pattern == 3)
            { candidate.AsSpan(12).Fill(255); original.AsSpan(12).Fill(255); prior.AsSpan(12).Fill(255); }
            candidate.CopyTo(disk.Ram, (int)output.Raw); original.CopyTo(disk.Ram, (int)durable.Raw); prior.CopyTo(disk.Ram, (int)accepted.Raw);
            disk.WriteUInt32(output, -4, 0x89ABCDEF); disk.WriteUInt32(output, bytes, 0xFEDCBA98);
            var expected = candidate.ToArray();
            // Byte-wise oracle is independent of the unrolled word traversal.
            for (var i = 12; i < bytes; i++) expected[i] = (byte)(candidate[i] & original[i] & (checkpoint ? prior[i] : 255));
            method.Invoke(null, new object[] { disk, output, durable, checkpoint ? accepted : APTR.Null, (uint)bytes });
            Assert.Equal(expected, disk.Ram.AsSpan((int)output.Raw, bytes).ToArray());
            Assert.Equal(original, disk.Ram.AsSpan((int)durable.Raw, bytes).ToArray());
            Assert.Equal(prior, disk.Ram.AsSpan((int)accepted.Raw, bytes).ToArray());
            Assert.Equal(0x89ABCDEFu, disk.ReadUInt32(output, -4)); Assert.Equal(0xFEDCBA98u, disk.ReadUInt32(output, bytes));
            Assert.Equal(0, disk.Writes); Assert.Equal(0, disk.DurableReads);
        }
    }

    [Theory]
    [InlineData(512)] [InlineData(1024)] [InlineData(4096)] [InlineData(32768)]
    public void TrstBitmapWordsCountOnlyAllocationBitsAcrossBlockSizes(int bytes)
    {
        var method = typeof(AmigaSmartFileSystemWriteCore).GetMethod("CountTrstBitmapFreeBits", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(Disk));
        var bitmap = APTR.FromPointer(1024);
        for (var pattern = 0; pattern < 5; pattern++)
        {
            var disk = new Disk(Image()); var image = new byte[bytes]; var random = new Random(0xB17 + pattern);
            random.NextBytes(image); image.AsSpan(0, 12).Fill(255);
            if (pattern == 0) image.AsSpan(12).Clear();
            if (pattern == 1) image.AsSpan(12).Fill(255);
            if (pattern == 2) for (var i = 12; i < bytes; i++) image[i] = (byte)((i & 1) == 0 ? 0xAA : 0x55);
            if (pattern == 3)
            { image.AsSpan(12).Clear(); image[12] = 128; image[^1] = 1; }
            image.CopyTo(disk.Ram, (int)bitmap.Raw); disk.WriteUInt32(bitmap, -4, uint.MaxValue); disk.WriteUInt32(bitmap, bytes, uint.MaxValue);
            var expected = 0u;
            for (var i = 12; i < bytes; i++) expected += (uint)BitOperations.PopCount((uint)image[i]);
            Assert.Equal(expected, (uint)method.Invoke(null, new object[] { disk, bitmap, (uint)bytes })!);
            Assert.Equal(image, disk.Ram.AsSpan((int)bitmap.Raw, bytes).ToArray());
            Assert.Equal(0, disk.Writes); Assert.Equal(0, disk.DurableReads);
        }
    }
}
