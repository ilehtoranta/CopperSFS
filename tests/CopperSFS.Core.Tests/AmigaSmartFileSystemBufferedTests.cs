using Amiga;
using CopperSFS;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    [Theory]
    [InlineData(0u)]
    [InlineData(40u)]
    public void ContiguousAppendKeepsReadOnlyMetadataUnchangedUntilCheckpointCapture(uint preferred)
    {
        var disk = new Disk(Image()) { Buffered = true, CheckReadOnlyViews = true };
        var work = BufferedWork(ref disk);
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = disk.Name("Run"), Length = 3 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false));
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work));
        var node = request.Node;
        work.AllocationCursor = work.JournalCursor = preferred;
        for (var index = 0u; index < 3; index++)
        {
            Array.Fill(disk.Ram, (byte)0xA7, 4000, 2048);
            request = new() { Node = node, Position = index * 2048, Buffer = APTR.FromPointer(4000), Length = 2048 };
            Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out var actual));
            Assert.Equal(2048u, actual);
        }
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work));
        var read = AmigaSmartFileSystemCore.Read(ref disk, node, 0, APTR.FromPointer(32768), 6144,
            work.Meta, work.Data, out var error);
        Assert.Equal(DOS.Error.None, error); Assert.Equal(6144, read);
        Assert.All(disk.Ram.AsSpan(32768, 6144).ToArray(), b => Assert.Equal(0xA7, b));
        Assert.Equal(0, disk.GeometryReads);
    }

    [Fact]
    public void HashedDirectoryLookupUsesNodeHashesAndChecksContainerOwnership()
    {
        var image = Image();
        Header(image, 11, 0x48544142);
        var table = Block(image, 11);
        U32(table, 12, 1); U32(table, 20, 3); Sum(table); // HELLO hash 15129, bucket 1
        U32(Block(image, 2), 36, 11); Sum(Block(image, 2));
        U16(Block(image, 6), 48, 15129); Sum(Block(image, 6));
        var disk = new Disk(image);
        Assert.Equal(3u, AmigaSmartFileSystemCore.Resolve(ref disk, 1, disk.Name("hello"), 5,
            APTR.FromPointer(512), out var error, out var record, out var at));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(1300u, disk.ReadUInt32(record, at + 16));
        Assert.Equal(1u, disk.ReadUInt32(record, 12));
        Assert.Equal(3u, disk.ReadUInt32(record, at + 4));
        U32(table, 12, 2); Sum(table);
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref disk, 1, disk.Name("hello"), 5,
            APTR.FromPointer(512), out error));
        Assert.Equal(DOS.Error.DiskNotValidated, error);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void HashedNamesStayVisibleAcrossCreateRenameDeleteAndRemount(bool buffered)
    {
        var image = Image();
        Header(image, 11, 0x48544142);
        var table = Block(image, 11); U32(table, 12, 1);
        static ushort HashName(string name)
        {
            var hash = (uint)name.Length;
            foreach (var value in System.Text.Encoding.Latin1.GetBytes(name.ToUpperInvariant()))
                hash = (hash * 13 + value) & 65535;
            return (ushort)hash;
        }
        foreach (var pair in new[] { ("Docs", 2u), ("Hello", 3u) })
        {
            var hash = HashName(pair.Item1);
            U32(table, 16 + hash % 124 * 4, pair.Item2);
            U16(Block(image, 6), 20 + (int)(pair.Item2 - 1) * 10 + 8, hash);
        }
        Sum(table); Sum(Block(image, 6));
        U32(Block(image, 2), 36, 11); U32(Block(image, 2), Bytes - 28, 81); Sum(Block(image, 2));
        var map = Block(image, 9);
        U32(map, 12, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(map[12..]) & ~(1u << 20)); Sum(map);
        var disk = new Disk(image);
        var work = buffered ? BufferedWork(ref disk) : BatchWork(ref disk);
        uint renamed = 0;
        foreach (var name in new[] { "Alpha", "Beta", "Gamma", "äöhello", "Delta", "Epsilon" })
        {
            var request = new AmigaSmartFileSystemWriteCore.Request
            { Directory = 1, Name = disk.Name(name), Length = (uint)name.Length };
            Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false), work.Error.ToString());
            if (name == "äöhello") renamed = request.Node;
            Assert.Equal(request.Node, AmigaSmartFileSystemCore.Resolve(ref disk, 1, disk.Name(name.ToUpperInvariant()),
                (uint)name.Length, work.Meta, out _));
        }
        var rename = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Node = renamed, Name = disk.Name("Renamed"), Length = 7 };
        Assert.True(AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref rename), work.Error.ToString());
        var beta = AmigaSmartFileSystemCore.Resolve(ref disk, 1, disk.Name("beta"), 4, work.Meta, out _);
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, beta), work.Error.ToString());
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
        var remounted = new Disk(disk.Snapshot());
        Assert.Equal(renamed, AmigaSmartFileSystemCore.Resolve(ref remounted, 1, remounted.Name("renamed"), 7, work.Meta, out _));
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref remounted, 1, remounted.Name("beta"), 4, work.Meta, out var error));
        Assert.Equal(DOS.Error.ObjectNotFound, error);
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref remounted, 1, remounted.Name("äöhello"), 7, work.Meta, out error));
        Assert.Equal(DOS.Error.ObjectNotFound, error);
    }

    private static AmigaSmartFileSystemWriteCore.Work BufferedWork(ref Disk disk, uint capacity = 32)
    {
        var work = BatchWork(ref disk);
        disk.WriteBack = work.Buffered = APTR.FromPointer(65536);
        Assert.True(AmigaSmartFileSystemWriteBuffer.Initialize(ref disk, work.Buffered, Bytes, capacity));
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work));
        return work;
    }

    private static void BufferedOverwrite(ref Disk disk, ref AmigaSmartFileSystemWriteCore.Work work, byte value)
    {
        Array.Fill(disk.Ram, value, 4000, 1300);
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = 3, Buffer = APTR.FromPointer(4000), Length = 1300 };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out var actual), work.Error.ToString());
        Assert.Equal(1300u, actual);
    }

    private static byte[] ReadBufferedFile(ref Disk disk, uint node = 3, uint length = 1300)
    {
        Assert.Equal((int)length, AmigaSmartFileSystemCore.Read(ref disk, node, 0,
            APTR.FromPointer(4000), length, APTR.FromPointer(512), APTR.FromPointer(1024), out var error));
        Assert.Equal(DOS.Error.None, error);
        return disk.Ram.AsSpan(4000, (int)length).ToArray();
    }

    [Theory]
    [InlineData(512u)]
    [InlineData(2048u)]
    [InlineData(8192u)]
    public void BufferedAppendReservesJournalOutsideNewDataRuns(uint chunkBytes)
    {
        var disk = new Disk(Image()) { Buffered = true };
        var work = BufferedWork(ref disk);
        var create = new AmigaSmartFileSystemWriteCore.Request { Directory = 1, Name = disk.Name("Append"), Length = 6 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref create, false));
        work.JournalCursor = work.AllocationCursor = 40;
        for (uint position = 0; position < 16384; position += chunkBytes)
        {
            for (var i = 0u; i < chunkBytes; i++) disk.Ram[32768 + i] = (byte)((position + i) * 29 + 7);
            var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = create.Node, Position = position, Buffer = APTR.FromPointer(32768), Length = chunkBytes };
            Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out _), work.Error.ToString());
        }
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
        var remounted = new Disk(disk.Snapshot());
        var contents = ReadBufferedFile(ref remounted, create.Node, 16384);
        Assert.Equal(Enumerable.Range(0, 16384).Select(i => (byte)(i * 29 + 7)), contents);
    }

    [Fact]
    public void BufferedWritesAreVisibleToReadersAndBecomeDurableTogether()
    {
        var disk = new Disk(Image()) { Buffered = true };
        var work = BufferedWork(ref disk);
        BufferedOverwrite(ref disk, ref work, 0xA7);
        BufferedOverwrite(ref disk, ref work, 0xB8);
        Assert.All(ReadBufferedFile(ref disk), b => Assert.Equal(0xB8, b));
        Assert.Equal(0, disk.Flushes);
        Assert.Equal(0, disk.Writes);
        var crashed = new Disk(disk.Snapshot());
        Assert.Equal(Enumerable.Range(0, 1300).Select(i => (byte)(i * 17)), ReadBufferedFile(ref crashed));
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
        Assert.Equal(4, disk.Flushes);
        var remounted = new Disk(disk.Snapshot());
        Assert.All(ReadBufferedFile(ref remounted), b => Assert.Equal(0xB8, b));
    }

    [Fact]
    public void FailedBufferedOperationRestoresItsSavepointAndKeepsEarlierWrites()
    {
        var disk = new Disk(Image()) { Buffered = true };
        var work = BufferedWork(ref disk);
        BufferedOverwrite(ref disk, ref work, 0xA7);
        // Extending this file updates its bitmap and extent before the new data
        // device write fails; the previous buffered file must survive intact.
        disk.FailAtWrite = 1;
        Array.Fill(disk.Ram, (byte)0xCC, 4000, 3000);
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = 3, Buffer = APTR.FromPointer(4000), Length = 3000 };
        Assert.False(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out var actual));
        Assert.Equal(0u, actual);
        Assert.All(ReadBufferedFile(ref disk), b => Assert.Equal(0xA7, b));
        disk.FailAtWrite = 0;
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
        var remounted = new Disk(disk.Snapshot());
        Assert.All(ReadBufferedFile(ref remounted), b => Assert.Equal(0xA7, b));
    }

    [Fact]
    public void OversizedOperationCommitsPriorChangesThenUsesStrictFallback()
    {
        var disk = new Disk(Image()) { Buffered = true };
        var work = BufferedWork(ref disk, 2);
        BufferedOverwrite(ref disk, ref work, 0xA7);
        Assert.True(disk.Flushes >= 4);
        Assert.Equal(0u, AmigaSmartFileSystemWriteBuffer.Count(ref disk, work.Buffered));
        var remounted = new Disk(disk.Snapshot());
        Assert.All(ReadBufferedFile(ref remounted), b => Assert.Equal(0xA7, b));
    }

    [Fact]
    public void EveryBufferedCommitCutRecoversAnEntireCheckpoint()
    {
        var initial = Image();
        var probe = new Disk(initial.ToArray()) { Buffered = true };
        var work = BufferedWork(ref probe);
        BufferedOverwrite(ref probe, ref work, 0xA7);
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref probe, ref work));
        for (var cut = 1; cut <= probe.IoEvents; cut++)
        {
            var disk = new Disk(initial.ToArray()) { Buffered = true };
            work = BufferedWork(ref disk);
            BufferedOverwrite(ref disk, ref work, 0xA7);
            disk.CutAfterIo = cut;
            Assert.Throws<PowerCut>(() => AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work));
            foreach (var image in disk.CrashImages())
            {
                var recovered = new Disk(image);
                var recovery = BatchWork(ref recovered);
                Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref recovery), $"cut {cut}: {recovery.Error}");
                var contents = ReadBufferedFile(ref recovered);
                Assert.True(contents.All(b => b == 0xA7) || contents.SequenceEqual(
                    Enumerable.Range(0, 1300).Select(i => (byte)(i * 17))), $"mixed checkpoint at {cut}");
            }
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    public void FailedBufferedFlushRetainsAcknowledgedDataForRetry(int failedFlush)
    {
        var disk = new Disk(Image()) { Buffered = true };
        var work = BufferedWork(ref disk);
        BufferedOverwrite(ref disk, ref work, 0xA7);
        disk.FailAtFlush = failedFlush;
        Assert.False(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work));
        disk.FailAtFlush = 0;
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
        var remounted = new Disk(disk.Snapshot());
        Assert.All(ReadBufferedFile(ref remounted), b => Assert.Equal(0xA7, b));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, false)]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(0, true)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void BufferedMetadataGroupsSurviveEveryCutAndPreserveEarlierCheckpoint(int operation, bool volatileDevice)
    {
        var seed = new Disk(Image());
        var seedWork = BatchWork(ref seed);
        BufferedOverwrite(ref seed, ref seedWork, 0x62); // durable checkpoint
        var checkpoint = seed.Snapshot();
        var probe = new Disk(checkpoint.ToArray()) { Buffered = volatileDevice };
        var work = BufferedWork(ref probe);
        Mutate(ref probe, ref work);
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref probe, ref work), work.Error.ToString());
        for (var cut = 1; cut <= probe.IoEvents; cut++)
        {
            var disk = new Disk(checkpoint.ToArray()) { Buffered = volatileDevice, CutAfterIo = cut };
            work = BufferedWork(ref disk);
            Assert.Throws<PowerCut>(() =>
            {
                Mutate(ref disk, ref work);
                AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work);
            });
            var recovered = new Disk(disk.Snapshot());
            var recovery = BatchWork(ref recovered);
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref recovery), $"{operation}/{cut}: {recovery.Error}");
            Assert.True(AmigaSmartFileSystemCore.Mount(ref recovered, recovery.Meta));
            if (operation is 0 or 2) Assert.All(ReadBufferedFile(ref recovered), b => Assert.Equal(0x62, b));
            if (operation == 1)
            {
                Assert.True(AmigaSmartFileSystemCore.Describe(ref recovered, 3, recovery.Meta, out var size, out _, out _));
                Assert.True(size is 600 or 1300);
                Assert.All(ReadBufferedFile(ref recovered, 3, size), b => Assert.Equal(0x62, b));
            }
            if (operation == 3 && AmigaSmartFileSystemCore.Describe(ref recovered, 3, recovery.Meta, out _, out _, out _))
                Assert.All(ReadBufferedFile(ref recovered), b => Assert.Equal(0x62, b));
        }

        void Mutate(ref Disk disk, ref AmigaSmartFileSystemWriteCore.Work state)
        {
            var request = new AmigaSmartFileSystemWriteCore.Request
            { Directory = 1, Node = 3, Name = disk.Name("Buffered"), Length = 8 };
            var result = operation switch
            {
                0 => AmigaSmartFileSystemWriteCore.Create(ref disk, ref state, ref request, false),
                1 => AmigaSmartFileSystemWriteCore.Resize(ref disk, ref state, 3, 600),
                2 => AmigaSmartFileSystemWriteCore.Rename(ref disk, ref state, ref request),
                _ => AmigaSmartFileSystemWriteCore.Remove(ref disk, ref state, 3)
            };
            Assert.True(result, state.Error.ToString());
        }
    }

    [Theory]
    [InlineData(1u)]
    [InlineData(32u)]
    public void DirectoryCursorPreservesCopiedFibTokensAndMutationInvalidation(uint capacity)
    {
        var disk = new Disk(Image());
        var work = BatchWork(ref disk);
        for (var i = 0; i < 22; i++)
        {
            var request = new AmigaSmartFileSystemWriteCore.Request
            { Directory = 1, Name = disk.Name($"Cursor{i:D2}"), Length = 8 };
            Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false));
        }
        var fib = APTR.FromPointer(4000); var expectedFib = APTR.FromPointer(5000);
        var cursor = new AmigaSmartFileSystemCore.DirectoryCursor { Entries = APTR.FromPointer(6000), Capacity = capacity };
        Assert.True(AmigaSmartFileSystemCore.Examine(ref disk, 1, fib, work.Meta));
        disk.Copy(fib, expectedFib, FileInfoBlock.SizeInBytes);
        byte[]? saved = null;
        for (var step = 0; step < 40; step++)
        {
            var expected = AmigaSmartFileSystemCore.ExamineNext(ref disk, 1, expectedFib, work.Meta, work.Data);
            var actual = AmigaSmartFileSystemCore.ExamineNext(ref disk, ref cursor, 1, fib, work.Meta, work.Data);
            Assert.Equal(expected, actual);
            Assert.Equal(disk.Ram.AsSpan(5000, (int)FileInfoBlock.SizeInBytes).ToArray(),
                disk.Ram.AsSpan(4000, (int)FileInfoBlock.SizeInBytes).ToArray());
            if (step == 1) saved = disk.Ram.AsSpan(4000, (int)FileInfoBlock.SizeInBytes).ToArray();
            if (step == 4) { saved!.CopyTo(disk.Ram, 4000); saved.CopyTo(disk.Ram, 5000); }
            if (step == 8) cursor.Wanted = 0;
            if (actual == DOS.Error.NoMoreEntries) break;
        }
    }
}
