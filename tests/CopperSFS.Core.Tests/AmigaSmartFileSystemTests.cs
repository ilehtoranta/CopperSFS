using System.Buffers.Binary;
using System.Text;
using Amiga;
using CopperSFS;

namespace CopperSFS.Tests;

public sealed class AmigaSmartFileSystemTests
{
    private const int Blocks = 128, Bytes = 512;
    private const uint Root = 0x53465300, Objects = 0x4F424A43, Nodes = 0x4E444320, Tree = 0x424E4443, TransactionOk = 0x54524F4B;
    private const uint Bitmap = 0x42544D50, Admin = 0x41444D43;
    private sealed class PowerCut : Exception;

    private struct Disk(byte[] image) : IAmigaWritableFileSystemPlatform
    {
        public readonly byte[] Ram = new byte[8192];
        public int Writes, FailAtWrite, IoEvents, CutAfterIo;
        public bool Buffered;
        private Dictionary<uint, byte[]>? pending;
        public AmigaVolumeGeometry Geometry { get { AmigaVolumeGeometry.Create(Blocks, Bytes, 1, out var geometry); return geometry; } }
        public bool ReadBlock(uint block, APTR destination)
        {
            if (block >= Blocks || !IsMapped(destination, Bytes)) return false;
            if (Buffered && pending is not null && pending.TryGetValue(block, out var volatileBlock))
                volatileBlock.CopyTo(Ram, (int)destination.Raw);
            else image.AsSpan((int)block * Bytes, Bytes).CopyTo(Ram.AsSpan((int)destination.Raw, Bytes));
            return true;
        }
        public bool WriteProtected() => false;
        public bool Flush()
        {
            if (Buffered && pending is not null)
            {
                foreach (var (block, bytes) in pending)
                    bytes.CopyTo(image, (int)block * Bytes);
                pending.Clear();
            }
            IoEvents++;
            if (CutAfterIo != 0 && IoEvents == CutAfterIo) throw new PowerCut();
            return true;
        }
        public bool WriteBlock(uint block, APTR source)
        {
            Writes++;
            if (FailAtWrite != 0 && Writes >= FailAtWrite) return false;
            if (block < Geometry.ReservedBlocks || block >= Blocks || !IsMapped(source, Bytes)) return false;
            if (Buffered) (pending ??= new())[block] = Ram.AsSpan((int)source.Raw, Bytes).ToArray();
            else Ram.AsSpan((int)source.Raw, Bytes).CopyTo(image.AsSpan((int)block * Bytes, Bytes));
            IoEvents++;
            if (CutAfterIo != 0 && IoEvents == CutAfterIo) throw new PowerCut();
            return true;
        }
        public byte ReadUInt8(APTR address, int offset = 0) => Ram[address.Raw + offset];
        public ushort ReadUInt16(APTR address, int offset = 0) => BinaryPrimitives.ReadUInt16BigEndian(Ram.AsSpan((int)address.Raw + offset));
        public uint ReadUInt32(APTR address, int offset = 0) => BinaryPrimitives.ReadUInt32BigEndian(Ram.AsSpan((int)address.Raw + offset));
        public void WriteUInt8(APTR address, int offset, byte value) => Ram[address.Raw + offset] = value;
        public void WriteUInt16(APTR address, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Ram.AsSpan((int)address.Raw + offset), value);
        public void WriteUInt32(APTR address, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Ram.AsSpan((int)address.Raw + offset), value);
        public bool IsMapped(APTR address, uint length) => address.Raw <= Ram.Length && length <= Ram.Length - address.Raw;
        public void Clear(APTR address, uint length) => Ram.AsSpan((int)address.Raw, (int)length).Clear();
        public void Copy(APTR source, APTR destination, uint length) => Ram.AsSpan((int)source.Raw, (int)length).CopyTo(Ram.AsSpan((int)destination.Raw));
        public APTR Name(string value) { Encoding.Latin1.GetBytes(value).CopyTo(Ram, 3000); return APTR.FromPointer(3000); }
        public byte[] Snapshot() => image.ToArray();
        public IEnumerable<byte[]> CrashImages()
        {
            var volatileBlocks = pending?.ToArray() ?? [];
            if (volatileBlocks.Length > 12) throw new InvalidOperationException("Unexpectedly large unflushed write set");
            for (var subset = 0; subset < 1 << volatileBlocks.Length; subset++)
            {
                var saved = Snapshot();
                for (var index = 0; index < volatileBlocks.Length; index++)
                    if ((subset & (1 << index)) != 0)
                        volatileBlocks[index].Value.CopyTo(saved, (int)volatileBlocks[index].Key * Bytes);
                yield return saved;
            }
        }
    }

    private const uint LargeDataBlock = 8_388_608; // 4 GiB / 512 bytes
    private const uint LargeBlocks = LargeDataBlock + 4096;
    private const uint LargeBitmapBase = 128;

    private struct SparseDisk(Dictionary<uint, byte[]> blocks) : IAmigaWritableFileSystemPlatform
    {
        public readonly byte[] Ram = new byte[8192];
        public uint CutAfterBlock;
        public AmigaVolumeGeometry Geometry
        {
            get { AmigaVolumeGeometry.Create(LargeBlocks, Bytes, 1, out var geometry); return geometry; }
        }
        public bool ReadBlock(uint block, APTR destination)
        {
            if (block >= LargeBlocks || !IsMapped(destination, Bytes)) return false;
            var target = Ram.AsSpan((int)destination.Raw, Bytes);
            if (blocks.TryGetValue(block, out var source)) source.CopyTo(target);
            else target.Clear();
            return true;
        }
        public bool WriteBlock(uint block, APTR source)
        {
            if (block < Geometry.ReservedBlocks || block >= LargeBlocks - 1 || !IsMapped(source, Bytes)) return false;
            blocks[block] = Ram.AsSpan((int)source.Raw, Bytes).ToArray();
            if (block == CutAfterBlock) throw new PowerCut();
            return true;
        }
        public bool HasBlock(uint block) => blocks.ContainsKey(block);
        public SparseDisk Restart() => new(blocks);
        public bool WriteProtected() => false;
        public bool Flush() => true;
        public byte ReadUInt8(APTR address, int offset = 0) => Ram[address.Raw + offset];
        public ushort ReadUInt16(APTR address, int offset = 0) => BinaryPrimitives.ReadUInt16BigEndian(Ram.AsSpan((int)address.Raw + offset));
        public uint ReadUInt32(APTR address, int offset = 0) => BinaryPrimitives.ReadUInt32BigEndian(Ram.AsSpan((int)address.Raw + offset));
        public void WriteUInt8(APTR address, int offset, byte value) => Ram[address.Raw + offset] = value;
        public void WriteUInt16(APTR address, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Ram.AsSpan((int)address.Raw + offset), value);
        public void WriteUInt32(APTR address, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Ram.AsSpan((int)address.Raw + offset), value);
        public bool IsMapped(APTR address, uint length) => address.Raw <= Ram.Length && length <= Ram.Length - address.Raw;
        public void Clear(APTR address, uint length) => Ram.AsSpan((int)address.Raw, (int)length).Clear();
        public void Copy(APTR source, APTR destination, uint length) => Ram.AsSpan((int)source.Raw, (int)length).CopyTo(Ram.AsSpan((int)destination.Raw));
    }

    private static void U16(Span<byte> block, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(block[offset..], value);
    private static void U32(Span<byte> block, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(block[offset..], value);
    private static void Sum(Span<byte> block)
    {
        U32(block, 4, 0);
        uint sum = 0;
        for (var i = 0; i < block.Length; i += 4) sum = unchecked(sum + BinaryPrimitives.ReadUInt32BigEndian(block[i..]));
        U32(block, 4, unchecked(0u - sum - 1));
    }
    private static Span<byte> Block(byte[] image, int number) => image.AsSpan(number * Bytes, Bytes);
    private static void Header(byte[] image, int number, uint id)
    {
        var block = Block(image, number); U32(block, 0, id); U32(block, 8, (uint)number);
    }
    private static int Object(Span<byte> block, int at, uint node, uint value, uint size, byte bits, string name, string comment = "")
    {
        U32(block, at + 4, node); U32(block, at + 8, 0xF); U32(block, at + 12, value); U32(block, at + 16, size);
        U32(block, at + 20, 86461); block[at + 24] = bits;
        var nameBytes = Encoding.Latin1.GetBytes(name); nameBytes.CopyTo(block[(at + 25)..]);
        var commentBytes = Encoding.Latin1.GetBytes(comment); commentBytes.CopyTo(block[(at + 26 + nameBytes.Length)..]);
        return (at + 27 + nameBytes.Length + commentBytes.Length + 1) & ~1;
    }

    internal static byte[] Image()
    {
        var image = new byte[Blocks * Bytes];
        foreach (var number in new[] { 0, Blocks - 1 })
        {
            Header(image, number, Root);
            var b = Block(image, number); U16(b, 12, 3); U16(b, 14, (ushort)(number == 0 ? 1 : 2));
            U32(b, 44, Blocks * Bytes); U32(b, 48, Blocks); U32(b, 52, Bytes);
            U32(b, 96, 9); U32(b, 100, 10); U32(b, 104, 2); U32(b, 108, 5); U32(b, 112, 6);
            Sum(b);
        }
        Header(image, 2, Objects);
        var root = Block(image, 2);
        Object(root, 24, 1, 0, 7, 128, "SFSVOL");
        U32(root, Bytes - 28, 82); Sum(root);

        Header(image, 9, Bitmap);
        var bitmap = Block(image, 9);
        for (var at = 12; at < Bytes; at += 4) U32(bitmap, at, uint.MaxValue);
        foreach (var used in new[] { 0, 2, 4, 5, 6, 7, 8, 9, 10, 20, 21, 23, 24, 127 })
        {
            var at = 12 + used / 32 * 4;
            U32(bitmap, at, BinaryPrimitives.ReadUInt32BigEndian(bitmap[at..]) & ~(1u << (31 - used % 32)));
        }
        for (var used = 64; used < 96; used++)
        {
            var at = 12 + used / 32 * 4;
            U32(bitmap, at, BinaryPrimitives.ReadUInt32BigEndian(bitmap[at..]) & ~(1u << (31 - used % 32)));
        }
        Sum(bitmap);
        Header(image, 10, Admin);
        var admin = Block(image, 10); U32(admin, 24, 64); Sum(admin);

        Header(image, 4, TransactionOk); Sum(Block(image, 4));

        Header(image, 6, Nodes);
        var nodes = Block(image, 6); U32(nodes, 12, 1); U32(nodes, 16, 1);
        U32(nodes, 20, 2); U32(nodes, 30, 7); U32(nodes, 40, 7); U32(nodes, 50, 8); Sum(nodes);

        Header(image, 5, Tree);
        var tree = Block(image, 5); U16(tree, 12, 3); tree[14] = 1; tree[15] = 14;
        U32(tree, 16, 20); U32(tree, 20, 23); U16(tree, 28, 2);
        U32(tree, 30, 23); U32(tree, 38, 20); U16(tree, 42, 1);
        U32(tree, 44, 24); U16(tree, 56, 1); Sum(tree);

        Header(image, 7, Objects);
        var contents = Block(image, 7); U32(contents, 12, 1);
        var next = Object(contents, 24, 2, 0, 8, 128, "Docs");
        Object(contents, next, 3, 20, 1300, 0, "Hello", "greeting"); Sum(contents);

        Header(image, 8, Objects);
        var sub = Block(image, 8); U32(sub, 12, 2);
        Object(sub, 24, 4, 24, 5, 0, "Nested"); Sum(sub);
        for (var i = 0; i < 1300; i++) image[(i < 1024 ? 20 : 23) * Bytes + i % 1024] = (byte)(i * 17);
        Encoding.ASCII.GetBytes("hello").CopyTo(image, 24 * Bytes);
        return image;
    }

    private static SparseDisk LargeSparseVolume()
    {
        var source = Image();
        var blocks = new Dictionary<uint, byte[]>();
        for (var block = 0; block < Blocks; block++)
            blocks[(uint)block] = Block(source, block).ToArray();

        // Nested is a five-byte file whose single extent starts at 4 GiB.
        // An append must allocate its next data block above that boundary.
        U32(blocks[8], 24 + 12, LargeDataBlock); Sum(blocks[8]);
        U32(blocks[5], 44, LargeDataBlock); Sum(blocks[5]);
        blocks[LargeDataBlock] = Block(source, 24).ToArray();

        const uint bitsPerMap = (Bytes - 12) * 8;
        var mapCount = (LargeBlocks + bitsPerMap - 1) / bitsPerMap;
        var used = new HashSet<uint>
        { 0, 2, 4, 5, 6, 7, 8, 9, 10, 20, 21, 23, 24, 127,
          LargeDataBlock, LargeBlocks - 1 };
        for (var block = 64u; block < 96; block++) used.Add(block);
        for (var index = 0u; index < mapCount; index++)
        {
            var number = LargeBitmapBase + index;
            used.Add(number);
            var map = new byte[Bytes];
            U32(map, 0, Bitmap); U32(map, 8, number);
            for (var at = 12; at < Bytes; at += 4) U32(map, at, uint.MaxValue);
            blocks[number] = map;
        }
        void MarkUsed(uint number)
        {
            var map = blocks[LargeBitmapBase + number / bitsPerMap];
            var index = number % bitsPerMap;
            var at = 12 + (int)(index / 32) * 4;
            U32(map, at, BinaryPrimitives.ReadUInt32BigEndian(map.AsSpan(at)) &
                ~(1u << (int)(31 - index % 32)));
        }
        foreach (var block in used) MarkUsed(block);
        for (var block = LargeBlocks; block < mapCount * bitsPerMap; block++) MarkUsed(block);
        for (var index = 0u; index < mapCount; index++) Sum(blocks[LargeBitmapBase + index]);

        var root = blocks[0];
        var lastByte = (ulong)LargeBlocks * Bytes;
        U32(root, 40, (uint)(lastByte >> 32)); U32(root, 44, (uint)lastByte);
        U32(root, 48, LargeBlocks); U32(root, 96, LargeBitmapBase); Sum(root);
        var alternate = root.ToArray();
        U32(alternate, 8, LargeBlocks - 1); U16(alternate, 14, 2); Sum(alternate);
        blocks[LargeBlocks - 1] = alternate;
        U32(blocks[2], Bytes - 28, LargeBlocks - (uint)used.Count); Sum(blocks[2]);
        return new SparseDisk(blocks);
    }

    private static AmigaSmartFileSystemWriteCore.Work LargeWork() => new()
    {
        Meta = APTR.FromPointer(512), Data = APTR.FromPointer(1024),
        Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048),
    };

    [Fact]
    public void SparseSfsVolumeBeyondFourGiBAppendsIntoUpperBlocksAndRemounts()
    {
        var disk = LargeSparseVolume();
        Assert.True(AmigaSmartFileSystemCore.Mount(ref disk, APTR.FromPointer(512)));
        for (var index = 0; index < 508; index++) disk.Ram[4000 + index] = (byte)(index * 37 + 11);
        var work = LargeWork();
        var append = new AmigaSmartFileSystemWriteCore.Request
        { Node = 4, Position = 5, Buffer = APTR.FromPointer(4000), Length = 508 };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref append, out var actual), work.Error.ToString());
        Assert.Equal(508u, actual);
        Assert.True(disk.HasBlock(LargeDataBlock + 1));

        var remounted = disk.Restart();
        Assert.True(AmigaSmartFileSystemCore.Mount(ref remounted, APTR.FromPointer(512)));
        Assert.Equal(513, AmigaSmartFileSystemCore.Read(ref remounted, 4, 0,
            APTR.FromPointer(4000), 513, APTR.FromPointer(512), APTR.FromPointer(1024), out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal("hello", Encoding.ASCII.GetString(remounted.Ram, 4000, 5));
        for (var index = 0; index < 508; index++)
            Assert.Equal((byte)(index * 37 + 11), remounted.Ram[4005 + index]);
    }

    [Fact]
    public void SparseSfsUpperBlockPowerCutRecoversBeforeRetry()
    {
        var disk = LargeSparseVolume();
        disk.CutAfterBlock = LargeDataBlock + 1;
        for (var index = 0; index < 508; index++) disk.Ram[4000 + index] = (byte)(index * 19 + 3);
        var work = LargeWork();
        var append = new AmigaSmartFileSystemWriteCore.Request
        { Node = 4, Position = 5, Buffer = APTR.FromPointer(4000), Length = 508 };
        Assert.Throws<PowerCut>(() => AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref append, out _));
        var recovered = disk.Restart();
        work = LargeWork();
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work), work.Error.ToString());
        Assert.True(AmigaSmartFileSystemCore.Mount(ref recovered, APTR.FromPointer(512)));
        Assert.Equal(5, AmigaSmartFileSystemCore.Read(ref recovered, 4, 0,
            APTR.FromPointer(4000), 5, APTR.FromPointer(512), APTR.FromPointer(1024), out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal("hello", Encoding.ASCII.GetString(recovered.Ram, 4000, 5));

        for (var index = 0; index < 508; index++) recovered.Ram[4000 + index] = (byte)(index * 19 + 3);
        work = LargeWork();
        append = new AmigaSmartFileSystemWriteCore.Request
        { Node = 4, Position = 5, Buffer = APTR.FromPointer(4000), Length = 508 };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref recovered, ref work, ref append, out var actual), work.Error.ToString());
        Assert.Equal(508u, actual);
        Assert.Equal(513, AmigaSmartFileSystemCore.Read(ref recovered, 4, 0,
            APTR.FromPointer(4000), 513, APTR.FromPointer(512), APTR.FromPointer(1024), out error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal("hello", Encoding.ASCII.GetString(recovered.Ram, 4000, 5));
        for (var index = 0; index < 508; index++)
            Assert.Equal((byte)(index * 19 + 3), recovered.Ram[4005 + index]);
    }

    internal static byte[] BootImage(string commandName, byte[] command)
    {
        var p = new Disk(Image());
        AmigaSmartFileSystemWriteCore.Work Work() => new()
        {
            Meta = APTR.FromPointer(512), Data = APTR.FromPointer(1024),
            Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048),
        };
        void Add(string path, byte[] bytes, bool directory)
        {
            var work = Work();
            var create = new AmigaSmartFileSystemWriteCore.Request
            { Directory = 1, Name = p.Name(path), Length = (uint)path.Length };
            Assert.True(AmigaSmartFileSystemWriteCore.Create(ref p, ref work, ref create, directory));
            if (directory) return;
            var position = 0;
            while (position < bytes.Length)
            {
                var count = Math.Min(bytes.Length - position, p.Ram.Length - 4000);
                bytes.AsSpan(position, count).CopyTo(p.Ram.AsSpan(4000, count));
                work = Work();
                var write = new AmigaSmartFileSystemWriteCore.Request
                { Node = create.Node, Position = (uint)position, Buffer = APTR.FromPointer(4000), Length = (uint)count };
                Assert.True(AmigaSmartFileSystemWriteCore.Write(ref p, ref work, ref write, out var actual));
                Assert.Equal((uint)count, actual);
                position += count;
            }
        }
        Add("C", [], true);
        Add("S", [], true);
        Add("C/" + commandName, command, false);
        Add("S/Startup-Sequence", Encoding.ASCII.GetBytes(commandName + "\n"), false);
        return p.Snapshot();
    }

    [Fact]
    public void MountResolveEnumerateAndReadFragmentedFile()
    {
        var p = new Disk(Image()); var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
        Assert.Equal((byte)'S', p.ReadUInt8(meta, 49));
        Assert.True(AmigaSmartFileSystemCore.CountUsed(ref p, meta, out var used)); Assert.Equal(44u, used);
        Assert.True(AmigaSmartFileSystemCore.Describe(ref p, 1, meta, out _, out var rootDirectory, out _));
        Assert.True(rootDirectory);
        Assert.True(p.ReadBlock(7, meta));
        Assert.Equal(Objects, p.ReadUInt32(meta)); Assert.Equal(1u, p.ReadUInt32(meta, 12));
        var file = AmigaSmartFileSystemCore.Resolve(ref p, 1, p.Name("hELLo"), 5, meta, out var error);
        Assert.Equal(DOS.Error.None, error); Assert.Equal(3u, file);
        Assert.True(AmigaSmartFileSystemCore.Describe(ref p, file, meta, out var size, out var directory, out var parent));
        Assert.Equal(1300u, size); Assert.False(directory); Assert.Equal(1u, parent);
        Assert.Equal(1300, AmigaSmartFileSystemCore.Read(ref p, file, 0, APTR.FromPointer(4000), 1300, meta, data, out error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(Enumerable.Range(0, 1300).Select(i => (byte)(i * 17)), p.Ram.AsSpan(4000, 1300).ToArray());
        Assert.True(AmigaSmartFileSystemCore.Examine(ref p, file, APTR.FromPointer(2000), meta));
        Assert.Equal(1300u, p.ReadUInt32(APTR.FromPointer(2000), 124));
        Assert.Equal(0u, p.ReadUInt32(APTR.FromPointer(2000), 116));
        Assert.True(AmigaSmartFileSystemCore.Examine(ref p, 1, APTR.FromPointer(2000), meta));
        Assert.Equal(7u, p.ReadUInt32(APTR.FromPointer(2000), 124));
        Assert.Equal(1u, p.ReadUInt32(APTR.FromPointer(2000), 128));
        Assert.Equal(DOS.Error.None, AmigaSmartFileSystemCore.ExamineNext(ref p, 1, APTR.FromPointer(2000), meta, data));
        Assert.Equal(0xFFFFFFFDu, p.ReadUInt32(APTR.FromPointer(2000), 4));
        Assert.Equal(3u, p.ReadUInt32(APTR.FromPointer(2000), 0));
        Assert.Equal(DOS.Error.None, AmigaSmartFileSystemCore.ExamineNext(ref p, 1, APTR.FromPointer(2000), meta, data));
        Assert.Equal(2u, p.ReadUInt32(APTR.FromPointer(2000), 4));
        Assert.Equal(1u, p.ReadUInt32(APTR.FromPointer(2000), 128));
        Assert.Equal(DOS.Error.NoMoreEntries, AmigaSmartFileSystemCore.ExamineNext(ref p, 1, APTR.FromPointer(2000), meta, data));
        Assert.Equal(4u, AmigaSmartFileSystemCore.Resolve(ref p, 1, p.Name("docs/NESTED"), 11, meta, out error));
        Assert.Equal(5, AmigaSmartFileSystemCore.Read(ref p, 4, 0, APTR.FromPointer(4000), 5, meta, data, out error));
        Assert.Equal("hello", Encoding.ASCII.GetString(p.Ram, 4000, 5));
    }

    [Fact]
    public void CorruptPrimaryRootCanUseSecondaryButBadMetadataFails()
    {
        var image = Image(); image[0] ^= 1;
        var p = new Disk(image); var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
        image[7 * Bytes + 40] ^= 1;
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref p, 1, p.Name("Hello"), 5, meta, out var error));
        Assert.Equal(DOS.Error.DiskNotValidated, error);
        Assert.Equal(-1, AmigaSmartFileSystemCore.Read(ref p, 3, 0, APTR.FromPointer(4000), 10, meta, data, out error));
    }

    [Fact]
    public void CaseSensitiveVolumeKeepsDistinctLookupSemantics()
    {
        var image = Image();
        foreach (var number in new[] { 0, Blocks - 1 })
        {
            var root = Block(image, number);
            root[20] = 128;
            Sum(root);
        }
        var p = new Disk(image); var meta = APTR.FromPointer(512);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref p, 1, p.Name("hello"), 5, meta, out var error));
        Assert.Equal(DOS.Error.ObjectNotFound, error);
        Assert.Equal(3u, AmigaSmartFileSystemCore.Resolve(ref p, 1, p.Name("Hello"), 5, meta, out error));
        Assert.Equal(DOS.Error.None, error);
    }

    [Fact]
    public void UnfinishedTransactionIsNotMountedAsClean()
    {
        var image = Image();
        var marker = Block(image, 4);
        U32(marker, 0, 0x54524641); // TRFA: original SFS must replay this transaction.
        Sum(marker);
        var p = new Disk(image);
        Assert.False(AmigaSmartFileSystemCore.Mount(ref p, APTR.FromPointer(512)));
    }

    [Fact]
    public void CaseInsensitiveLookupFoldsLatin1LikeOriginalSfs()
    {
        var image = Image();
        var entries = Block(image, 7);
        entries[56 + 25] = 0xE4; // ä; the original handler folds it to Ä (0xC4).
        Sum(entries);
        var p = new Disk(image); var meta = APTR.FromPointer(512);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
        Assert.Equal(3u, AmigaSmartFileSystemCore.Resolve(ref p, 1, p.Name("Äello"), 5, meta, out var error));
        Assert.Equal(DOS.Error.None, error);
    }

    [Fact]
    public void CreateWriteAndRemountFile()
    {
        var p = new Disk(Image());
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024); var bitmap = APTR.FromPointer(1536);
        var work = new AmigaSmartFileSystemWriteCore.Work { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        var create = new AmigaSmartFileSystemWriteCore.Request { Directory = 1, Name = p.Name("Docs/new.bin"), Length = 12 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref p, ref work, ref create, false));
        Assert.Equal(5u, create.Node);
        for (var i = 0; i < 1500; i++) p.Ram[4000 + i] = (byte)(i % 239);
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        var write = new AmigaSmartFileSystemWriteCore.Request
        { Node = create.Node, Buffer = APTR.FromPointer(4000), Length = 1500 };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref p, ref work, ref write, out var actual));
        Assert.Equal(1500u, actual);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
        Assert.Equal(5u, AmigaSmartFileSystemCore.Resolve(ref p, 1, p.Name("Docs/new.bin"), 12, meta, out var error));
        Assert.Equal(1500, AmigaSmartFileSystemCore.Read(ref p, create.Node, 0, APTR.FromPointer(4000), 1500,
            meta, data, out error));
        Assert.Equal(Enumerable.Range(0, 1500).Select(i => (byte)(i % 239)), p.Ram.AsSpan(4000, 1500).ToArray());
        Assert.True(AmigaSmartFileSystemCore.CountUsed(ref p, meta, out var used));
        Assert.Equal(47u, used);
    }

    [Fact]
    public void CreateAcceptsVolumeQualifiedRootFilename()
    {
        var p = new Disk(Image());
        var meta = APTR.FromPointer(512);
        var work = new AmigaSmartFileSystemWriteCore.Work
        { Meta = meta, Data = APTR.FromPointer(1024), Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048) };
        const string path = "SFS0:NativeWrite";
        var create = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = p.Name(path), Length = (uint)path.Length };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref p, ref work, ref create, false));
        Assert.Equal(5u, create.Node);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
        Assert.Equal(create.Node, AmigaSmartFileSystemCore.Resolve(ref p, 1,
            p.Name("NativeWrite"), 11, meta, out var error));
        Assert.Equal(DOS.Error.None, error);
    }

    [Fact]
    public void AppendUsesNewExtentWhenNextPhysicalBlockIsOccupied()
    {
        var p = new Disk(Image());
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024); var bitmap = APTR.FromPointer(1536);
        for (var i = 0; i < 300; i++) p.Ram[4000 + i] = (byte)(255 - i % 251);
        var work = new AmigaSmartFileSystemWriteCore.Work { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        var write = new AmigaSmartFileSystemWriteCore.Request
        { Node = 3, Position = 1300, Buffer = APTR.FromPointer(4000), Length = 300 };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref p, ref work, ref write, out var actual));
        Assert.Equal(300u, actual);
        Assert.True(p.ReadBlock(5, meta));
        Assert.NotEqual(0u, p.ReadUInt32(meta, 48)); // key 23 links to the new extent
        Assert.NotEqual(24u, p.ReadUInt32(meta, 48)); // block 24 belongs to another file
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
        Assert.Equal(1600, AmigaSmartFileSystemCore.Read(ref p, 3, 0, APTR.FromPointer(4000), 1600,
            meta, data, out var error));
        Assert.Equal(Enumerable.Range(0, 1300).Select(i => (byte)(i * 17)).Concat(
            Enumerable.Range(0, 300).Select(i => (byte)(255 - i % 251))), p.Ram.AsSpan(4000, 1600).ToArray());
    }

    [Fact]
    public void TruncateAndDeleteReleaseDataAndObjectNode()
    {
        var p = new Disk(Image());
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024); var bitmap = APTR.FromPointer(1536);
        var work = new AmigaSmartFileSystemWriteCore.Work { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        Assert.True(AmigaSmartFileSystemWriteCore.Truncate(ref p, ref work, 3));
        Assert.True(AmigaSmartFileSystemCore.Describe(ref p, 3, meta, out var size, out _, out _));
        Assert.Equal(0u, size);
        Assert.True(AmigaSmartFileSystemCore.CountUsed(ref p, meta, out var used));
        Assert.Equal(41u, used);
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref p, ref work, 3));
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref p, 1, p.Name("Hello"), 5, meta, out var error));
        Assert.Equal(DOS.Error.ObjectNotFound, error);
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        Assert.False(AmigaSmartFileSystemWriteCore.Remove(ref p, ref work, 2));
        Assert.Equal(DOS.Error.DirectoryNotEmpty, work.Error);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
    }

    [Fact]
    public void CreateAndRemoveEmptyDirectory()
    {
        var p = new Disk(Image());
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024); var bitmap = APTR.FromPointer(1536);
        var work = new AmigaSmartFileSystemWriteCore.Work { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        var create = new AmigaSmartFileSystemWriteCore.Request { Directory = 1, Name = p.Name("Fresh"), Length = 5 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref p, ref work, ref create, true));
        Assert.True(AmigaSmartFileSystemCore.Describe(ref p, create.Node, meta, out _, out var directory, out _));
        Assert.True(directory);
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref p, ref work, create.Node));
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref p, 1, p.Name("Fresh"), 5, meta, out var error));
        Assert.Equal(DOS.Error.ObjectNotFound, error);
    }

    [Fact]
    public void ProtectionCommentAndDatePersistInFileInfo()
    {
        var p = new Disk(Image());
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024); var bitmap = APTR.FromPointer(1536);
        var work = new AmigaSmartFileSystemWriteCore.Work { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = 3, Buffer = APTR.FromPointer(4000), Length = 12 };
        Encoding.Latin1.GetBytes("new greeting").CopyTo(p.Ram, 4000);
        Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref p, ref work, ref request, DosPacketAction.SetComment));
        p.WriteUInt32(APTR.FromPointer(4000), 0, 10);
        p.WriteUInt32(APTR.FromPointer(4000), 4, 123);
        p.WriteUInt32(APTR.FromPointer(4000), 8, 100);
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref p, ref work, ref request, DosPacketAction.SetDate));
        request.Position = 4;
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref p, ref work, ref request, DosPacketAction.SetProtection));
        var fib = APTR.FromPointer(2000);
        Assert.True(AmigaSmartFileSystemCore.Examine(ref p, 3, fib, meta));
        Assert.Equal(4u, p.ReadUInt32(fib, 116));
        Assert.Equal(10u, p.ReadUInt32(fib, 132));
        Assert.Equal(123u, p.ReadUInt32(fib, 136));
        Assert.Equal(100u, p.ReadUInt32(fib, 140));
        Assert.Equal(12u, p.ReadUInt8(fib, 144));
        Assert.Equal("new greeting", Encoding.Latin1.GetString(p.Ram, (int)fib.Raw + 145, 12));
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        var write = new AmigaSmartFileSystemWriteCore.Request { Node = 3, Buffer = APTR.FromPointer(4000), Length = 1 };
        Assert.False(AmigaSmartFileSystemWriteCore.Write(ref p, ref work, ref write, out _));
        Assert.Equal(DOS.Error.WriteProtected, work.Error);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
    }

    [Fact]
    public void RenameAcrossDirectoriesPreservesObjectAndComment()
    {
        var p = new Disk(Image());
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024); var bitmap = APTR.FromPointer(1536);
        var work = new AmigaSmartFileSystemWriteCore.Work { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        var rename = new AmigaSmartFileSystemWriteCore.Request
        { Node = 3, Directory = 1, Name = p.Name("Docs/Renamed"), Length = 12 };
        Assert.True(AmigaSmartFileSystemWriteCore.Rename(ref p, ref work, ref rename));
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref p, 1, p.Name("Hello"), 5, meta, out var error));
        Assert.Equal(DOS.Error.ObjectNotFound, error);
        Assert.Equal(3u, AmigaSmartFileSystemCore.Resolve(ref p, 1, p.Name("Docs/Renamed"), 12, meta, out error));
        Assert.True(AmigaSmartFileSystemCore.Describe(ref p, 3, meta, out var size, out _, out var parent));
        Assert.Equal(1300u, size); Assert.Equal(2u, parent);
        var fib = APTR.FromPointer(2000);
        Assert.True(AmigaSmartFileSystemCore.Examine(ref p, 3, fib, meta));
        Assert.Equal("greeting", Encoding.Latin1.GetString(p.Ram, (int)fib.Raw + 145, 8));
        Assert.Equal(1300, AmigaSmartFileSystemCore.Read(ref p, 3, 0, APTR.FromPointer(4000), 1300,
            meta, data, out error));
        Assert.Equal(Enumerable.Range(0, 1300).Select(i => (byte)(i * 17)), p.Ram.AsSpan(4000, 1300).ToArray());
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        rename = new() { Node = 2, Directory = 1, Name = p.Name("Docs/Inner"), Length = 10 };
        Assert.False(AmigaSmartFileSystemWriteCore.Rename(ref p, ref work, ref rename));
        Assert.Equal(DOS.Error.ObjectInUse, work.Error);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
    }

    [Fact]
    public void CreationGrowsFullObjectNodeRoot()
    {
        var image = Image();
        var nodes = Block(image, 6);
        for (var at = 60; at + 10 <= Bytes; at += 10) U32(nodes, at, 7);
        Sum(nodes);
        var p = new Disk(image);
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024); var bitmap = APTR.FromPointer(1536);
        var work = new AmigaSmartFileSystemWriteCore.Work { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        var create = new AmigaSmartFileSystemWriteCore.Request { Directory = 1, Name = p.Name("Extra"), Length = 5 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref p, ref work, ref create, false));
        Assert.Equal(50u, create.Node);
        Assert.Equal(50u, AmigaSmartFileSystemCore.Resolve(ref p, 1, p.Name("Extra"), 5, meta, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
    }

    [Fact]
    public void DirectoryCreationExpandsExhaustedAdminSpace()
    {
        var image = Image();
        var admin = Block(image, 10);
        U32(admin, 28, uint.MaxValue); Sum(admin);
        var p = new Disk(image);
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024); var bitmap = APTR.FromPointer(1536);
        var work = new AmigaSmartFileSystemWriteCore.Work { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        var create = new AmigaSmartFileSystemWriteCore.Request { Directory = 1, Name = p.Name("Fresh"), Length = 5 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref p, ref work, ref create, true));
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        var child = new AmigaSmartFileSystemWriteCore.Request { Directory = create.Node, Name = p.Name("Child"), Length = 5 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref p, ref work, ref child, false));
        Assert.Equal(child.Node, AmigaSmartFileSystemCore.Resolve(ref p, 1, p.Name("Fresh/Child"), 11, meta, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.True(AmigaSmartFileSystemCore.CountUsed(ref p, meta, out var used));
        Assert.Equal(76u, used);
    }

    [Fact]
    public void FragmentedAppendSplitsFullExtentRootAndRemainsReadable()
    {
        var image = Image();
        var tree = Block(image, 5);
        U16(tree, 12, 35);
        for (var i = 0; i < 32; i++)
        {
            var at = 16 + (i + 3) * 14;
            U32(tree, at, (uint)(25 + i)); U16(tree, at + 12, 1);
            var bitmap = Block(image, 9); var block = 25 + i;
            var offset = 12 + block / 32 * 4;
            U32(bitmap, offset, BinaryPrimitives.ReadUInt32BigEndian(bitmap[offset..]) & ~(1u << (31 - block % 32)));
        }
        Sum(tree); Sum(Block(image, 9));
        U32(Block(image, 2), Bytes - 28, 50); Sum(Block(image, 2));
        var p = new Disk(image);
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024); var bitmapScratch = APTR.FromPointer(1536);
        var work = new AmigaSmartFileSystemWriteCore.Work { Meta = meta, Data = data, Bitmap = bitmapScratch, Journal = APTR.FromPointer(2048) };
        for (var i = 0; i < 300; i++) p.Ram[4000 + i] = (byte)(i ^ 0xA5);
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = 3, Position = 1300, Buffer = APTR.FromPointer(4000), Length = 300 };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref p, ref work, ref request, out var actual));
        Assert.Equal(300u, actual);
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
        Assert.True(p.ReadBlock(5, meta));
        Assert.Equal((byte)0, p.ReadUInt8(meta, 14));
        Assert.Equal(1600, AmigaSmartFileSystemCore.Read(ref p, 3, 0, APTR.FromPointer(4000), 1600,
            meta, data, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(Enumerable.Range(0, 300).Select(i => (byte)(i ^ 0xA5)), p.Ram.AsSpan(5300, 300).ToArray());
        work = new() { Meta = meta, Data = data, Bitmap = bitmapScratch, Journal = APTR.FromPointer(2048) };
        Assert.True(AmigaSmartFileSystemWriteCore.Truncate(ref p, ref work, 3));
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
    }

    [Fact]
    public void ResizeShrinksExtentsAndZeroFillsGrowth()
    {
        var p = new Disk(Image());
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024); var bitmap = APTR.FromPointer(1536);
        var work = new AmigaSmartFileSystemWriteCore.Work { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        Assert.True(AmigaSmartFileSystemWriteCore.Resize(ref p, ref work, 3, 700));
        Assert.True(AmigaSmartFileSystemCore.Describe(ref p, 3, meta, out var size, out _, out _));
        Assert.Equal(700u, size);
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        Assert.True(AmigaSmartFileSystemWriteCore.Resize(ref p, ref work, 3, 1700));
        Assert.Equal(1700, AmigaSmartFileSystemCore.Read(ref p, 3, 0, APTR.FromPointer(4000), 1700,
            meta, data, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(Enumerable.Range(0, 700).Select(i => (byte)(i * 17)), p.Ram.AsSpan(4000, 700).ToArray());
        Assert.All(p.Ram.AsSpan(4700, 1000).ToArray(), value => Assert.Equal((byte)0, value));
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        Assert.True(AmigaSmartFileSystemWriteCore.Resize(ref p, ref work, 3, 0));
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        Assert.True(AmigaSmartFileSystemWriteCore.Resize(ref p, ref work, 3, 32));
        Assert.Equal(32, AmigaSmartFileSystemCore.Read(ref p, 3, 0, APTR.FromPointer(4000), 32,
            meta, data, out error));
        Assert.All(p.Ram.AsSpan(4000, 32).ToArray(), value => Assert.Equal((byte)0, value));
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
    }

    [Fact]
    public void RemovingLastExtentInIndexedLeafReleasesLeaf()
    {
        var image = Image();
        var root = Block(image, 5);
        root.Clear(); Header(image, 5, Tree);
        U16(root, 12, 2); root[15] = 8;
        U32(root, 16, 0); U32(root, 20, 64);
        U32(root, 24, 24); U32(root, 28, 65); Sum(root);
        Header(image, 64, Tree);
        var left = Block(image, 64); U16(left, 12, 2); left[14] = 1; left[15] = 14;
        U32(left, 16, 20); U32(left, 20, 23); U16(left, 28, 2);
        U32(left, 30, 23); U32(left, 38, 20); U16(left, 42, 1); Sum(left);
        Header(image, 65, Tree);
        var right = Block(image, 65); U16(right, 12, 1); right[14] = 1; right[15] = 14;
        U32(right, 16, 24); U16(right, 28, 1); Sum(right);
        U32(Block(image, 10), 28, 0xC0000000); Sum(Block(image, 10));
        var p = new Disk(image);
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024); var bitmap = APTR.FromPointer(1536);
        var work = new AmigaSmartFileSystemWriteCore.Work { Meta = meta, Data = data, Bitmap = bitmap, Journal = APTR.FromPointer(2048) };
        Assert.True(AmigaSmartFileSystemWriteCore.Truncate(ref p, ref work, 4));
        Assert.True(p.ReadBlock(5, meta));
        Assert.Equal((ushort)1, p.ReadUInt16(meta, 12));
        Assert.True(p.ReadBlock(10, meta));
        Assert.Equal(0x80000000u, p.ReadUInt32(meta, 28));
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
        Assert.Equal(1300, AmigaSmartFileSystemCore.Read(ref p, 3, 0, APTR.FromPointer(4000), 1300,
            meta, data, out var error));
        Assert.Equal(DOS.Error.None, error);
    }

    [Fact]
    public void InterruptedCreateRollsBackAtEveryBlockWrite()
    {
        const string name = "Interrupted";
        var probe = new Disk(Image());
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024);
        var bitmap = APTR.FromPointer(1536); var journal = APTR.FromPointer(2048);
        var work = new AmigaSmartFileSystemWriteCore.Work
        { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = probe.Name(name), Length = (uint)name.Length };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref probe, ref work, ref request, false));
        Assert.True(probe.Writes > 5);

        for (var stop = 1; stop <= probe.Writes; stop++)
        {
            var image = Image();
            var p = new Disk(image) { FailAtWrite = stop };
            work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
            request = new() { Directory = 1, Name = p.Name(name), Length = (uint)name.Length };
            Assert.False(AmigaSmartFileSystemWriteCore.Create(ref p, ref work, ref request, false));
            var restarted = new Disk(image);
            work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref restarted, ref work));
            Assert.True(AmigaSmartFileSystemCore.Mount(ref restarted, meta));
            Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref restarted, 1,
                restarted.Name(name), (uint)name.Length, meta, out var error));
            Assert.Equal(DOS.Error.ObjectNotFound, error);
            Assert.True(AmigaSmartFileSystemCore.CountUsed(ref restarted, meta, out var used));
            Assert.Equal(44u, used);
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref restarted, ref work));
        }
    }

    [Fact]
    public void InterruptedOverwriteRestoresOriginalFileData()
    {
        var probe = new Disk(Image());
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024);
        var bitmap = APTR.FromPointer(1536); var journal = APTR.FromPointer(2048);
        for (var i = 0; i < 64; i++) probe.Ram[4000 + i] = 0xA5;
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = 3, Buffer = APTR.FromPointer(4000), Length = 64 };
        var work = new AmigaSmartFileSystemWriteCore.Work
        { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref probe, ref work, ref request, out _));
        for (var stop = 1; stop <= probe.Writes; stop++)
        {
            var image = Image();
            var p = new Disk(image) { FailAtWrite = stop };
            for (var i = 0; i < 64; i++) p.Ram[4000 + i] = 0xA5;
            request = new() { Node = 3, Buffer = APTR.FromPointer(4000), Length = 64 };
            work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
            Assert.False(AmigaSmartFileSystemWriteCore.Write(ref p, ref work, ref request, out _));
            var restarted = new Disk(image);
            work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref restarted, ref work));
            Assert.True(AmigaSmartFileSystemCore.Mount(ref restarted, meta));
            Assert.Equal(64, AmigaSmartFileSystemCore.Read(ref restarted, 3, 0,
                APTR.FromPointer(4000), 64, meta, data, out var error));
            Assert.Equal(DOS.Error.None, error);
            Assert.Equal(Enumerable.Range(0, 64).Select(i => (byte)(i * 17)),
                restarted.Ram.AsSpan(4000, 64).ToArray());
        }
    }

    [Fact]
    public void RecoveryCanResumeAfterItsOwnWriteFailure()
    {
        var image = Image();
        var p = new Disk(image) { FailAtWrite = 5 };
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024);
        var bitmap = APTR.FromPointer(1536); var journal = APTR.FromPointer(2048);
        var work = new AmigaSmartFileSystemWriteCore.Work
        { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
        var name = p.Name("Interrupted");
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = name, Length = 11 };
        Assert.False(AmigaSmartFileSystemWriteCore.Create(ref p, ref work, ref request, false));
        var firstRecovery = new Disk(image) { FailAtWrite = 2 };
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
        Assert.False(AmigaSmartFileSystemWriteCore.Recover(ref firstRecovery, ref work));
        var secondRecovery = new Disk(image);
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref secondRecovery, ref work));
        Assert.True(AmigaSmartFileSystemCore.Mount(ref secondRecovery, meta));
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref secondRecovery, 1,
            secondRecovery.Name("Interrupted"), 11, meta, out var error));
        Assert.Equal(DOS.Error.ObjectNotFound, error);
    }

    [Fact]
    public void NextWriteRecoversInterruptedTransactionBeforeProceeding()
    {
        var image = Image();
        var failed = new Disk(image) { FailAtWrite = 5 };
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024);
        var bitmap = APTR.FromPointer(1536); var journal = APTR.FromPointer(2048);
        var work = new AmigaSmartFileSystemWriteCore.Work
        { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = failed.Name("Interrupted"), Length = 11 };
        Assert.False(AmigaSmartFileSystemWriteCore.Create(ref failed, ref work, ref request, false));

        var restarted = new Disk(image);
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
        request = new() { Directory = 1, Name = restarted.Name("Completed"), Length = 9 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref restarted, ref work, ref request, false));
        Assert.True(AmigaSmartFileSystemCore.Mount(ref restarted, meta));
        Assert.Equal(0u, AmigaSmartFileSystemCore.Resolve(ref restarted, 1,
            restarted.Name("Interrupted"), 11, meta, out var missingError));
        Assert.Equal(DOS.Error.ObjectNotFound, missingError);
        Assert.NotEqual(0u, AmigaSmartFileSystemCore.Resolve(ref restarted, 1,
            restarted.Name("Completed"), 9, meta, out var completedError));
        Assert.Equal(DOS.Error.None, completedError);
    }

    [Fact]
    public void ReplaysOriginalSfsTrstRedoRecord()
    {
        var image = Image();
        var log = Block(image, 11);
        Header(image, 11, 0x54525354); // TRST
        U16(log, 16, 7); U32(log, 18, 20); log[22] = 0;
        log[23] = 0xC0; // one immediate long, followed by 127 unchanged longs
        log[24] = (byte)'T'; log[25] = (byte)'E'; log[26] = (byte)'S'; log[27] = (byte)'T';
        log[28] = 0x7F; log[29] = 0x7E;
        Sum(log);
        var marker = Block(image, 4);
        U32(marker, 0, 0x54524641); U32(marker, 12, 11); Sum(marker);
        var p = new Disk(image);
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024);
        var work = new AmigaSmartFileSystemWriteCore.Work
        { Meta = meta, Data = data, Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048) };
        Assert.False(AmigaSmartFileSystemCore.Mount(ref p, meta));
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref p, ref work));
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
        Assert.Equal(4, AmigaSmartFileSystemCore.Read(ref p, 3, 0,
            APTR.FromPointer(4000), 4, meta, data, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal("TEST", Encoding.ASCII.GetString(p.Ram, 4000, 4));
    }

    [Fact]
    public void ReplaysOriginalSfsRecordAcrossTrstBlocks()
    {
        var image = Image();
        var operation = new byte[522];
        U16(operation, 0, 514); U32(operation, 2, 20);
        operation[7] = 0xFF; operation[264] = 0xFF;
        for (var i = 0; i < 256; i++)
        {
            operation[8 + i] = (byte)(i ^ 0x5A);
            operation[265 + i] = (byte)(i ^ 0xA5);
        }
        Header(image, 11, 0x54525354);
        Header(image, 12, 0x54525354);
        var first = Block(image, 11); var second = Block(image, 12);
        U32(first, 12, 12);
        operation.AsSpan(0, 496).CopyTo(first[16..]);
        operation.AsSpan(496).CopyTo(second[16..]);
        Sum(first); Sum(second);
        var marker = Block(image, 4);
        U32(marker, 0, 0x54524641); U32(marker, 12, 11); Sum(marker);
        var p = new Disk(image);
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024);
        var work = new AmigaSmartFileSystemWriteCore.Work
        { Meta = meta, Data = data, Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048) };
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref p, ref work));
        Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
        Assert.Equal(512, AmigaSmartFileSystemCore.Read(ref p, 3, 0,
            APTR.FromPointer(4000), 512, meta, data, out var error));
        Assert.Equal(DOS.Error.None, error);
        Assert.Equal(operation.AsSpan(8, 256).ToArray(), p.Ram.AsSpan(4000, 256).ToArray());
        Assert.Equal(operation.AsSpan(265, 256).ToArray(), p.Ram.AsSpan(4256, 256).ToArray());
    }

    [Fact]
    public void RejectsMalformedOriginalJournalBeforeChangingData()
    {
        var image = Image();
        Header(image, 11, 0x54525354);
        var log = Block(image, 11);
        U16(log, 16, 7); U32(log, 18, 20);
        log[23] = 0xC0; log[24] = 1; log[28] = 0x7F; log[29] = 0x7E;
        U16(log, 30, 1); U32(log, 32, 20); log[37] = 0x40; // only 64 of 128 longs
        Sum(log);
        var marker = Block(image, 4);
        U32(marker, 0, 0x54524641); U32(marker, 12, 11); Sum(marker);
        var original = Block(image, 20).ToArray();
        var p = new Disk(image);
        var work = new AmigaSmartFileSystemWriteCore.Work
        { Meta = APTR.FromPointer(512), Data = APTR.FromPointer(1024),
          Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048) };
        Assert.False(AmigaSmartFileSystemWriteCore.Recover(ref p, ref work));
        Assert.Equal(0, p.Writes);
        Assert.Equal(original, Block(image, 20).ToArray());
    }

    [Fact]
    public void InterruptedTruncatePreservesFreedFileData()
    {
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024);
        var bitmap = APTR.FromPointer(1536); var journal = APTR.FromPointer(2048);
        var probe = new Disk(Image());
        var work = new AmigaSmartFileSystemWriteCore.Work
        { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
        Assert.True(AmigaSmartFileSystemWriteCore.Truncate(ref probe, ref work, 3));
        for (var stop = 1; stop <= probe.Writes; stop++)
        {
            var image = Image();
            var p = new Disk(image) { FailAtWrite = stop };
            work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
            Assert.False(AmigaSmartFileSystemWriteCore.Truncate(ref p, ref work, 3));
            var restarted = new Disk(image);
            work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref restarted, ref work));
            Assert.True(AmigaSmartFileSystemCore.Mount(ref restarted, meta));
            Assert.Equal(1300, AmigaSmartFileSystemCore.Read(ref restarted, 3, 0,
                APTR.FromPointer(4000), 1300, meta, data, out var error));
            Assert.Equal(DOS.Error.None, error);
            Assert.Equal(Enumerable.Range(0, 1300).Select(i => (byte)(i * 17)),
                restarted.Ram.AsSpan(4000, 1300).ToArray());
        }
    }

    [Fact]
    public void CorruptUndoImageKeepsVolumeDirty()
    {
        var image = Image();
        var p = new Disk(image) { FailAtWrite = 5 };
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024);
        var bitmap = APTR.FromPointer(1536); var journal = APTR.FromPointer(2048);
        var work = new AmigaSmartFileSystemWriteCore.Work
        { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = p.Name("Interrupted"), Length = 11 };
        Assert.False(AmigaSmartFileSystemWriteCore.Create(ref p, ref work, ref request, false));
        var head = BinaryPrimitives.ReadUInt32BigEndian(Block(image, 4)[12..]);
        var saved = BinaryPrimitives.ReadUInt32BigEndian(Block(image, (int)head)[20..]);
        Block(image, (int)saved)[40] ^= 0x5A;
        var restarted = new Disk(image);
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
        Assert.False(AmigaSmartFileSystemWriteCore.Recover(ref restarted, ref work));
        Assert.Equal(0x54524641u, BinaryPrimitives.ReadUInt32BigEndian(Block(image, 4)));
    }

    [Fact]
    public void InterleavedFileWritesAndReadsKeepEachCommittedStreamIndependent()
    {
        var p = new Disk(Image());
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024);
        var work = new AmigaSmartFileSystemWriteCore.Work
        { Meta = meta, Data = data, Bitmap = APTR.FromPointer(1536), Journal = APTR.FromPointer(2048) };
        var left = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = p.Name("Left"), Length = 4 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref p, ref work, ref left, false));
        var right = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = p.Name("Right"), Length = 5 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref p, ref work, ref right, false));

        var leftExpected = new List<byte>(); var rightExpected = new List<byte>();
        uint leftPosition = 0, rightPosition = 0;
        for (var turn = 0; turn < 8; turn++)
        {
            var leftChunk = Enumerable.Range(0, 97 + turn * 11).Select(i => (byte)(i * 13 + turn)).ToArray();
            leftChunk.CopyTo(p.Ram, 4000);
            var leftWrite = new AmigaSmartFileSystemWriteCore.Request
            { Node = left.Node, Position = leftPosition, Buffer = APTR.FromPointer(4000), Length = (uint)leftChunk.Length };
            Assert.True(AmigaSmartFileSystemWriteCore.Write(ref p, ref work, ref leftWrite, out var leftActual));
            Assert.Equal((uint)leftChunk.Length, leftActual);
            leftPosition += leftActual; leftExpected.AddRange(leftChunk);
            AssertFile(left.Node, leftExpected);
            AssertFile(right.Node, rightExpected);

            var rightChunk = Enumerable.Range(0, 83 + turn * 17).Select(i => (byte)(i * 7 + turn * 3)).ToArray();
            rightChunk.CopyTo(p.Ram, 4000);
            var rightWrite = new AmigaSmartFileSystemWriteCore.Request
            { Node = right.Node, Position = rightPosition, Buffer = APTR.FromPointer(4000), Length = (uint)rightChunk.Length };
            Assert.True(AmigaSmartFileSystemWriteCore.Write(ref p, ref work, ref rightWrite, out var rightActual));
            Assert.Equal((uint)rightChunk.Length, rightActual);
            rightPosition += rightActual; rightExpected.AddRange(rightChunk);
            AssertFile(left.Node, leftExpected);
            AssertFile(right.Node, rightExpected);
        }

        void AssertFile(uint node, List<byte> expected)
        {
            Assert.True(AmigaSmartFileSystemCore.Mount(ref p, meta));
            Assert.Equal(expected.Count, AmigaSmartFileSystemCore.Read(ref p, node, 0,
                APTR.FromPointer(5000), (uint)expected.Count, meta, data, out var error));
            Assert.Equal(DOS.Error.None, error);
            Assert.Equal(expected.ToArray(), p.Ram.AsSpan(5000, expected.Count).ToArray());
        }
    }

    [Fact]
    public void InterruptedWriterPreservesOtherCommittedFileAndCanBeRetried()
    {
        var seeded = new Disk(Image());
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024);
        var bitmap = APTR.FromPointer(1536); var journal = APTR.FromPointer(2048);
        var work = new AmigaSmartFileSystemWriteCore.Work
        { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
        var left = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = seeded.Name("Left"), Length = 4 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref seeded, ref work, ref left, false));
        var right = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = seeded.Name("Right"), Length = 5 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref seeded, ref work, ref right, false));
        var leftOriginal = Enumerable.Range(0, 701).Select(i => (byte)(i * 11)).ToArray();
        var rightOriginal = Enumerable.Range(0, 799).Select(i => (byte)(i * 19)).ToArray();
        leftOriginal.CopyTo(seeded.Ram, 4000);
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Node = left.Node, Buffer = APTR.FromPointer(4000), Length = (uint)leftOriginal.Length };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref seeded, ref work, ref request, out _));
        rightOriginal.CopyTo(seeded.Ram, 4000);
        request = new() { Node = right.Node, Buffer = APTR.FromPointer(4000), Length = (uint)rightOriginal.Length };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref seeded, ref work, ref request, out _));

        var committed = seeded.Snapshot();
        var replacement = Enumerable.Range(0, 733).Select(i => (byte)(255 - i * 3)).ToArray();
        var probe = new Disk(committed.ToArray());
        replacement.CopyTo(probe.Ram, 4000);
        request = new() { Node = left.Node, Position = 0, Buffer = APTR.FromPointer(4000), Length = (uint)replacement.Length };
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref probe, ref work, ref request, out _));

        for (var stop = 1; stop <= probe.Writes; stop++)
        {
            var image = committed.ToArray();
            var failed = new Disk(image) { FailAtWrite = stop };
            replacement.CopyTo(failed.Ram, 4000);
            request = new() { Node = left.Node, Buffer = APTR.FromPointer(4000), Length = (uint)replacement.Length };
            work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
            Assert.False(AmigaSmartFileSystemWriteCore.Write(ref failed, ref work, ref request, out _));

            var recovered = new Disk(image);
            work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
            Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work));
            Assert.True(AmigaSmartFileSystemCore.Mount(ref recovered, meta));
            Assert.Equal(leftOriginal.Length, AmigaSmartFileSystemCore.Read(ref recovered, left.Node,
                0, APTR.FromPointer(5000), (uint)leftOriginal.Length, meta, data, out var leftError));
            Assert.Equal(DOS.Error.None, leftError);
            Assert.Equal(leftOriginal, recovered.Ram.AsSpan(5000, leftOriginal.Length).ToArray());
            Assert.Equal(rightOriginal.Length, AmigaSmartFileSystemCore.Read(ref recovered, right.Node,
                0, APTR.FromPointer(5000), (uint)rightOriginal.Length, meta, data, out var rightError));
            Assert.Equal(DOS.Error.None, rightError);
            Assert.Equal(rightOriginal, recovered.Ram.AsSpan(5000, rightOriginal.Length).ToArray());

            replacement.CopyTo(recovered.Ram, 4000);
            request = new() { Node = left.Node, Buffer = APTR.FromPointer(4000), Length = (uint)replacement.Length };
            work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
            Assert.True(AmigaSmartFileSystemWriteCore.Write(ref recovered, ref work, ref request, out _));
            Assert.True(AmigaSmartFileSystemCore.Mount(ref recovered, meta));
            Assert.Equal(replacement.Length, AmigaSmartFileSystemCore.Read(ref recovered, left.Node,
                0, APTR.FromPointer(5000), (uint)replacement.Length, meta, data, out leftError));
            Assert.Equal(DOS.Error.None, leftError);
            Assert.Equal(replacement, recovered.Ram.AsSpan(5000, replacement.Length).ToArray());
        }
    }

    [Fact]
    public void PowerCutAfterEveryWriteOrFlushLeavesOnlyCommittedFileVersions()
    {
        var seeded = new Disk(Image());
        var meta = APTR.FromPointer(512); var data = APTR.FromPointer(1024);
        var bitmap = APTR.FromPointer(1536); var journal = APTR.FromPointer(2048);
        var work = new AmigaSmartFileSystemWriteCore.Work
        { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
        var other = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = seeded.Name("Other"), Length = 5 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref seeded, ref work, ref other, false));
        var otherBytes = Enumerable.Range(0, 613).Select(i => (byte)(i * 23)).ToArray();
        otherBytes.CopyTo(seeded.Ram, 4000);
        var write = new AmigaSmartFileSystemWriteCore.Request
        { Node = other.Node, Buffer = APTR.FromPointer(4000), Length = (uint)otherBytes.Length };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref seeded, ref work, ref write, out _));
        var committed = seeded.Snapshot();
        var original = Enumerable.Range(0, 1300).Select(i => (byte)(i * 17)).ToArray();
        var replacement = Enumerable.Range(0, 1300).Select(i => (byte)(255 - i * 29)).ToArray();

        var probe = new Disk(committed.ToArray()) { Buffered = true };
        replacement.CopyTo(probe.Ram, 4000);
        work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
        write = new() { Node = 3, Buffer = APTR.FromPointer(4000), Length = (uint)replacement.Length };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref probe, ref work, ref write, out _));
        Assert.True(probe.IoEvents > probe.Writes);

        for (var cut = 1; cut <= probe.IoEvents; cut++)
        {
            var interrupted = new Disk(committed.ToArray()) { Buffered = true, CutAfterIo = cut };
            replacement.CopyTo(interrupted.Ram, 4000);
            work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
            write = new() { Node = 3, Buffer = APTR.FromPointer(4000), Length = (uint)replacement.Length };
            Assert.Throws<PowerCut>(() => AmigaSmartFileSystemWriteCore.Write(ref interrupted, ref work, ref write, out _));

            foreach (var saved in interrupted.CrashImages())
            {
                var recovered = new Disk(saved);
                work = new() { Meta = meta, Data = data, Bitmap = bitmap, Journal = journal };
                Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work));
                Assert.True(AmigaSmartFileSystemCore.Mount(ref recovered, meta));
                Assert.Equal(1300, AmigaSmartFileSystemCore.Read(ref recovered, 3, 0,
                    APTR.FromPointer(5000), 1300, meta, data, out var error));
                Assert.Equal(DOS.Error.None, error);
                var actual = recovered.Ram.AsSpan(5000, 1300).ToArray();
                Assert.True(actual.SequenceEqual(original) || actual.SequenceEqual(replacement), $"Cut after I/O {cut}");
                Assert.Equal(otherBytes.Length, AmigaSmartFileSystemCore.Read(ref recovered, other.Node, 0,
                    APTR.FromPointer(5000), (uint)otherBytes.Length, meta, data, out error));
                Assert.Equal(DOS.Error.None, error);
                Assert.Equal(otherBytes, recovered.Ram.AsSpan(5000, otherBytes.Length).ToArray());
                Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref recovered, ref work));
            }
        }
    }
}
