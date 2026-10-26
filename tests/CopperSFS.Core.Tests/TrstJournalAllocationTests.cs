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
    [InlineData(6, false)] [InlineData(6, true)]
    [InlineData(7, false)] [InlineData(7, true)]
    public void TrstJournalPagesMatchIndependentAllocationOracle(int pattern, bool scratchView)
    {
        const int total = 8192;
        var image = new byte[total * Bytes];
        var originalFree = new bool[total]; var finalFree = new bool[total];
        var random = new Random(0x54525354 + pattern);
        for (var block = 0; block < total; block++)
        {
            // Include long spans originally free but consumed by the group,
            // the reverse, isolated bits, and mixed words across map boundaries.
            var state = pattern switch
            {
                0 => 0, 1 => 3, 2 => block % 4,
                3 => block < 8000 ? 1 : 3,
                4 => block < 8000 ? 2 : 3,
                5 => block % 129 < 63 ? 3 : block % 3,
                6 => random.Next(4), _ => block % 65 < 33 ? 1 : 3
            };
            originalFree[block] = (state & 1) != 0;
            finalFree[block] = (state & 2) != 0;
        }
        for (var index = 0; index < 3; index++)
        {
            Header(image, 12 + index, Bitmap);
            var map = Block(image, 12 + index);
            for (var bit = 0; bit < 4000 && index * 4000 + bit < total; bit++)
                if (originalFree[index * 4000 + bit]) map[12 + bit / 8] |= (byte)(128 >> (bit & 7));
            Sum(map);
        }
        var disk = new Disk(image) { CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratchView };
        var work = TrstWork(); work.BitmapBase = 12; work.RootContainer = 2;
        var arena = APTR.FromPointer(60000);
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref disk, arena, 8192, Bytes));
        // Map 13 is deliberately absent from the overlay: its durable bitmap
        // alone governs availability. Maps 12 and 14 have final owned images.
        foreach (var index in new[] { 0, 2 })
        {
            var map = Block(image, 12 + index).ToArray(); map.AsSpan(12).Clear();
            for (var bit = 0; bit < 4000 && index * 4000 + bit < total; bit++)
                if (finalFree[index * 4000 + bit]) map[12 + bit / 8] |= (byte)(128 >> (bit & 7));
            Sum(map);
            map.CopyTo(disk.Ram.AsSpan((int)work.Data.Raw, Bytes));
            Assert.True(AmigaSmartFileSystemRedoArena.Store(ref disk, arena, (uint)(12 + index), work.Data));
        }
        var targets = new uint[] { 12, 14, 16, 31, 3999, 4000, 8190 };
        // Payload targets deliberately begin with a valid BTMP image. Their
        // contents must not cause them to be treated as allocation bitmaps.
        foreach (var target in targets.Skip(2))
            Assert.True(AmigaSmartFileSystemRedoArena.Store(ref disk, arena, target, work.Data));
        var expected = Enumerable.Range(1, total - 2).Where(block =>
            originalFree[block] && (block / 4000 == 1 || finalFree[block]) &&
            block != 4 && !targets.Contains((uint)block)).Select(block => (uint)block).ToArray();
        var savedArena = disk.Ram.AsSpan((int)arena.Raw, 8192).ToArray();
        var savedImage = image.ToArray();
        const BindingFlags fields = BindingFlags.Instance | BindingFlags.NonPublic;
        object boxedWork = work;
        typeof(AmigaSmartFileSystemWriteCore.Work).GetField("BlockBytes", fields)!.SetValue(boxedWork, 512u);
        typeof(AmigaSmartFileSystemWriteCore.Work).GetField("BlockCount", fields)!.SetValue(boxedWork, (uint)total);
        typeof(AmigaSmartFileSystemWriteCore.Work).GetField("ReservedBlocks", fields)!.SetValue(boxedWork, 1u);
        var core = typeof(AmigaSmartFileSystemWriteCore);
        // Independently count every originally free bit in the two touched
        // maps. Final contents cannot make this admission bound less strict.
        var group = APTR.FromPointer(100000);
        var originalCount = (uint)originalFree.Count(bit => bit);
        var touchedCount = (uint)originalFree.Where((bit, at) => at / 4000 != 1 && bit).Count();
        var records = (uint)targets.Length;
        var pages = (records * (128 * 5 + 8) + 495) / 496;
        var admission = core.GetMethod("TrstGroupHasJournalSpace", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(Disk));
        foreach (var available in new[] { 0u, originalCount })
        {
            disk.WriteUInt32(group, 60, available);
            disk.RedoOverlay = arena;
            object[] args = [disk, boxedWork, group, arena, false, false];
            var enough = (bool)admission.Invoke(null, args)!;
            disk = (Disk)args[0];
            Assert.Equal(available >= touchedCount + records + pages, enough);
            Assert.Equal(enough, (bool)args[4]); // Only conservative admission may feed a continuation proof.
            Assert.Equal(arena, disk.RedoOverlay);
        }
        disk.RedoOverlay = APTR.Null;
        // Expire and check the last borrowed map before any scratch writes.
        Assert.True(disk.ReadBlock(12, work.Meta));
        var stagedType = core.GetNestedType("RedoPlatform`1", BindingFlags.NonPublic)!.MakeGenericType(typeof(Disk));
        var staged = Activator.CreateInstance(stagedType)!;
        stagedType.GetField("Inner", fields)!.SetValue(staged, disk);
        stagedType.GetField("Arena", fields)!.SetValue(staged, arena);
        var streamType = core.GetNestedType("RedoStream", BindingFlags.NonPublic)!;
        var allocate = core.GetMethod("TrstSpace", BindingFlags.Static | BindingFlags.NonPublic)!.MakeGenericMethod(typeof(Disk));
        foreach (var start in new uint[] { 1, 31, 32, 3999, 4000, 8000, 8190, 8191 })
        {
            var stream = Activator.CreateInstance(streamType)!;
            streamType.GetField("Search", fields)!.SetValue(stream, start);
            streamType.GetField("Bitmap", fields)!.SetValue(stream, uint.MaxValue);
            object[] args = [disk, boxedWork, staged, stream, 0u];
            foreach (var block in expected.Where(block => block >= start))
            {
                Assert.True((bool)allocate.Invoke(null, args)!);
                Assert.Equal(block, (uint)args[4]);
            }
            Assert.False((bool)allocate.Invoke(null, args)!);
            Assert.Equal(0u, (uint)args[4]);
            Assert.Equal(DOS.Error.DiskFull, ((AmigaSmartFileSystemWriteCore.Work)args[1]).Error);
        }
        Assert.Empty(disk.PhysicalWrites);
        Assert.Equal(savedArena, disk.Ram.AsSpan((int)arena.Raw, 8192).ToArray());
        Assert.Equal(savedImage, image);
    }
}
