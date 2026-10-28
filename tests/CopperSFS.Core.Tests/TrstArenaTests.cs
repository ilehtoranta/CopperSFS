using System.Buffers.Binary;
using Amiga;

namespace CopperSFS.Tests;

public sealed class TrstArenaTests
{
    private struct Memory(int length) : IAmigaFileSystemPlatform
    {
        public readonly byte[] Bytes = Enumerable.Repeat((byte)0xA5, length).ToArray();
        public int Reads;
        public AmigaVolumeGeometry Geometry => default;
        public uint BlockBytes => 512;
        public uint BlockCount => uint.MaxValue;
        public uint ReservedBlocks => 1;
        public bool ReadBlock(uint block, APTR destination) => throw new InvalidOperationException("Arena access must not issue disk I/O");
        public bool ReadMetadataBlock(uint block, uint id, APTR destination) => throw new InvalidOperationException();
        public APTR ReadMetadataView(uint block, uint id, APTR scratch) => throw new InvalidOperationException();
        public uint ReadDataBlocks(uint block, uint count, APTR destination) => throw new InvalidOperationException();
        public byte ReadUInt8(APTR a, int offset = 0) => Bytes[(int)a.Raw + offset];
        public ushort ReadUInt16(APTR a, int offset = 0) => BinaryPrimitives.ReadUInt16BigEndian(Bytes.AsSpan((int)a.Raw + offset));
        public uint ReadUInt32(APTR a, int offset = 0)
        { Reads++; return BinaryPrimitives.ReadUInt32BigEndian(Bytes.AsSpan((int)a.Raw + offset)); }
        public void WriteUInt8(APTR a, int offset, byte value) => Bytes[(int)a.Raw + offset] = value;
        public void WriteUInt16(APTR a, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Bytes.AsSpan((int)a.Raw + offset), value);
        public void WriteUInt32(APTR a, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Bytes.AsSpan((int)a.Raw + offset), value);
        public void Clear(APTR a, uint length) => Bytes.AsSpan((int)a.Raw, (int)length).Clear();
        public void Copy(APTR a, APTR b, uint length) => Bytes.AsSpan((int)a.Raw, (int)length).CopyTo(Bytes.AsSpan((int)b.Raw));
        public bool IsMapped(APTR a, uint length) => a.Raw <= Bytes.Length && length <= Bytes.Length - a.Raw;
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(2048)]
    [InlineData(4096)]
    [InlineData(32768)]
    public void FullArenaCoalescesWritesAndResetsWithoutCrossingItsBounds(int blockBytes)
    {
        const uint arenaBytes = 192 * 1024;
        var arena = APTR.FromPointer(64); var source = APTR.FromPointer(arena.Raw + arenaBytes + 64);
        var p = new Memory((int)source.Raw + blockBytes);
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, arenaBytes, (uint)blockBytes));
        var capacity = p.ReadUInt32(arena, 4);
        if (blockBytes == 512) Assert.Equal(374u, capacity);
        for (var i = 0u; i < capacity; i++)
        {
            p.Bytes.AsSpan((int)source.Raw, blockBytes).Fill((byte)i);
            Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, i, source));
        }
        Assert.False(AmigaSmartFileSystemRedoArena.Store(ref p, arena, uint.MaxValue, source));
        Assert.Equal(1u, p.ReadUInt32(arena, 12));
        // Existing keys must remain writable even when there are no free slots.
        p.Bytes.AsSpan((int)source.Raw, blockBytes).Fill(0xF3);
        Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, 0, source));
        Assert.Equal(capacity, p.ReadUInt32(arena));
        for (var i = 0u; i < capacity; i++)
        {
            var image = AmigaSmartFileSystemRedoArena.Find(ref p, arena, i);
            Assert.True(image.IsNotNull);
            Assert.All(p.Bytes.AsSpan((int)image.Raw, blockBytes).ToArray(), b => Assert.Equal(i == 0 ? (byte)0xF3 : (byte)i, b));
        }
        Assert.All(p.Bytes.AsSpan(0, 64).ToArray(), b => Assert.Equal(0xA5, b));
        Assert.All(p.Bytes.AsSpan((int)(arena.Raw + arenaBytes), 64).ToArray(), b => Assert.Equal(0xA5, b));
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, arenaBytes, (uint)blockBytes));
        Assert.Equal(0u, p.ReadUInt32(arena, 12));
        for (var i = 0u; i < capacity; i++) Assert.True(AmigaSmartFileSystemRedoArena.Find(ref p, arena, i).IsNull);
        Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, uint.MaxValue, source));
        Assert.True(AmigaSmartFileSystemRedoArena.Find(ref p, arena, uint.MaxValue).IsNotNull);
    }

    [Fact]
    public void CollidingKeysWrapAroundAndKeepDistinctFinalImages()
    {
        var p = new Memory(32768); var arena = APTR.FromPointer(64); var source = APTR.FromPointer(30000);
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, 16384, 512));
        var mask = p.ReadUInt32(arena, 16);
        // Intentionally drive all entries into the last bucket: exercise both
        // collision resolution and wraparound instead of relying on random luck.
        var keys = Enumerable.Range(0, 100000).Select(i => (uint)i)
            .Where(b => ((b ^ (b >> 9) ^ (b >> 18)) & mask) == mask).Take(25).ToArray();
        for (var i = 0; i < keys.Length; i++)
        {
            p.WriteUInt32(source, 0, (uint)i);
            Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, keys[i], source));
        }
        for (var i = keys.Length - 1; i >= 0; i--)
        {
            p.WriteUInt32(source, 0, keys[i] ^ 0xAA55AA55);
            Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, keys[i], source));
        }
        Assert.Equal((uint)keys.Length, p.ReadUInt32(arena));
        foreach (var key in keys)
            Assert.Equal(key ^ 0xAA55AA55, p.ReadUInt32(AmigaSmartFileSystemRedoArena.Find(ref p, arena, key)));
        Assert.True(AmigaSmartFileSystemRedoArena.Find(ref p, arena, uint.MaxValue).IsNull);
    }

    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(32768)]
    public void OwnedAllocationExtentSurvivesResetAndIndexSizeTransitions(int blockBytes)
    {
        const uint maximum = 192 * 1024;
        var budgets = new HashSet<uint> { maximum, (uint)(152 + 16 * (blockBytes + 8)) };
        // Just above each 75%-full index boundary, removing the unused tail
        // can select a smaller index. The allocation descriptor must still
        // describe exactly the span that Exec will later free.
        foreach (var slots in new uint[] { 1, 2, 3, 6, 12, 24, 48, 96, 192, 377 })
            foreach (var extra in new uint[] { 0, 4, 8, 36, 128, 512, 1024 })
                budgets.Add(24 + slots * ((uint)blockBytes + 8) + extra);
        foreach (var budget in budgets.Where(b => b >= blockBytes + 40 && b <= maximum))
        {
            var bytes = AmigaSmartFileSystemRedoArena.FitBytes(budget, (uint)blockBytes);
            Assert.InRange(bytes, (uint)blockBytes + 40, budget);
            var arena = APTR.FromPointer(64);
            var source = APTR.FromPointer(arena.Raw + bytes + 64);
            var p = new Memory((int)source.Raw + blockBytes);
            for (var reset = 0; reset < 2; reset++)
            {
                Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, bytes, (uint)blockBytes));
                Assert.Equal(bytes, AmigaSmartFileSystemRedoArena.StorageBytes(ref p, arena));
                var capacity = p.ReadUInt32(arena, 4);
                for (var i = 0u; i < capacity; i++)
                    Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, i, source));
                Assert.Equal(arena.Raw + bytes,
                    AmigaSmartFileSystemRedoArena.Find(ref p, arena, capacity - 1).Raw + (uint)blockBytes);
                Assert.False(AmigaSmartFileSystemRedoArena.Store(ref p, arena, uint.MaxValue, source));
                Assert.Equal(bytes, AmigaSmartFileSystemRedoArena.StorageBytes(ref p, arena));
                Assert.All(p.Bytes.AsSpan(0, 64).ToArray(), b => Assert.Equal(0xA5, b));
                Assert.All(p.Bytes.AsSpan((int)(arena.Raw + bytes), 64).ToArray(), b => Assert.Equal(0xA5, b));
            }
        }
    }

    [Fact]
    public void ConsecutiveBlockLookupWorkDoesNotGrowWithStagedCount()
    {
        var p = new Memory(210000); var arena = APTR.FromPointer(64); var source = APTR.FromPointer(200000);
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, 192 * 1024, 512));
        for (var i = 0u; i < 370; i++) Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, i, source));
        p.Reads = 0;
        for (var i = 0u; i < 370; i++) Assert.True(AmigaSmartFileSystemRedoArena.Find(ref p, arena, i).IsNotNull);
        // A linear scan takes more than 68,000 key reads here. This is an
        // algorithmic regression budget, independent of host timing noise.
        Assert.InRange(p.Reads, 370, 370 * 8);
    }

    [Fact]
    public void TinyArenaAlwaysRetainsAnEmptyBucketForAbsentKeys()
    {
        var p = new Memory(4096); var arena = APTR.FromPointer(64); var source = APTR.FromPointer(3000);
        // Exercise both sides of the one/two-slot sizing transition.
        foreach (var size in new uint[] { 552, 1071, 1072, 1080, 1600 })
        {
            Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, size, 512));
            var capacity = p.ReadUInt32(arena, 4);
            Assert.True(capacity < p.ReadUInt32(arena, 16) + 1);
            for (var i = 0u; i < capacity; i++) Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, i, source));
            Assert.True(AmigaSmartFileSystemRedoArena.Find(ref p, arena, uint.MaxValue).IsNull);
            Assert.False(AmigaSmartFileSystemRedoArena.Store(ref p, arena, uint.MaxValue, source));
        }
    }

    [Fact]
    public void ExistingSixteenByteOverlayKeepsItsLayoutAndCapacity()
    {
        var p = new Memory(4096); var arena = APTR.FromPointer(64); var source = APTR.FromPointer(3000);
        p.Clear(arena, 16); p.WriteUInt32(arena, 4, 2); p.WriteUInt32(arena, 8, 512);
        p.WriteUInt32(source, 0, 0x12345678);
        Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, 0, source));
        Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, uint.MaxValue, source));
        Assert.False(AmigaSmartFileSystemRedoArena.Store(ref p, arena, 99, source));
        Assert.Equal(arena.Raw + 20, AmigaSmartFileSystemRedoArena.Find(ref p, arena, 0).Raw);
        Assert.Equal(arena.Raw + 536, AmigaSmartFileSystemRedoArena.Find(ref p, arena, uint.MaxValue).Raw);
        p.WriteUInt32(source, 0, 0x87654321);
        Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, 0, source));
        Assert.Equal(0x87654321u, p.ReadUInt32(AmigaSmartFileSystemRedoArena.Find(ref p, arena, 0)));
        Assert.Equal(0x12345678u, p.ReadUInt32(AmigaSmartFileSystemRedoArena.Find(ref p, arena, uint.MaxValue)));
        Assert.Equal(2u, p.ReadUInt32(arena));
        Assert.Equal(1u, p.ReadUInt32(arena, 12));
    }

    [Fact]
    public void InvalidOrTooSmallArenaDoesNotTouchMemory()
    {
        var p = new Memory(1024); var before = p.Bytes.ToArray(); var arena = APTR.FromPointer(64);
        Assert.False(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, 551, 512));
        Assert.False(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, 1000, 512));
        Assert.Equal(before, p.Bytes);
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, 552, 512));
        Assert.Equal(1u, p.ReadUInt32(arena, 4));
    }
    [Fact]
    public void SlotVerificationInvalidatesOnEditsStoresAndResetWithoutEvictingOtherSlots()
    {
        var p = new Memory(32768); var arena = APTR.FromPointer(64); var source = APTR.FromPointer(30000);
        const uint id = 0x424E4443;
        void Seed(uint block)
        {
            p.Clear(source, 512); p.WriteUInt32(source, 0, id); p.WriteUInt32(source, 8, block);
            p.WriteUInt32(source, 4, unchecked(uint.MaxValue - id - block));
            Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, block, source));
        }
        APTR View(uint block, bool edit = false, uint type = id)
        {
            var image = AmigaSmartFileSystemRedoArena.MetadataView(ref p, arena, block, type, edit, out var found);
            Assert.True(found); return image;
        }
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, 16384, 512));
        Seed(3); p.Reads = 0;
        var first = View(3); Assert.True(first.IsNotNull); Assert.True(p.Reads >= 128);
        p.Reads = 0; Assert.Equal(first, View(3)); Assert.InRange(p.Reads, 1, 20);
        Assert.True(View(3, type: 0x4F424A43).IsNull);
        Assert.Equal(first, View(3, edit: true));
        p.WriteUInt32(first, 12, 1);
        Assert.True(View(3).IsNull); // Editable view revoked the cached checksum.
        Seed(3); Assert.True(View(3).IsNotNull);
        p.WriteUInt32(first, 12, 1);
        Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, 3, first));
        Assert.True(View(3).IsNull); // A self-store also revokes verification.
        Seed(3); Seed(11); Assert.True(View(3).IsNotNull); Assert.True(View(11).IsNotNull);
        p.Reads = 0; Assert.True(View(3).IsNotNull); Assert.InRange(p.Reads, 1, 20);
        Assert.True(AmigaSmartFileSystemRedoArena.MetadataView(ref p, arena, 99, id, false, out var missing).IsNull);
        Assert.False(missing);
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, arena, 16384, 512));
        p.Clear(source, 512);
        Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, arena, 3, source));
        Assert.True(View(3).IsNull);
        Assert.All(p.Bytes.AsSpan(0, 64).ToArray(), b => Assert.Equal(0xA5, b));
        Assert.All(p.Bytes.AsSpan(16448, 64).ToArray(), b => Assert.Equal(0xA5, b));
    }
    [Theory]
    [InlineData(512)]
    [InlineData(1024)]
    [InlineData(4096)]
    [InlineData(32768)]
    public void ClonePreservesCollidingImagesVerificationAndExactOwnedExtentInBothDirections(int blockBytes)
    {
        var bytes = AmigaSmartFileSystemRedoArena.FitBytes(192 * 1024, (uint)blockBytes);
        var first = APTR.FromPointer(64);
        var second = APTR.FromPointer(first.Raw + bytes + 64);
        var input = APTR.FromPointer(second.Raw + bytes + 64);
        var p = new Memory((int)input.Raw + blockBytes);
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, first, bytes, (uint)blockBytes));
        var capacity = p.ReadUInt32(first, 4);
        var mask = p.ReadUInt32(first, 16);
        var keys = Enumerable.Range(0, 1000000).Select(i => 0x80000000u + (uint)i)
            .Where(key => ((key ^ (key >> 9) ^ (key >> 18)) & mask) == mask).Take((int)capacity).ToArray();
        Assert.Equal((int)capacity, keys.Length);
        const uint id = 0x424e4443;
        for (var i = 0; i < keys.Length; i++) {
            p.Clear(input, (uint)blockBytes); p.WriteUInt32(input, 0, id); p.WriteUInt32(input, 8, keys[i]);
            p.WriteUInt32(input, 12, (uint)i); p.WriteUInt32(input, 4, unchecked(uint.MaxValue - id - keys[i] - (uint)i));
            Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, first, keys[i], input));
            Assert.True(AmigaSmartFileSystemRedoArena.MetadataView(ref p, first, keys[i], id, false, out var found).IsNotNull && found);
        }
        Assert.False(AmigaSmartFileSystemRedoArena.Store(ref p, first, uint.MaxValue, input));
        var original = p.Bytes.AsSpan((int)first.Raw, (int)bytes).ToArray();
        Assert.True(AmigaSmartFileSystemRedoArena.Clone(ref p, first, second, bytes));
        Assert.Equal(original, p.Bytes.AsSpan((int)first.Raw, (int)bytes).ToArray());
        Assert.Equal(0u, p.ReadUInt32(second, 12));
        Assert.Equal(bytes, AmigaSmartFileSystemRedoArena.StorageBytes(ref p, second));
        foreach (var key in keys) {
            var a = AmigaSmartFileSystemRedoArena.Find(ref p, first, key);
            var b = AmigaSmartFileSystemRedoArena.Find(ref p, second, key);
            Assert.Equal(a.Raw - first.Raw, b.Raw - second.Raw);
            Assert.Equal(p.Bytes.AsSpan((int)a.Raw, blockBytes).ToArray(), p.Bytes.AsSpan((int)b.Raw, blockBytes).ToArray());
        }
        p.Reads = 0;
        var edited = AmigaSmartFileSystemRedoArena.MetadataView(ref p, second, keys[0], id, true, out var exists);
        Assert.True(exists && edited.IsNotNull); Assert.InRange(p.Reads, 1, 20);
        p.WriteUInt32(edited, 12, 123);
        Assert.True(AmigaSmartFileSystemRedoArena.MetadataView(ref p, second, keys[0], id, false, out _).IsNull);
        Assert.True(AmigaSmartFileSystemRedoArena.MetadataView(ref p, first, keys[0], id, false, out _).IsNotNull);
        p.WriteUInt32(edited, 4, unchecked(uint.MaxValue - id - keys[0] - 123));
        Assert.True(AmigaSmartFileSystemRedoArena.Store(ref p, second, keys[0], edited));
        Assert.True(AmigaSmartFileSystemRedoArena.MetadataView(ref p, second, keys[0], id, false, out _).IsNotNull);
        var accepted = p.Bytes.AsSpan((int)second.Raw, (int)bytes).ToArray();
        Assert.True(AmigaSmartFileSystemRedoArena.Clone(ref p, second, first, bytes));
        Assert.Equal(accepted, p.Bytes.AsSpan((int)second.Raw, (int)bytes).ToArray());
        Assert.Equal(123u, p.ReadUInt32(AmigaSmartFileSystemRedoArena.Find(ref p, first, keys[0]), 12));
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, second, bytes, (uint)blockBytes));
        Assert.True(AmigaSmartFileSystemRedoArena.Clone(ref p, second, first, bytes));
        Assert.True(AmigaSmartFileSystemRedoArena.Find(ref p, first, keys[0]).IsNull);
        Assert.Equal(bytes, AmigaSmartFileSystemRedoArena.StorageBytes(ref p, first));
        foreach (var offset in new uint[] { 0, first.Raw + bytes, second.Raw + bytes })
            Assert.All(p.Bytes.AsSpan((int)offset, 64).ToArray(), b => Assert.Equal(0xa5, b));
    }

    [Fact]
    public void CloneRejectsOverlapInsufficientSpaceAndInvalidOwnedHeadersBeforeWriting()
    {
        var p = new Memory(65536); var first = APTR.FromPointer(64); var second = APTR.FromPointer(32768);
        var bytes = AmigaSmartFileSystemRedoArena.FitBytes(16384, 512);
        Assert.True(AmigaSmartFileSystemRedoArena.Initialize(ref p, first, bytes, 512));
        void Reject(APTR destination, uint available) {
            var before = p.Bytes.ToArray();
            Assert.False(AmigaSmartFileSystemRedoArena.Clone(ref p, first, destination, available));
            Assert.Equal(before, p.Bytes);
        }
        Reject(first, bytes); Reject(APTR.FromPointer(first.Raw + 4), bytes); Reject(second, bytes - 1);
        Reject(APTR.FromPointer(65500), bytes);
        var capacity = p.ReadUInt32(first, 4);
        p.WriteUInt32(first, 0, capacity + 1); Reject(second, bytes); p.WriteUInt32(first, 0, 0);
        var slotBase = p.ReadUInt32(first, 20);
        p.WriteUInt32(first, 20, slotBase + 4); Reject(second, bytes); p.WriteUInt32(first, 20, slotBase);
        p.WriteUInt32(first, 8, 512); Reject(second, bytes);
    }

}
