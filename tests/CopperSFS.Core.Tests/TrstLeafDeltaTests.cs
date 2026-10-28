using System.Buffers.Binary;
using System.Reflection;
using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(0xFFFEu, 3u, 14, 0, 1, true)]
    [InlineData(0xFFF8u, 16u, 14, 1, 0, true)]
    [InlineData(0xFFFFu, 1u, 14, 0, 2, true)]
    [InlineData(0x1FFFFF8u, 16u, 18, 1, 2, true)]
    [InlineData(0xFFFEu, 3u, 16, 1, 0, true)]
    [InlineData(0xFFFEu, 3u, 14, 0, 1, false)]
    public void TrstLeafDeltaMatchesIndependentFullChecksumAcrossHalfwordCarries(
        uint tail, uint count, int stride, int beforeIndex, int tailIndex, bool hasAfter)
    {
        var beforeAt = 16 + beforeIndex * stride; var tailAt = 16 + tailIndex * stride;
        var afterAt = 16 + (3 - beforeIndex - tailIndex) * stride;
        var beforeLength = tail > 0x100000 ? 65535u - count : 64u;
        const uint tailLength = 512;
        var after = hasAfter ? tail + tailLength + 100 : 0;
        var data = Enumerable.Range(0, Bytes).Select(i => (byte)(i * 13 + 7)).ToArray();
        void Word(byte[] image, int at, uint value) => BinaryPrimitives.WriteUInt32BigEndian(image.AsSpan(at), value);
        void Half(byte[] image, int at, uint value) => BinaryPrimitives.WriteUInt16BigEndian(image.AsSpan(at), (ushort)value);
        Word(data, 0, Tree); Word(data, 8, 6); Half(data, 12, 3); data[14] = 1; data[15] = (byte)stride;
        Word(data, beforeAt, 2048); Word(data, beforeAt + 4, tail); Word(data, beforeAt + 8, 0x80000003);
        Half(data, beforeAt + 12, beforeLength);
        Word(data, tailAt, tail); Word(data, tailAt + 4, after); Word(data, tailAt + 8, 2048);
        Half(data, tailAt + 12, tailLength);
        if (hasAfter)
        {
            Word(data, afterAt, after); Word(data, afterAt + 4, 0); Word(data, afterAt + 8, tail);
            Half(data, afterAt + 12, 16);
        }
        Sum(data);
        var expected = data.ToArray();
        Word(expected, tailAt, tail + count); Half(expected, tailAt + 12, tailLength - count);
        Word(expected, beforeAt + 4, tail + count); Half(expected, beforeAt + 12, beforeLength + count);
        if (hasAfter) Word(expected, afterAt + 8, tail + count);
        Sum(expected); // Full independent recomputation, not the delta formula.
        var disk = new Disk(new byte[Blocks * Bytes]);
        var hint = APTR.FromPointer(50000); var leaf = APTR.FromPointer(60000);
        data.CopyTo(disk.Ram, (int)leaf.Raw);
        disk.WriteUInt32(hint, 8, tail); disk.WriteUInt32(hint, 12, tailLength);
        disk.WriteUInt32(hint, 16, 2048); disk.WriteUInt32(hint, 20, beforeLength);
        disk.WriteUInt32(hint, 24, after); disk.WriteUInt32(hint, 32, (uint)tailAt);
        disk.WriteUInt32(hint, 36, (uint)beforeAt); disk.WriteUInt32(hint, 40, (uint)afterAt);
        var savedHint = disk.Ram.AsSpan((int)hint.Raw, 48).ToArray();
        var method = typeof(AmigaSmartFileSystemWriteCore).GetMethod("ApplyTrstContinuationLeaf",
            BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(Disk));
        object[] args = [disk, hint, leaf, count]; method.Invoke(null, args); disk = (Disk)args[0];
        Assert.Equal(expected, disk.Ram.AsSpan((int)leaf.Raw, Bytes).ToArray());
        Assert.Equal(savedHint, disk.Ram.AsSpan((int)hint.Raw, 48).ToArray());
        Assert.Empty(disk.PhysicalWrites);
    }
}
