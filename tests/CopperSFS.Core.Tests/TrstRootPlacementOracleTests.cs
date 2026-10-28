using System.Reflection;
using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(0, false)] [InlineData(0, true)]
    [InlineData(1, false)] [InlineData(1, true)]
    [InlineData(2, false)] [InlineData(2, true)]
    [InlineData(3, false)] [InlineData(3, true)]
    [InlineData(4, false)] [InlineData(4, true)]
    [InlineData(5, false)] [InlineData(5, true)]
    public void TrstRootPlacementMatchesIndependentBitOracle(int pattern, bool scratch)
    {
        const int total = 8192;
        var image = new byte[total * Bytes];
        var free = new bool[total];
        var random = new Random(0x545253 + pattern);
        for (var i = 64; i < total; i++) free[i] = pattern == 0 || random.Next(8) != 0;
        foreach (var (begin, length) in new[] { (257, 257), (991, 128), (3967, 66), (7983, 129) })
            Array.Fill(free, true, begin, length);
        (int Begin, int Length)[] extents = pattern switch
        {
            0 => [],
            1 => [(100, 3800), (4300, 3000)],
            2 => [(64, 257), (3999, 2), (7500, 500)],
            3 => [(100, 1000), (2000, 1000), (4000, 1000), (6000, 1900)],
            4 => [(100, 2000), (1000, 2000)], // Invalid range index must fall back to bitmaps.
            _ => [(100, 1000), (7000, 500)]
        };
        foreach (var range in extents) Array.Fill(free, false, range.Begin, range.Length);
        // Reserved blocks deliberately remain free on disk. The independent
        // expected set excludes them, requiring the search to apply its mask.
        var onDisk = free.ToArray();
        Array.Fill(free, false, 128, 32);
        Header(image, 9, 0x424E4443);
        var leaf = Block(image, 9);
        U16(leaf, 12, (ushort)extents.Length); leaf[14] = pattern == 5 ? (byte)0 : (byte)1; leaf[15] = 14;
        for (var i = 0; i < extents.Length; i++)
        {
            U32(leaf, 16 + i * 14, (uint)extents[i].Begin);
            U16(leaf, 28 + i * 14, (ushort)extents[i].Length);
        }
        Sum(leaf);
        for (var index = 0; index < 3; index++)
        {
            Header(image, 12 + index, Bitmap);
            var map = Block(image, 12 + index);
            for (var bit = 0; bit < 4000 && index * 4000 + bit < total; bit++)
                if (onDisk[index * 4000 + bit]) map[12 + bit / 8] |= (byte)(128 >> (bit & 7));
            Sum(map);
        }
        var disk = new Disk(image) { CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratch };
        var work = TrstWork(); work.BitmapBase = 12; work.ExtentRoot = 9; work.RootContainer = 2;
        object boxedWork = work;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        foreach (var (name, value) in new[] { ("BlockBytes", 512u), ("BlockCount", (uint)total), ("ReservedBlocks", 1u), ("AdminReservation", 128u) })
            typeof(AmigaSmartFileSystemWriteCore.Work).GetField(name, flags)!.SetValue(boxedWork, value);
        var scan = typeof(AmigaSmartFileSystemWriteCore).GetMethod("ScanTrstRootPlacement", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(Disk));
        foreach (var start in new[] { 1, 31, 100, 991, 3999, 4000, 7900, 8190 })
        foreach (var minimum in new[] { 1, 16, 128, 256, 512, 3582, 4000 })
        foreach (var end in new[] { 1, 32, 3999, 4000, 4001, 8000, 8191, 8192, 9000 })
        {
            var maximum = minimum + ((start + minimum) & 31);
            var expected = Oracle(start, minimum, maximum, end);
            object[] args = [disk, boxedWork, (uint)start, (uint)minimum, (uint)maximum, (uint)end, 0u, 0u];
            Assert.True((bool)scan.Invoke(null, args)!);
            disk = (Disk)args[0];
            Assert.Equal(expected.First, (uint)args[6]); Assert.Equal(expected.Count, (uint)args[7]);
        }
        Assert.True(disk.ReadBlock(12, work.Meta)); // Expire and check the final borrowed view.
        Assert.Empty(disk.PhysicalWrites);

        (uint First, uint Count) Oracle(int start, int minimum, int maximum, int end)
        {
            var high = Math.Min(total - 1, end); var position = start;
            while (position < high)
            {
                if (!free[position]) { position++; continue; }
                var begin = position; var limit = Math.Min(high, (begin / 4000 + 1) * 4000);
                while (position < limit && free[position] && position - begin < maximum) position++;
                if (position - begin >= minimum) return ((uint)begin, (uint)(position - begin));
            }
            return (0, 0);
        }
    }
}
