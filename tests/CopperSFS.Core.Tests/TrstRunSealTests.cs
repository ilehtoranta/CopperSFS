using Amiga;
using System.Reflection;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void TrstRunSealPreservesAllWordWeightsAndWraparound(int pattern)
    {
        var disk = new Disk(Image()); var group = APTR.FromPointer(1024); var arena = APTR.FromPointer(2048);
        var covered = Enumerable.Range(0, 50).Select(i => (Address: group, Offset: i * 4))
            .Concat(new (APTR Address, int Offset)[] { (group, 208), (group, 212), (arena, 0), (arena, 4), (arena, 8), (arena, 16), (arena, 20) }).ToArray();
        var method = typeof(AmigaSmartFileSystemWriteCore).GetMethod("TrstRunSeal", BindingFlags.NonPublic | BindingFlags.Static)!
            .MakeGenericMethod(typeof(Disk));
        var random = new Random(0x57A1);
        for (var vector = 0; vector < 57; vector++)
        {
            // Independent closed-form oracle: the first word has weight 57,
            // the last weight 1. Basis vectors also catch omitted/duplicated
            // words, including descriptor padding and the unrolled tail.
            var expected = 0u;
            for (var i = 0; i < covered.Length; i++)
            {
                var value = pattern switch
                {
                    0 => i == vector ? 1u : 0u,
                    1 => uint.MaxValue,
                    2 => (i & 1) == 0 ? 0xAAAAAAAAu : 0x55555555u,
                    _ => (uint)random.NextInt64(0, 1L << 32)
                };
                disk.WriteUInt32(covered[i].Address, covered[i].Offset, value);
                expected = unchecked(expected + value * (uint)(57 - i));
            }
            // These mutable fields are deliberately outside the seal.
            disk.WriteUInt32(group, 200, (uint)random.Next()); disk.WriteUInt32(group, 204, (uint)random.Next());
            disk.WriteUInt32(group, 216, (uint)random.Next()); disk.WriteUInt32(arena, 12, (uint)random.Next());
            Assert.Equal(expected, (uint)method.Invoke(null, new object[] { disk, group, arena })!);
        }
    }
}
