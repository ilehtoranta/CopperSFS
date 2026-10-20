using System.Buffers.Binary;
using Amiga;
using CopperSFS;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private static AmigaSmartFileSystemWriteCore.Work BatchWork(ref Disk disk, uint capacity = 32)
    {
        disk.Batch = APTR.FromPointer(8192);
        Assert.True(AmigaSmartFileSystemTransactionBuffer.Initialize(ref disk, disk.Batch, Bytes, capacity));
        return new()
        {
            Meta = APTR.FromPointer(512), Data = APTR.FromPointer(1024),
            Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048), Batch = disk.Batch
        };
    }

    // Compare the fixture's allocated sectors, ignoring leftover journal bytes
    // in free space. This checks file data, nodes, extents, bitmap and free count.
    private static byte[] AllocatedState(byte[] image)
    {
        var result = new List<byte>();
        var map = image.AsSpan(9 * Bytes, Bytes);
        for (var block = 0; block < Blocks; block++)
        {
            var bits = BinaryPrimitives.ReadUInt32BigEndian(map[(12 + block / 32 * 4)..]);
            if ((bits & (1u << (31 - block % 32))) == 0)
            {
                result.AddRange(BitConverter.GetBytes(block));
                result.AddRange(image.AsSpan(block * Bytes, Bytes).ToArray());
            }
        }
        return result.ToArray();
    }

    private static bool BatchMutation(ref Disk disk, ref AmigaSmartFileSystemWriteCore.Work work, int operation)
    {
        for (var i = 0; i < 2000; i++) disk.Ram[4000 + i] = (byte)(i * 29 + 5);
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = 3, Directory = 1, Name = disk.Name("Changed"), Length = 7, Buffer = APTR.FromPointer(4000) };
        switch (operation)
        {
            case 0: return AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false);
            case 1:
                request.Length = 1300;
                return AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out _);
            case 2:
                request.Position = 1300; request.Length = 700;
                return AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out _);
            case 3: return AmigaSmartFileSystemWriteCore.Truncate(ref disk, ref work, 3);
            case 4: return AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref request);
            case 5: return AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, 3);
            case 6: return AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetComment);
            case 7: return AmigaSmartFileSystemWriteCore.Resize(ref disk, ref work, 3, 800);
            default: return AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, true);
        }
    }

    public static IEnumerable<object[]> BatchCases()
    {
        foreach (var capacity in new uint[] { 1, 2, 32 })
        for (var operation = 0; operation < 9; operation++)
        foreach (var buffered in new[] { false, true })
            yield return [capacity, operation, buffered];
    }

    [Theory]
    [MemberData(nameof(BatchCases))]
    public void BatchedMutationsRecoverOldOrNewAcrossEveryIoAndBufferDrain(uint capacity, int operation, bool buffered)
    {
        var original = Image();
        var before = AllocatedState(original);
        var probe = new Disk(original.ToArray()) { Buffered = buffered };
        var work = BatchWork(ref probe, capacity);
        Assert.True(BatchMutation(ref probe, ref work, operation), work.Error.ToString());
        var after = AllocatedState(probe.Snapshot());
        Assert.False(before.SequenceEqual(after));
        Assert.Equal(0u, probe.ReadUInt32(probe.Batch));
        if (capacity == 32) Assert.Equal(4, probe.Flushes);

        for (var cut = 1; cut <= probe.IoEvents; cut++)
        {
            var interrupted = new Disk(original.ToArray()) { Buffered = buffered, CutAfterIo = cut };
            work = BatchWork(ref interrupted, capacity);
            Assert.Throws<PowerCut>(() => BatchMutation(ref interrupted, ref work, operation));
            var recovered = new Disk(interrupted.Snapshot());
            work = BatchWork(ref recovered, capacity);
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work),
                $"operation {operation}, capacity {capacity}, cut {cut}: {work.Error}");
            Assert.True(AmigaSmartFileSystemCore.Mount(ref recovered, work.Meta));
            var actual = AllocatedState(recovered.Snapshot());
            Assert.True(actual.SequenceEqual(before) || actual.SequenceEqual(after),
                $"operation {operation}, capacity {capacity}, cut {cut}, buffered {buffered}");
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work));
        }
    }

    [Fact]
    public void BatchedUndoPublicationToleratesEverySubsetOfEarlySectorPersistence()
    {
        var original = Image();
        var before = AllocatedState(original);
        var probe = new Disk(original.ToArray()) { Buffered = true };
        var work = BatchWork(ref probe);
        Assert.True(BatchMutation(ref probe, ref work, 1));
        var after = AllocatedState(probe.Snapshot());
        Assert.Equal(4, probe.Flushes);
        for (var cut = 1; cut <= probe.IoEvents; cut++)
        {
            var interrupted = new Disk(original.ToArray()) { Buffered = true, CutAfterIo = cut };
            work = BatchWork(ref interrupted);
            Assert.Throws<PowerCut>(() => BatchMutation(ref interrupted, ref work, 1));
            foreach (var saved in interrupted.CrashImages())
            {
                var recovered = new Disk(saved);
                work = BatchWork(ref recovered);
                Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work));
                Assert.True(AmigaSmartFileSystemCore.Mount(ref recovered, work.Meta));
                var actual = AllocatedState(recovered.Snapshot());
                Assert.True(actual.SequenceEqual(before) || actual.SequenceEqual(after), $"cut {cut}");
            }
        }
    }

    [Theory]
    [InlineData(0u, 2u)]
    [InlineData(0u, 32u)]
    [InlineData(1024u, 2u)]
    [InlineData(1024u, 32u)]
    public void BatchedContiguousAppendsPreservePreviouslyCommittedBytes(uint initialBytes, uint capacity)
    {
        var seed = new Disk(Image());
        var work = BatchWork(ref seed);
        var create = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = seed.Name("Run"), Length = 3 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref seed, ref work, ref create, false));
        if (initialBytes != 0)
        {
            Array.Fill(seed.Ram, (byte)0x73, 4000, (int)initialBytes);
            work.AllocationCursor = 25;
            var initial = new AmigaSmartFileSystemWriteCore.Request
            { Node = create.Node, Buffer = APTR.FromPointer(4000), Length = initialBytes };
            Assert.True(AmigaSmartFileSystemWriteCore.Write(ref seed, ref work, ref initial, out _));
        }
        var original = seed.Snapshot();
        var before = AllocatedState(original);
        foreach (var buffered in new[] { false, true })
        {
            var probe = new Disk(original.ToArray()) { Buffered = buffered };
            work = BatchWork(ref probe, capacity); work.AllocationCursor = 25 + initialBytes / 512;
            Array.Fill(probe.Ram, (byte)0xA7, 4000, 1024);
            var append = new AmigaSmartFileSystemWriteCore.Request
            { Node = create.Node, Position = initialBytes, Buffer = APTR.FromPointer(4000), Length = 1024 };
            Assert.True(AmigaSmartFileSystemWriteCore.Write(ref probe, ref work, ref append, out var actual));
            Assert.Equal(1024u, actual);
            if (capacity == 32) Assert.Equal(4, probe.Flushes);
            var after = AllocatedState(probe.Snapshot());
            for (var cut = 1; cut <= probe.IoEvents; cut++)
            {
                var interrupted = new Disk(original.ToArray()) { Buffered = buffered, CutAfterIo = cut };
                work = BatchWork(ref interrupted, capacity); work.AllocationCursor = 25 + initialBytes / 512;
                Array.Fill(interrupted.Ram, (byte)0xA7, 4000, 1024);
                Assert.Throws<PowerCut>(() => AmigaSmartFileSystemWriteCore.Write(ref interrupted, ref work, ref append, out _));
                var recovered = new Disk(interrupted.Snapshot());
                work = BatchWork(ref recovered, capacity);
                Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work));
                Assert.True(AmigaSmartFileSystemCore.Mount(ref recovered, work.Meta));
                var state = AllocatedState(recovered.Snapshot());
                Assert.True(state.SequenceEqual(before) || state.SequenceEqual(after),
                    $"initial {initialBytes}, capacity {capacity}, buffered {buffered}, cut {cut}");
            }
        }
    }

    [Fact]
    public void FailedBatchedIoDiscardsStagedViewBeforeRecoveryAndRetry()
    {
        var original = Image();
        var probe = new Disk(original.ToArray()) { Buffered = true };
        var work = BatchWork(ref probe, 2);
        Assert.True(BatchMutation(ref probe, ref work, 1));
        var before = AllocatedState(original); var after = AllocatedState(probe.Snapshot());
        for (var kind = 0; kind < 2; kind++)
        for (var stop = 1; stop <= (kind == 0 ? probe.Writes : probe.Flushes); stop++)
        {
            var failed = new Disk(original.ToArray()) { Buffered = true,
                FailAtWrite = kind == 0 ? stop : 0, FailAtFlush = kind == 1 ? stop : 0 };
            work = BatchWork(ref failed, 2);
            Assert.False(BatchMutation(ref failed, ref work, 1));
            failed.FailAtWrite = failed.FailAtFlush = 0;
            // Reuse the same memory/cache, as the live handler does after an I/O error.
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref failed, ref work));
            Assert.Equal(0u, failed.ReadUInt32(failed.Batch));
            Assert.True(failed.Flush());
            var actual = AllocatedState(failed.Snapshot());
            Assert.True(actual.SequenceEqual(before) || actual.SequenceEqual(after), $"kind {kind}, stop {stop}");
            Assert.True(BatchMutation(ref failed, ref work, 1));
            Assert.Equal(after, AllocatedState(failed.Snapshot()));
        }
    }
}
