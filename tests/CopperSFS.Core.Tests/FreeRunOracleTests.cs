using System.Reflection;
using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(0, false, false)] [InlineData(0, false, true)]
    [InlineData(0, true, false)] [InlineData(0, true, true)]
    [InlineData(1, false, false)] [InlineData(1, false, true)]
    [InlineData(1, true, false)] [InlineData(1, true, true)]
    [InlineData(2, false, false)] [InlineData(2, false, true)]
    [InlineData(2, true, false)] [InlineData(2, true, true)]
    [InlineData(3, false, false)] [InlineData(3, false, true)]
    [InlineData(3, true, false)] [InlineData(3, true, true)]
    [InlineData(4, false, false)] [InlineData(4, false, true)]
    [InlineData(4, true, false)] [InlineData(4, true, true)]
    [InlineData(5, false, false)] [InlineData(5, false, true)]
    [InlineData(5, true, false)] [InlineData(5, true, true)]
    [InlineData(6, false, false)] [InlineData(6, false, true)]
    [InlineData(6, true, false)] [InlineData(6, true, true)]
    [InlineData(7, false, false)] [InlineData(7, false, true)]
    [InlineData(7, true, false)] [InlineData(7, true, true)]
    public void FreeRunSearchMatchesIndependentBitOracle(int pattern, bool adminReservation, bool scratchView)
        => CheckFreeRunOracle(pattern, adminReservation, scratchView, false);

    [Theory]
    [InlineData(0, false, false)] [InlineData(0, false, true)]
    [InlineData(0, true, false)] [InlineData(0, true, true)]
    [InlineData(1, false, false)] [InlineData(1, false, true)]
    [InlineData(1, true, false)] [InlineData(1, true, true)]
    [InlineData(2, false, false)] [InlineData(2, false, true)]
    [InlineData(2, true, false)] [InlineData(2, true, true)]
    [InlineData(3, false, false)] [InlineData(3, false, true)]
    [InlineData(3, true, false)] [InlineData(3, true, true)]
    [InlineData(4, false, false)] [InlineData(4, false, true)]
    [InlineData(4, true, false)] [InlineData(4, true, true)]
    [InlineData(5, false, false)] [InlineData(5, false, true)]
    [InlineData(5, true, false)] [InlineData(5, true, true)]
    [InlineData(6, false, false)] [InlineData(6, false, true)]
    [InlineData(6, true, false)] [InlineData(6, true, true)]
    [InlineData(7, false, false)] [InlineData(7, false, true)]
    [InlineData(7, true, false)] [InlineData(7, true, true)]
    public void TrstFreeRunSearchMatchesIndependentThreeViewOracle(int pattern, bool adminReservation, bool scratchView)
        => CheckFreeRunOracle(pattern, adminReservation, scratchView, true);

    [Theory]
    [InlineData(0, false, false)]
    [InlineData(0, false, true)]
    [InlineData(0, true, false)]
    [InlineData(0, true, true)]
    [InlineData(1, false, false)]
    [InlineData(1, false, true)]
    [InlineData(1, true, false)]
    [InlineData(1, true, true)]
    [InlineData(6, false, false)]
    [InlineData(6, false, true)]
    [InlineData(6, true, false)]
    [InlineData(6, true, true)]
    [InlineData(7, false, false)]
    [InlineData(7, false, true)]
    [InlineData(7, true, false)]
    [InlineData(7, true, true)]
    public void BoundedFreeRunSearchMatchesIndependentBitOracle(int pattern, bool scratchView, bool durable)
        => CheckFreeRunOracle(pattern, true, scratchView, durable, true);

    private static void CheckFreeRunOracle(int pattern, bool adminReservation, bool scratchView, bool durable, bool bounded = false)
    {
        const int total = 8192;
        var image = new byte[total * Bytes];
        var free = new bool[total];
        var random = new Random(0x534653 + pattern);
        for (var block = 0; block < total; block++) free[block] = pattern switch
        {
            0 => false, 1 => true, 2 => (block & 1) == 0, 3 => (block & 1) != 0,
            4 => block % 65 < 31, 5 => block % 127 < 63,
            6 => random.Next(8) != 0, _ => random.Next(2) != 0
        };
        // Guaranteed candidates at word and bitmap boundaries, including a
        // long run a first-fit scan must prefer over later aligned candidates.
        if (pattern >= 2)
            foreach (var (start, length) in new[] { (257, 17), (991, 33), (3967, 66), (7983, 129) })
                Array.Fill(free, true, start, length);
        // The expected bits above are independent of the three scanner inputs.
        // At every other position, distribute all seven non-all-free states
        // across original, accepted and current bitmaps. This includes blocks
        // retired from disk and accepted unpublished data freed by a candidate.
        var originalFree = new bool[total]; var acceptedFree = new bool[total]; var currentFree = new bool[total];
        for (var block = 0; block < total; block++)
        {
            var state = !durable ? (free[block] ? 7 : 0) : free[block] ? 7 : block % 7;
            originalFree[block] = (state & 1) != 0;
            acceptedFree[block] = (state & 2) != 0;
            currentFree[block] = (state & 4) != 0;
        }
        for (var index = 0; index < 3; index++)
        {
            Header(image, 12 + index, Bitmap);
            var map = Block(image, 12 + index);
            for (var bit = 0; bit < 4000 && index * 4000 + bit < total; bit++)
                if (originalFree[index * 4000 + bit]) map[12 + bit / 8] |= (byte)(128 >> (bit & 7));
            Sum(map);
        }
        // These reservations are excluded by FreeSearchMap, independently of
        // the stored bitmap. All other bits are deliberately arbitrary.
        free[4] = false;
        if (adminReservation) Array.Fill(free, false, 128, 32);
        var disk = new Disk(image) { CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratchView };
        var work = TrstWork(); work.BitmapBase = 12; work.RootContainer = 2;
        var checkpoint = APTR.FromPointer(60000); var candidate = APTR.FromPointer(90000);
        if (durable)
        {
            Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref disk, checkpoint, 4096, Bytes));
            Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref disk, candidate, 4096, Bytes));
            for (var index = 0; index < 3; index++)
                foreach (var accepted in new[] { true, false })
                {
                    var map = Block(image, 12 + index).ToArray(); map.AsSpan(12).Clear();
                    var input = accepted ? acceptedFree : currentFree;
                    for (var bit = 0; bit < 4000 && index * 4000 + bit < total; bit++)
                        if (input[index * 4000 + bit]) map[12 + bit / 8] |= (byte)(128 >> (bit & 7));
                    Sum(map);
                    map.CopyTo(disk.Ram.AsSpan((int)work.Meta.Raw, Bytes));
                    Assert.True(AmigaSmartFileSystemRedoArena.Store(ref disk,
                        accepted ? checkpoint : candidate, (uint)(12 + index), work.Meta));
                }
            disk.RedoOverlay = candidate;
            // Staged home targets are excluded even when all three maps say free.
            Array.Fill(free, false, 12, 3);
        }
        object boxedWork = work;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        typeof(AmigaSmartFileSystemWriteCore.Work).GetField("BlockBytes", flags)!.SetValue(boxedWork, 512u);
        typeof(AmigaSmartFileSystemWriteCore.Work).GetField("BlockCount", flags)!.SetValue(boxedWork, (uint)total);
        typeof(AmigaSmartFileSystemWriteCore.Work).GetField("ReservedBlocks", flags)!.SetValue(boxedWork, 1u);
        typeof(AmigaSmartFileSystemWriteCore.Work).GetField("AdminReservation", flags)!.SetValue(boxedWork, adminReservation ? 128u : 0u);
        if (durable) typeof(AmigaSmartFileSystemWriteCore.Work).GetField("RedoCheckpoint", flags)!.SetValue(boxedWork, checkpoint);
        var scan = typeof(AmigaSmartFileSystemWriteCore).GetMethod(bounded ? "ScanFreeRunBounded" : "ScanFreeRun", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(Disk));
        var starts = new[] { 1, 16, 31, 32, 33, 255, 257, 991, 3967, 3999, 4000, 4001, 7983, 7999, 8000, 8159, 8190 };
        var minimums = new[] { 1, 2, 3, 7, 15, 16, 17, 31, 32, 33, 63, 64, 65, 127, 128, 129, 256 };
        if (bounded)
        {
            starts = [16, 31, 32, 991, 3967, 3999, 4000, 7983, 8190];
            minimums = [1, 31, 32, 127, 128, 129, 3582, 3999, 4000, 4001, 8000];
        }
        foreach (var searchEnd in bounded ? new[] { 0, 1, 32, 33, 3999, 4000, 4001, 8000, 8191, 8192, 9000 } : new[] { 0 })
        foreach (var start in starts)
        foreach (var minimum in minimums)
        foreach (var oneMap in new[] { false, true })
        foreach (var prefix in new[] { false, true })
        {
            var maximum = minimum + ((start + minimum) & 31);
            var expected = Oracle(start, minimum, maximum, oneMap, prefix, searchEnd);
            object[] args = [disk, boxedWork, (uint)start, (uint)minimum, (uint)maximum, oneMap, prefix, 0u, 0u, durable];
            if (bounded) args = [.. args, (uint)searchEnd];
            Assert.True((bool)scan.Invoke(null, args)!);
            disk = (Disk)args[0];
            Assert.Equal(expected.First, (uint)args[7]);
            Assert.Equal(expected.Count, (uint)args[8]);
        }
        // The final lookup expires and checks the last borrowed view too.
        Assert.True(disk.ReadBlock(12, work.Meta));
        Assert.Empty(disk.PhysicalWrites);

        (uint First, uint Count) Oracle(int start, int minimum, int maximum, bool oneMap, bool prefix, int searchEnd)
        {
            var high = searchEnd == 0 ? total - 1 : Math.Min(total - 1, searchEnd);
            var position = start;
            while (position < high)
            {
                if (!free[position]) { if (prefix) break; position++; continue; }
                var begin = position;
                var limit = oneMap ? Math.Min(high, (begin / 4000 + 1) * 4000) : high;
                while (position < limit && free[position] && position - begin < maximum) position++;
                var count = position - begin;
                if (count >= minimum) return ((uint)begin, (uint)count);
                if (prefix) break;
            }
            return (0, 0);
        }
    }
}
