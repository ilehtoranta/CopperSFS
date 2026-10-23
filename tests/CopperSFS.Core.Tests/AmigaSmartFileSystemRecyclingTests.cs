using Amiga;
using CopperSFS;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private static byte[] RecycledImage()
    {
        var image = Image();
        foreach (var block in new[] { 0, Blocks - 1 })
        { Block(image, block)[20] = 64; Sum(Block(image, block)); }
        Block(image, 7).Clear(); Header(image, 7, Objects);
        var entries = Block(image, 7); U32(entries, 12, 1);
        var at = Object(entries, 24, 2, 0, 8, 135, ".recycled");
        Object(entries, at, 3, 20, 1300, 0, "Hello", "greeting"); Sum(entries);
        // Nested is an existing recycled file, occupying one block.
        U32(Block(image, 2), Bytes - 36, 1); U32(Block(image, 2), Bytes - 32, 1); Sum(Block(image, 2));
        return image;
    }

    private static void AssertRecycledCounters(ref Disk disk, uint files, uint blocks)
    {
        var scratch = APTR.FromPointer(512);
        Assert.True(disk.ReadMetadataBlock(2, Objects, scratch));
        Assert.Equal(blocks, disk.ReadUInt32(scratch, Bytes - 36));
        Assert.Equal(files, disk.ReadUInt32(scratch, Bytes - 32));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteOnWriteProtectedMediaPreservesTheAmigaDosError(bool buffered)
    {
        // RKM AmigaDOS 10.2.9 distinguishes physical/software media protection
        // (ERROR_DISK_WRITE_PROTECTED) from an unvalidated filesystem.
        var image = RecycledImage(); var before = image.ToArray();
        var disk = new Disk(image) { ReadOnly = true };
        var work = buffered ? BufferedWork(ref disk) : BatchWork(ref disk);
        Assert.False(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3));
        Assert.Equal(DOS.Error.DiskWriteProtected, work.Error);
        Assert.Equal(0, disk.Writes);
        Assert.Equal(before, disk.Snapshot());
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void RecycledCollisionsRespectVolumeCasePolicy(bool buffered, bool sensitive)
    {
        var image = RecycledImage();
        foreach (var block in new[] { 0, Blocks - 1 })
        { Block(image, block)[20] = (byte)(64 | (sensitive ? 128 : 0)); Sum(Block(image, block)); }
        var disk = new Disk(image) { CheckReadOnlyViews = true };
        var work = buffered ? BufferedWork(ref disk) : BatchWork(ref disk);
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3), work.Error.ToString());
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = disk.Name("hello"), Length = 5 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false), work.Error.ToString());
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, request.Node), work.Error.ToString());
        var recycled = sensitive ? "hello" : "hello$AAA";
        Assert.Equal(request.Node, AmigaSmartFileSystemCore.Resolve(ref disk, 2, disk.Name(recycled),
            (uint)recycled.Length, work.Meta, out var error));
        Assert.Equal(DOS.Error.None, error);
        AssertRecycledCounters(ref disk, 3, 4);
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
        var remounted = new Disk(image); work = BatchWork(ref remounted);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref remounted, work.Meta));
        Assert.Equal(request.Node, AmigaSmartFileSystemCore.Resolve(ref remounted, 2, remounted.Name(recycled),
            (uint)recycled.Length, work.Meta, out error));
    }

    [Fact]
    public void RecoveryRefreshesCachedRecyclingPolicy()
    {
        var image = RecycledImage(); var disk = new Disk(image);
        var work = BufferedWork(ref disk);
        Assert.True(AmigaSmartFileSystemWriteCore.Resize(ref disk, ref work, 3, 1300));
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work));
        foreach (var block in new[] { 0, Blocks - 1 })
        { Block(image, block)[20] = 0; Sum(Block(image, block)); }
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work));
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3), work.Error.ToString());
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref disk, 2, disk.Name("Hello"), 5, work.Meta, out var error));
        Assert.Equal(DOS.Error.ObjectNotFound, error);
        AssertRecycledCounters(ref disk, 1, 1);
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecyclingCompactsWordAlignedRecordsWithoutChangingNeighbors(bool buffered)
    {
        var image = RecycledImage(); var disk = new Disk(image) { CheckReadOnlyViews = true };
        var work = buffered ? BufferedWork(ref disk) : BatchWork(ref disk);
        var nodes = new uint[8];
        for (var i = 0; i < nodes.Length; i++)
        {
            var name = new string((char)('a' + i), i + 1);
            var request = new AmigaSmartFileSystemWriteCore.Request
            { Directory = 1, Name = disk.Name(name), Length = (uint)name.Length };
            Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false), work.Error.ToString());
            nodes[i] = request.Node;
            var commentLength = 1 + i % 3;
            request.Buffer = disk.Name(new string((char)('A' + i), commentLength)); request.Length = (uint)commentLength;
            Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetComment), work.Error.ToString());
        }
        // Move records from the middle, exercising both checksum half-word
        // boundaries and overlapping compaction with live neighbors after it.
        foreach (var i in new[] { 3, 1, 6, 0, 7, 2, 5, 4 })
            Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, nodes[i]), work.Error.ToString());
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
        var remounted = new Disk(image); work = BatchWork(ref remounted);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref remounted, work.Meta));
        for (var i = 0; i < nodes.Length; i++)
        {
            var name = new string((char)('a' + i), i + 1);
            Assert.Equal(nodes[i], AmigaSmartFileSystemCore.Resolve(ref remounted, 2, remounted.Name(name),
                (uint)name.Length, work.Meta, out var error));
            Assert.Equal(DOS.Error.None, error);
            var fib = APTR.FromPointer(6000);
            Assert.True(AmigaSmartFileSystemCore.Examine(ref remounted, nodes[i], fib, work.Meta));
            var commentLength = 1 + i % 3;
            Assert.Equal(commentLength, remounted.ReadUInt8(fib, 144));
            for (var j = 0; j < commentLength; j++) Assert.Equal((byte)('A' + i), remounted.ReadUInt8(fib, 145 + j));
        }
        AssertRecycledCounters(ref remounted, 9, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DeleteRecyclesFilesAndPermanentDeleteUpdatesCounters(bool buffered)
    {
        var image = RecycledImage(); var disk = new Disk(image);
        var work = buffered ? BufferedWork(ref disk) : BatchWork(ref disk);
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3), work.Error.ToString());
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref disk, 1, disk.Name("Hello"), 5, work.Meta, out var error));
        Assert.Equal(DOS.Error.ObjectNotFound, error);
        Assert.Equal(3u, AmigaSmartFileSystemCore.Resolve(ref disk, 2, disk.Name("hello"), 5, work.Meta, out error));
        Assert.Equal(1300, AmigaSmartFileSystemCore.Read(ref disk, 3, 0, APTR.FromPointer(4000), 1300, work.Meta, work.Data, out error));
        Assert.Equal(Enumerable.Range(0, 1300).Select(i => (byte)(i * 17)), disk.Ram.AsSpan(4000, 1300).ToArray());
        AssertRecycledCounters(ref disk, 2, 4);
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
        var remounted = new Disk(image); work = BatchWork(ref remounted);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref remounted, work.Meta));
        AssertRecycledCounters(ref remounted, 2, 4);
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref remounted, ref work, 3), work.Error.ToString());
        AssertRecycledCounters(ref remounted, 1, 1);
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref remounted, 2, remounted.Name("Hello"), 5, work.Meta, out error));
        Assert.Equal(DOS.Error.ObjectNotFound, error);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref remounted, work.Meta));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecycleNamesUseOriginalHandlerTruncationAndCollisionSuffixes(bool buffered)
    {
        var disk = new Disk(RecycledImage());
        var work = buffered ? BufferedWork(ref disk) : BatchWork(ref disk);
        var stem = new string('a', 96);
        for (var index = 0; index < 3; index++)
        {
            var name = stem + index.ToString("D4");
            var request = new AmigaSmartFileSystemWriteCore.Request
            { Directory = 1, Name = disk.Name(name), Length = (uint)name.Length };
            Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false), work.Error.ToString());
            var node = request.Node;
            Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, node), work.Error.ToString());
            var recycledName = stem + (index == 0 ? "" : index == 1 ? "$AAA" : "$AAB");
            Assert.Equal(node, AmigaSmartFileSystemCore.Resolve(ref disk, 2, disk.Name(recycledName), (uint)recycledName.Length, work.Meta, out var error));
            Assert.Equal(DOS.Error.None, error);
        }
        AssertRecycledCounters(ref disk, 4, 1);
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void RecycledFileResizeWriteTruncateAndRestoreMaintainCounters(bool buffered)
    {
        var disk = new Disk(RecycledImage());
        var work = buffered ? BufferedWork(ref disk) : BatchWork(ref disk);
        Assert.True(AmigaSmartFileSystemWriteCore.Resize(ref disk, ref work, 4, 1000), work.Error.ToString());
        AssertRecycledCounters(ref disk, 1, 2);
        Array.Fill(disk.Ram, (byte)0xA7, 4000, 1000);
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = 4, Buffer = APTR.FromPointer(4000), Position = 1000, Length = 1000 };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out var actual), work.Error.ToString());
        Assert.Equal(1000u, actual); AssertRecycledCounters(ref disk, 1, 4);
        Assert.True(AmigaSmartFileSystemWriteCore.Truncate(ref disk, ref work, 4), work.Error.ToString());
        AssertRecycledCounters(ref disk, 1, 0);
        request = new() { Node = 4, Directory = 1, Name = disk.Name("Restored"), Length = 8 };
        Assert.True(AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref request), work.Error.ToString());
        AssertRecycledCounters(ref disk, 0, 0);
        Assert.Equal(4u, AmigaSmartFileSystemCore.Resolve(ref disk, 1, disk.Name("Restored"), 8, work.Meta, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
    }

    [Fact]
    public void RecycleDirectoryRejectsUserInsertionAndDeletionButCanBeRenamed()
    {
        var disk = new Disk(RecycledImage()); var work = BatchWork(ref disk);
        var request = new AmigaSmartFileSystemWriteCore.Request { Directory = 2, Name = disk.Name("User"), Length = 4 };
        Assert.False(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false));
        Assert.Equal(DOS.Error.ObjectInUse, work.Error);
        request.Node = 3;
        Assert.False(AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref request));
        Assert.Equal(DOS.Error.ObjectInUse, work.Error);
        Assert.False(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 2));
        Assert.Equal(DOS.Error.ObjectInUse, work.Error);
        Assert.Equal(0, disk.Writes);
        request = new() { Node = 2, Directory = 1, Name = disk.Name("Trash"), Length = 5 };
        Assert.True(AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref request), work.Error.ToString());
        Assert.Equal(2u, AmigaSmartFileSystemCore.Resolve(ref disk, 1, disk.Name("Trash"), 5, work.Meta, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3), work.Error.ToString());
        Assert.Equal(3u, AmigaSmartFileSystemCore.Resolve(ref disk, 1, disk.Name("Trash/Hello"), 11, work.Meta, out error));
        Assert.Equal(DOS.Error.None, error);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public void RecyclingAndPermanentDeletionRecoverAcrossEveryWriteAndFlush(bool buffered, bool permanent)
    {
        var original = RecycledImage();
        if (permanent)
        {
            var seed = new Disk(original); var seedWork = BatchWork(ref seed);
            Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref seed, ref seedWork, 3));
        }
        var before = AllocatedState(original);
        var baseline = new Disk(original.ToArray()) { Buffered = true };
        var work = buffered ? BufferedWork(ref baseline) : BatchWork(ref baseline);
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref baseline, ref work, 3), work.Error.ToString());
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref baseline, ref work), work.Error.ToString());
        var after = AllocatedState(baseline.Snapshot());
        for (var cut = 1; cut <= baseline.IoEvents; cut++)
        {
            var disk = new Disk(original.ToArray()) { Buffered = true };
            work = buffered ? BufferedWork(ref disk) : BatchWork(ref disk);
            disk.CutAfterIo = cut;
            Assert.Throws<PowerCut>(() =>
            {
                Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3), work.Error.ToString());
                Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
            });
            var recovered = new Disk(disk.Snapshot()); work = BatchWork(ref recovered);
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work), $"cut {cut}: {work.Error}");
            Assert.True(AmigaSmartFileSystemCore.Mount(ref recovered, work.Meta));
            var state = AllocatedState(recovered.Snapshot());
            Assert.True(state.SequenceEqual(before) || state.SequenceEqual(after), $"buffered {buffered}, permanent {permanent}, cut {cut}/{baseline.IoEvents}");
        }
    }
}
