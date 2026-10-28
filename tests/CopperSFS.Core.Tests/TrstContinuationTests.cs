using System.Buffers.Binary;
using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private static (byte[] Image, byte[] Bytes, HashSet<int> Used) ContinuationSeed(int first = 2000, bool nextExtent = false, int length = 128, bool constrainReplacementRuns = false)
    {
        const int total = 16384;
        var basic = BitmapRunFixture(first + 1, length, false);
        var image = new byte[total * Bytes]; basic.Image.CopyTo(image, 0);
        var used = Enumerable.Range(0, 1000).ToHashSet(); used.Add(total - 1);
        var firstLength = nextExtent ? length - 32 : length;
        used.UnionWith(Enumerable.Range(first, firstLength));
        if (nextExtent) used.UnionWith(Enumerable.Range(6000, length - firstLength));
        // Force the short first hole to remain the best placement for tests
        // that must reach a retired-but-durable source boundary. Later holes
        // can hold a packet but cannot hold this source extent. Keep the used
        // set, bitmap checksums and root free-space count mutually consistent.
        if (constrainReplacementRuns)
            for (var block = first + firstLength + 64; block < total - 1; block += 64) used.Add(block);
        var root = Block(image, 0);
        U32(root, 44, total * Bytes); U32(root, 48, total); Sum(root);
        root.CopyTo(Block(image, total - 1)); U32(Block(image, total - 1), 8, total - 1); Sum(Block(image, total - 1));
        for (var i = 0; i < 5; i++)
        {
            var map = Block(image, 12 + i); map.Clear(); Header(image, 12 + i, Bitmap);
            for (var b = 0; b < 4000 && i * 4000 + b < total; b++)
                if (!used.Contains(i * 4000 + b)) map[12 + b / 8] |= (byte)(128 >> (b & 7));
            Sum(map);
        }
        U32(Block(image, 2), Bytes - 28, (uint)(total - used.Count)); Sum(Block(image, 2));
        U32(Block(image, 7), basic.ObjectAt + 16, (uint)(length * Bytes)); Sum(Block(image, 7));
        var tree = Block(image, 5); tree.Clear(); Header(image, 5, Tree);
        U16(tree, 12, (ushort)(nextExtent ? 3 : 2)); tree[14] = 1; tree[15] = 14;
        U32(tree, 16, 24); U16(tree, 28, 1);
        U32(tree, 30, (uint)first); U16(tree, 42, (ushort)firstLength);
        if (nextExtent) { U32(tree, 34, 6000); U32(tree, 44, 6000); U32(tree, 52, (uint)first); U16(tree, 56, (ushort)(length - firstLength)); }
        Sum(tree);
        var contents = Enumerable.Range(0, length * Bytes).Select(i => (byte)(i * 13 + 3)).ToArray();
        contents.AsSpan(0, firstLength * Bytes).CopyTo(image.AsSpan(first * Bytes));
        if (nextExtent) contents.AsSpan(firstLength * Bytes).CopyTo(image.AsSpan(6000 * Bytes));
        return (image, contents, used);
    }

    private static Disk StageContinuation(byte[] image, byte[] expected, int writeBytes = 1024, bool scratchView = false)
    {
        var disk = new Disk(image.ToArray()) { Buffered = true, CheckReadOnlyViews = true, ReturnScratchMetadataViews = scratchView };
        Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
        for (var i = 0; i < 2; i++)
        {
            var payload = Enumerable.Repeat((byte)(0xA1 + i), writeBytes).ToArray();
            Assert.True(GroupWrite(ref disk, 3, (uint)(i * writeBytes), payload));
            payload.CopyTo(expected, i * writeBytes);
        }
        Assert.Equal(3u, disk.ReadUInt32(GroupAddress, 76));
        return disk;
    }

    [Theory]
    [InlineData(2000, false, false)] [InlineData(2000, true, false)]
    [InlineData(2000, false, true)] [InlineData(2000, true, true)]
    [InlineData(3984, false, false)] [InlineData(3984, true, true)]
    public void TrstContinuationUpdatesOwnedImagesWithoutCloningAndPreservesNeighbors(int first, bool nextExtent, bool scratchView)
    {
        var seed = ContinuationSeed(first, nextExtent); var expected = seed.Bytes.ToArray();
        var disk = StageContinuation(seed.Image, expected, 8192, scratchView);
        var active = disk.RedoOverlay;
        var payload = Enumerable.Range(0, 8192).Select(i => (byte)(i * 31 + 9)).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 16384, payload));
        Assert.Equal(active, disk.RedoOverlay); // The generic path must swap arenas.
        payload.CopyTo(expected, 16384);
        Assert.Equal(expected, GroupRead(ref disk, 3));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref disk, 4));
        var work = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var final = disk.Snapshot(); var remount = new Disk(final);
        Assert.Equal(expected, GroupRead(ref remount, 3));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
        Assert.True(AmigaSmartFileSystemCore.CountUsed(ref remount, work.Meta, out var used));
        Assert.Equal((uint)seed.Used.Count - 2, used);
        var expectedUsed = seed.Used.ToHashSet();
        foreach (var b in Enumerable.Range(first, 48)) Assert.True(expectedUsed.Remove(b));
        foreach (var b in Enumerable.Range(1000, 48)) Assert.True(expectedUsed.Add(b));
        for (var b = 0; b < 16384; b++)
        {
            var map = Block(final, 12 + b / 4000); var bit = b % 4000;
            Assert.Equal(!expectedUsed.Contains(b), (map[12 + bit / 8] & (128 >> (bit & 7))) != 0);
        }
        var leaf = Block(final, 5); var recordCount = BinaryPrimitives.ReadUInt16BigEndian(leaf[12..]);
        uint key = 1000, previous = 0x80000003; var allocated = 0;
        while (key != 0)
        {
            var at = 16;
            while (at < 16 + recordCount * 14 && BinaryPrimitives.ReadUInt32BigEndian(leaf[at..]) != key) at += 14;
            Assert.True(at < 16 + recordCount * 14);
            Assert.Equal(previous, BinaryPrimitives.ReadUInt32BigEndian(leaf[(at + 8)..]));
            allocated += BinaryPrimitives.ReadUInt16BigEndian(leaf[(at + 12)..]); Assert.InRange(allocated, 1, 128);
            previous = key; key = BinaryPrimitives.ReadUInt32BigEndian(leaf[(at + 4)..]);
        }
        Assert.Equal(128, allocated);
    }

    [Theory]
    [InlineData(32768, false)] [InlineData(32768, true)]
    [InlineData(65536, false)] [InlineData(65536, true)]
    public void TrstContinuationUpdatesMultipleBitmapWordsAtMaximumPacketSize(int packetBytes, bool scratchView)
    {
        var seed = ContinuationSeed(nextExtent: true, length: 1024);
        var expected = seed.Bytes.ToArray();
        var disk = StageContinuation(seed.Image, expected, packetBytes, scratchView);
        var active = disk.RedoOverlay;
        var payload = Enumerable.Range(0, packetBytes).Select(i => (byte)(i * 37 + 9)).ToArray();
        Assert.True(GroupWrite(ref disk, 3, (uint)(packetBytes * 2), payload));
        Assert.Equal(active, disk.RedoOverlay);
        payload.CopyTo(expected, packetBytes * 2);
        // Read in bounded chunks; a single large host read would overlap the
        // group's caller-owned RAM at GroupAddress and invalidate the test.
        byte[] Contents(ref Disk source)
        {
            var result = new byte[expected.Length]; var work = TrstWork();
            for (var offset = 0; offset < result.Length; offset += 65536)
            {
                var count = Math.Min(65536, result.Length - offset);
                Assert.Equal(count, AmigaSmartFileSystemCore.Read(ref source, 3, (uint)offset,
                    APTR.FromPointer(4000), (uint)count, work.Meta, work.Data, out var error));
                Assert.Equal(DOS.Error.None, error);
                source.Ram.AsSpan(4000, count).CopyTo(result.AsSpan(offset));
            }
            return result;
        }
        Assert.Equal(expected, Contents(ref disk));
        var work = TrstWork();
        Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var final = disk.Snapshot(); var remount = new Disk(final);
        Assert.Equal(expected, Contents(ref remount));
        Assert.Equal("hello"u8.ToArray(), GroupRead(ref remount, 4));
        var expectedUsed = seed.Used.ToHashSet();
        foreach (var b in Enumerable.Range(2000, 3 * packetBytes / Bytes)) Assert.True(expectedUsed.Remove(b));
        foreach (var b in Enumerable.Range(1000, 3 * packetBytes / Bytes)) Assert.True(expectedUsed.Add(b));
        for (var b = 0; b < 16384; b++)
        {
            var map = Block(final, 12 + b / 4000); var bit = b % 4000;
            Assert.Equal(!expectedUsed.Contains(b), (map[12 + bit / 8] & (128 >> (bit & 7))) != 0);
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void TrstContinuationDataFailuresRetainEveryAcceptedImageAndAllowRetry(bool scratchView)
    {
        var seed = ContinuationSeed(nextExtent: true);
        for (var failure = 1; failure <= 16; failure++)
        {
            var expected = seed.Bytes.ToArray(); var disk = StageContinuation(seed.Image, expected, 8192, scratchView);
            var active = disk.RedoOverlay; var span = (int)disk.ReadUInt32(GroupAddress, 12);
            var checkpoint = disk.Ram.AsSpan((int)active.Raw, span).ToArray();
            disk.FailAtWrite = disk.Writes + failure;
            var payload = Enumerable.Repeat((byte)0xF1, 8192).ToArray();
            Assert.False(GroupWrite(ref disk, 3, 16384, payload));
            Assert.Equal(active, disk.RedoOverlay);
            Assert.Equal(checkpoint, disk.Ram.AsSpan((int)active.Raw, span).ToArray());
            Assert.Equal(expected, GroupRead(ref disk, 3));
            disk.FailAtWrite = 0;
            Assert.True(GroupWrite(ref disk, 3, 16384, payload)); payload.CopyTo(expected, 16384);
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
            var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
        }
    }

    [Theory]
    [InlineData(32, 0u)] [InlineData(36, uint.MaxValue)] [InlineData(40, 0u)]
    [InlineData(8, 1u)] [InlineData(16, 1u)] [InlineData(24, 1u)]
    public void TrstContinuationRejectsCorruptPrivateRecordsBeforeDataIo(int field, uint value)
    {
        var seed = ContinuationSeed(nextExtent: true); var expected = seed.Bytes.ToArray();
        var disk = StageContinuation(seed.Image, expected);
        var active = disk.RedoOverlay; var writes = disk.Writes;
        disk.WriteUInt32(GroupAddress, 76 + field, value);
        Assert.False(GroupWrite(ref disk, 3, 2048, new byte[1024]));
        Assert.Equal(writes, disk.Writes); Assert.Equal(active, disk.RedoOverlay);
        Assert.Equal(expected, GroupRead(ref disk, 3));
    }

    [Fact]
    public void TrstContinuationFallsBackForNonSequentialPartialAndOtherFileWrites()
    {
        var seed = ContinuationSeed(); var expected = seed.Bytes.ToArray();
        var disk = StageContinuation(seed.Image, expected);
        var active = disk.RedoOverlay;
        var partial = Enumerable.Repeat((byte)0x71, 37).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 2051, partial)); partial.CopyTo(expected, 2051);
        Assert.NotEqual(active, disk.RedoOverlay);
        Assert.Equal(0u, disk.ReadUInt32(GroupAddress, 76));
        Assert.True(GroupWrite(ref disk, 4, 0, "world"u8.ToArray()));
        var overwrite = Enumerable.Repeat((byte)0x81, 1024).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 4096, overwrite)); overwrite.CopyTo(expected, 4096);
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
        Assert.Equal("world"u8.ToArray(), GroupRead(ref remount, 4));
    }

    [Fact]
    public void TrstContinuationDoesNotWriteRetiredDurableDataOutsideTheJournal()
    {
        var seed = ContinuationSeed(1040, constrainReplacementRuns: true); var expected = seed.Bytes.ToArray();
        var disk = StageContinuation(seed.Image, expected, 8192); var active = disk.RedoOverlay;
        var payload = Enumerable.Repeat((byte)0xC7, 8192).ToArray();
        // The next physical run reaches blocks retired by earlier accepted
        // Writes but still reachable on disk. Choose a different free run;
        // never issue unpublished writes to those retired source blocks.
        Assert.True(GroupWrite(ref disk, 3, 16384, payload)); payload.CopyTo(expected, 16384);
        Assert.NotEqual(active, disk.RedoOverlay);
        Assert.Equal(seed.Bytes, disk.Snapshot().AsSpan(1040 * Bytes, seed.Bytes.Length).ToArray());
        Assert.Equal(expected, GroupRead(ref disk, 3));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Fact]
    public void TrstContinuationRetainsTheConservativeJournalSpaceGate()
    {
        var seed = ContinuationSeed(); var expected = seed.Bytes.ToArray();
        var disk = StageContinuation(seed.Image, expected); var active = disk.RedoOverlay;
        // A deliberately pessimistic lower bound must select the full allocator.
        disk.WriteUInt32(GroupAddress, 60, 0);
        var payload = Enumerable.Repeat((byte)0xC9, 1024).ToArray();
        Assert.True(GroupWrite(ref disk, 3, 2048, payload)); payload.CopyTo(expected, 2048);
        Assert.NotEqual(active, disk.RedoOverlay);
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        var remount = new Disk(disk.Snapshot()); Assert.Equal(expected, GroupRead(ref remount, 3));
    }

    [Fact]
    public void TrstContinuationSectorCutsAndReorderedPendingWritesRecoverWholeGroup()
    {
        var seed = ContinuationSeed(nextExtent: true);
        var final = seed.Bytes.ToArray();
        var payloads = Enumerable.Range(0, 3).Select(n => Enumerable.Repeat((byte)(0xD0 + n), 1024).ToArray()).ToArray();
        for (var i = 0; i < payloads.Length; i++) payloads[i].CopyTo(final, i * 1024);
        void Run(ref Disk disk)
        {
            Assert.True(AmigaSmartFileSystemWriteCore.InitializeTrstGroup(ref disk, GroupAddress, 192 * 1024));
            APTR accepted = APTR.Null;
            for (var i = 0; i < 3; i++)
            {
                Assert.True(GroupWrite(ref disk, 3, (uint)(i * 1024), payloads[i]));
                if (i == 1) accepted = disk.RedoOverlay;
                if (i == 2) Assert.Equal(accepted, disk.RedoOverlay);
            }
            var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref disk, ref work, GroupAddress));
        }
        var baseline = new Disk(seed.Image.ToArray()) { Buffered = true }; Run(ref baseline);
        var done = new Disk(baseline.Snapshot()); Assert.Equal(final, GroupRead(ref done, 3));
        for (var cut = 1; cut <= baseline.IoEvents; cut++)
        {
            var disk = new Disk(seed.Image.ToArray()) { Buffered = true, CutAfterIo = cut };
            Assert.Throws<PowerCut>(() => Run(ref disk));
            foreach (var image in disk.CrashImages())
            {
                var recovered = new Disk(image); var work = TrstWork();
                Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work));
                var contents = GroupRead(ref recovered, 3);
                Assert.True(contents.SequenceEqual(seed.Bytes) || contents.SequenceEqual(final), $"Partial group after cut {cut}");
                Assert.Equal("hello"u8.ToArray(), GroupRead(ref recovered, 4));
            }
        }
    }
}
