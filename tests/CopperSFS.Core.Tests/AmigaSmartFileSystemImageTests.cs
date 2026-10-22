using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Amiga;
using CopperSFS;
using Xunit.Abstractions;

namespace CopperSFS.Tests;

public sealed class SfsImageTheoryAttribute : TheoryAttribute
{
    public SfsImageTheoryAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("COPPERSFS_IMAGE_MANIFEST")))
            Skip = "Set COPPERSFS_IMAGE_MANIFEST to an independent SFS image manifest outside Git.";
    }
}

/// <summary>Opt-in host semantic media tests. Each case owns a disposable image copy.</summary>
public sealed class AmigaSmartFileSystemImageTests(ITestOutputHelper output)
{
    private static readonly APTR Meta = APTR.FromPointer(4096), Data = APTR.FromPointer(36864),
        Fib = APTR.FromPointer(1024), Transfer = APTR.FromPointer(131073);

    public sealed record Entry(string Path, string Type, bool Hidden, uint Size, string? Sha256,
        uint Protection, string Comment, uint Seconds);
    public sealed record Manifest(string ImagePath, string ImageSha256, string Partition,
        ulong StartByte, uint BlockBytes, uint BlockCount, uint ReservedBlocks, Entry[] Entries);

    [SfsImageTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ExternalImageFullReadAndMutationRemount(bool buffered)
    {
        var manifest = JsonSerializer.Deserialize<Manifest>(File.ReadAllText(
            Environment.GetEnvironmentVariable("COPPERSFS_IMAGE_MANIFEST")!))!;
        using (var source = File.OpenRead(manifest.ImagePath))
            Assert.Equal(manifest.ImageSha256, Convert.ToHexString(SHA256.HashData(source)), ignoreCase: true);
        var directory = Directory.CreateTempSubdirectory("CopperSfsExternal-").FullName;
        var path = System.IO.Path.Combine(directory, buffered ? "buffered.hdf" : "strict.hdf");
        output.WriteLine($"Disposable image: {path}");
        File.Copy(manifest.ImagePath, path);
        try
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                var disk = new ImageDisk(stream, manifest);
                Assert.True(AmigaSmartFileSystemCore.Mount(ref disk, Meta), "Mount failed");
                VerifyCorpus(ref disk, manifest, cached: buffered);
                Mutate(ref disk, manifest, buffered);
            }
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read))
            {
                var disk = new ImageDisk(stream, manifest);
                Assert.True(AmigaSmartFileSystemCore.Mount(ref disk, Meta), "Fresh mount failed");
                VerifyCorpus(ref disk, manifest, extra: true, cached: buffered);
                var node = Resolve(ref disk, 1, "CopperSfsImageTest/renamed.bin");
                var expected = Payload();
                AssertFile(ref disk, node, expected);
                Assert.True(AmigaSmartFileSystemCore.Examine(ref disk, node, Fib, Meta));
                Assert.Equal("External image regression", disk.BString(Fib, 144));
                Assert.Equal(128u, disk.ReadUInt32(Fib, 116));
                Assert.Equal(400u, disk.ReadUInt32(Fib, 132));
                Assert.Equal(13u, disk.ReadUInt32(Fib, 136));
                Assert.Equal(100u, disk.ReadUInt32(Fib, 140));
                var work = NewWork();
                Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, node), work.Error.ToString());
                Assert.Equal(node, Resolve(ref disk, 2, "renamed.bin"));
                AssertFile(ref disk, node, expected);
                Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, node), $"Permanent recycle delete: {work.Error}");
                var parent = Resolve(ref disk, 1, "CopperSfsImageTest");
                Assert.True(AmigaSmartFileSystemWriteCore.Remove(ref disk, ref work, parent), work.Error.ToString());
                Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), work.Error.ToString());
            }
            using (var stream = File.OpenRead(path))
            {
                var disk = new ImageDisk(stream, manifest);
                Assert.True(AmigaSmartFileSystemCore.Mount(ref disk, Meta));
                VerifyCorpus(ref disk, manifest, cached: buffered);
            }
            output.WriteLine($"Verified {manifest.Entries.Length} original entries before mutation, after fresh mount, and after cleanup.");
        }
        finally
        {
            // Optional retention allows independent filesystem/handler checks on the completed copy.
            if (Environment.GetEnvironmentVariable("COPPERSFS_IMAGE_KEEP") != "1")
                Directory.Delete(directory, recursive: true);
        }
    }

    private static AmigaSmartFileSystemWriteCore.Work NewWork() => new()
    { Meta = Meta, Data = Data, Bitmap = APTR.FromPointer(69632), Journal = APTR.FromPointer(102400) };

    private static byte[] Payload() => Enumerable.Range(0, 65539).Select(i => (byte)(i * 17 + 29)).ToArray();

    private static void Mutate(ref ImageDisk disk, Manifest manifest, bool buffered)
    {
        var work = NewWork();
        if (buffered)
        {
            disk.WriteBack = work.Buffered = APTR.FromPointer(262144);
            Assert.True(AmigaSmartFileSystemWriteBuffer.Initialize(ref disk, work.Buffered, disk.BlockBytes, 32));
        }
        Assert.True(AmigaSmartFileSystemWriteCore.Recover(ref disk, ref work), work.Error.ToString());
        // Overwrite part of an existing externally created file, then restore
        // its exact bytes. The later full-corpus passes verify its neighbors too.
        var existing = manifest.Entries.First(e => e.Type == "file" && e.Size is >= 1024 and <= 65539 && (e.Protection & 4) == 0);
        var existingNode = 1u;
        foreach (var component in existing.Path.Split('/')) existingNode = Resolve(ref disk, existingNode, component);
        Assert.Equal((int)existing.Size, AmigaSmartFileSystemCore.Read(ref disk, existingNode, 0, Transfer,
            existing.Size, Meta, Data, out var existingError));
        Assert.Equal(DOS.Error.None, existingError);
        var originalBytes = disk.Ram.AsSpan((int)Transfer.Raw, (int)existing.Size).ToArray();
        var changedBytes = originalBytes.ToArray();
        changedBytes.AsSpan(17, 513).Fill(0xA7);
        changedBytes.CopyTo(disk.Ram, (int)Transfer.Raw);
        var overwrite = new AmigaSmartFileSystemWriteCore.Request
        { Node = existingNode, Position = 17, Buffer = APTR.FromPointer(Transfer.Raw + 17), Length = 513 };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref overwrite, out var overwritten), $"Overwrite {existing.Path}: {work.Error}");
        Assert.Equal(513u, overwritten);
        AssertFile(ref disk, existingNode, changedBytes);
        originalBytes.CopyTo(disk.Ram, (int)Transfer.Raw);
        overwrite = new() { Node = existingNode, Buffer = Transfer, Length = existing.Size };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref overwrite, out overwritten), $"Restore {existing.Path}: {work.Error}");
        Assert.Equal(existing.Size, overwritten);
        var request = new AmigaSmartFileSystemWriteCore.Request
        { Directory = 1, Name = disk.Name("CopperSfsImageTest"), Length = (uint)"CopperSfsImageTest".Length };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, true), $"Create directory: {work.Error}");
        var parent = request.Node;
        request = new() { Directory = parent, Name = disk.Name("data.bin"), Length = 8 };
        Assert.True(AmigaSmartFileSystemWriteCore.Create(ref disk, ref work, ref request, false), $"Create file: {work.Error}");
        var node = request.Node;
        var payload = Payload();
        payload.CopyTo(disk.Ram, (int)Transfer.Raw);
        request = new() { Node = node, Buffer = Transfer, Length = (uint)payload.Length };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out var actual), $"Write: {work.Error}");
        Assert.Equal((uint)payload.Length, actual);
        AssertFile(ref disk, node, payload);
        Assert.True(AmigaSmartFileSystemWriteCore.Resize(ref disk, ref work, node, 1301), $"Shrink: {work.Error}");
        AssertFile(ref disk, node, payload[..1301]);
        payload.CopyTo(disk.Ram, (int)Transfer.Raw);
        request = new() { Node = node, Position = 1301, Buffer = APTR.FromPointer(Transfer.Raw + 1301), Length = (uint)payload.Length - 1301 };
        Assert.True(AmigaSmartFileSystemWriteCore.Write(ref disk, ref work, ref request, out actual), $"Append: {work.Error}");
        Assert.Equal(request.Length, actual);
        request = new() { Node = node, Directory = parent, Name = disk.Name("renamed.bin"), Length = 11 };
        Assert.True(AmigaSmartFileSystemWriteCore.Rename(ref disk, ref work, ref request), $"Rename: {work.Error}");
        Assert.Equal(node, Resolve(ref disk, parent, "RENAMED.BIN"));
        request = new() { Node = node, Buffer = disk.Name("External image regression"), Length = (uint)"External image regression".Length };
        Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetComment), $"Comment: {work.Error}");
        request = new() { Node = node, Position = 128 };
        Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetProtection), $"Protection: {work.Error}");
        disk.WriteUInt32(APTR.FromPointer(512), 0, 400);
        disk.WriteUInt32(APTR.FromPointer(512), 4, 13);
        disk.WriteUInt32(APTR.FromPointer(512), 8, 100);
        request = new() { Node = node, Buffer = APTR.FromPointer(512) };
        Assert.True(AmigaSmartFileSystemWriteCore.SetMetadata(ref disk, ref work, ref request, DosPacketAction.SetDate), $"Date: {work.Error}");
        Assert.True(AmigaSmartFileSystemWriteCore.Flush(ref disk, ref work), $"Flush: {work.Error}");
        AssertFile(ref disk, node, payload);
    }

    private static uint Resolve(ref ImageDisk disk, uint directory, string name)
    {
        var node = AmigaSmartFileSystemCore.Resolve(ref disk, directory, disk.Name(name), (uint)name.Length, Meta, out var error);
        Assert.True(node != 0 && error == DOS.Error.None, $"Resolve {name}: {error}");
        return node;
    }

    private static void AssertFile(ref ImageDisk disk, uint node, byte[] expected)
    {
        disk.Clear(Transfer, (uint)expected.Length);
        var actual = AmigaSmartFileSystemCore.Read(ref disk, node, 0, Transfer, (uint)expected.Length + 1, Meta, Data, out var error);
        Assert.True(error == DOS.Error.None && actual == expected.Length, $"Read {node}: {error}, {actual}/{expected.Length}");
        Assert.Equal(expected, disk.Ram.AsSpan((int)Transfer.Raw, expected.Length).ToArray());
    }

    private static void VerifyCorpus(ref ImageDisk disk, Manifest manifest, bool extra = false, bool cached = false)
    {
        var expected = manifest.Entries.Where(e => !e.Hidden).ToDictionary(e => e.Path);
        var seen = new HashSet<string>();
        var directories = new Stack<(uint Node, string Path)>();
        directories.Push((1, ""));
        // SFS intentionally hides its recycle directory from ExNext. It remains
        // addressable by name and must not be mistaken for an enumeration loss.
        foreach (var hidden in manifest.Entries.Where(e => e.Hidden))
        {
            var node = Resolve(ref disk, 1, hidden.Path);
            if (hidden.Type == "dir") directories.Push((node, hidden.Path));
        }
        while (directories.TryPop(out var directory))
        {
            Assert.True(AmigaSmartFileSystemCore.Examine(ref disk, directory.Node, Fib, Meta));
            var cursor = new AmigaSmartFileSystemCore.DirectoryCursor
            { Entries = APTR.FromPointer(2048), Capacity = cached ? 32u : 0u };
            while (true)
            {
                var error = AmigaSmartFileSystemCore.ExamineNext(ref disk, ref cursor, directory.Node, Fib, Meta, Data);
                if (error == DOS.Error.NoMoreEntries) break;
                Assert.True(error == DOS.Error.None, $"Enumerate {directory.Path}: {error}");
                var name = disk.BString(Fib, 8);
                var path = directory.Path.Length == 0 ? name : directory.Path + "/" + name;
                if (extra && path == "CopperSfsImageTest") continue;
                Assert.True(seen.Add(path), $"Duplicate entry {path}");
                Assert.True(expected.TryGetValue(path, out var entry), $"Unexpected entry {path}");
                var node = disk.ReadUInt32(Fib);
                var type = unchecked((int)disk.ReadUInt32(Fib, 4));
                Assert.True((entry!.Type == "dir") == (type > 0), $"Type {path}: {type}");
                Assert.Equal(entry.Protection, disk.ReadUInt32(Fib, 116));
                Assert.Equal(entry.Comment, disk.BString(Fib, 144));
                Assert.Equal(entry.Seconds, disk.ReadUInt32(Fib, 132) * 86400 + disk.ReadUInt32(Fib, 136) * 60 + disk.ReadUInt32(Fib, 140) / 50);
                Assert.Equal(node, Resolve(ref disk, directory.Node, name));
                if (entry.Type == "dir") directories.Push((node, path));
                else if (entry.Type == "file")
                {
                    Assert.Equal(entry.Size, disk.ReadUInt32(Fib, 124));
                    using var checksum = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    var readCursor = new AmigaSmartFileSystemCore.ReadCursor();
                    for (var position = 0u; position < entry.Size;)
                    {
                        var count = Math.Min(65537u, entry.Size - position);
                        var actual = AmigaSmartFileSystemCore.Read(ref disk, ref readCursor, node, position, Transfer, count, Meta, Data, out error);
                        Assert.True(error == DOS.Error.None && actual == count, $"Read {path}@{position}: {error}, {actual}/{count}");
                        checksum.AppendData(disk.Ram.AsSpan((int)Transfer.Raw, actual));
                        position += count;
                    }
                    Assert.True(string.Equals(entry.Sha256, Convert.ToHexString(checksum.GetHashAndReset()), StringComparison.OrdinalIgnoreCase), $"Hash {path}");
                }
            }
        }
        Assert.True(expected.Count == seen.Count, "Missing entries: " + string.Join(", ", expected.Keys.Except(seen)));
    }

    private struct ImageDisk(FileStream stream, Manifest manifest) : IAmigaWritableFileSystemPlatform
    {
        public readonly byte[] Ram = new byte[4 * 1024 * 1024];
        public APTR WriteBack;
        public uint BlockCount => manifest.BlockCount;
        public uint BlockBytes => manifest.BlockBytes;
        public uint ReservedBlocks => manifest.ReservedBlocks;
        public AmigaVolumeGeometry Geometry
        {
            get { Assert.True(AmigaVolumeGeometry.Create(BlockCount, BlockBytes, ReservedBlocks, out var geometry)); return geometry; }
        }
        public bool ReadBlock(uint block, APTR destination)
        {
            if (block >= BlockCount || !IsMapped(destination, BlockBytes)) return false;
            if (!AmigaSmartFileSystemWriteBuffer.TryRead(ref this, WriteBack, BlockBytes, block, destination, 0, out var dirty)) return false;
            if (dirty) return true;
            stream.Position = checked((long)(manifest.StartByte + (ulong)block * BlockBytes));
            stream.ReadExactly(Ram.AsSpan((int)destination.Raw, (int)BlockBytes));
            return true;
        }
        public bool ReadMetadataBlock(uint block, uint id, APTR destination) => AmigaFileSystemBlockIo.ReadMetadata(ref this, block, id, destination);
        public APTR ReadMetadataView(uint block, uint id, APTR scratch) => ReadMetadataBlock(block, id, scratch) ? scratch : APTR.Null;
        public uint ReadDataBlocks(uint block, uint count, APTR destination) => AmigaFileSystemBlockIo.Read(ref this, block, count, destination);
        public bool WriteProtected() => !stream.CanWrite;
        public bool WriteBlock(uint block, APTR source)
        {
            if (block < ReservedBlocks || block >= BlockCount || !IsMapped(source, BlockBytes)) return false;
            stream.Position = checked((long)(manifest.StartByte + (ulong)block * BlockBytes));
            stream.Write(Ram.AsSpan((int)source.Raw, (int)BlockBytes));
            return true;
        }
        public bool WriteDataBlocks(uint block, uint count, APTR source) => AmigaFileSystemBlockIo.Write(ref this, block, count, source);
        public bool Flush() { stream.Flush(true); return true; }
        public byte ReadUInt8(APTR address, int offset = 0) => Ram[address.Raw + offset];
        public ushort ReadUInt16(APTR address, int offset = 0) => BinaryPrimitives.ReadUInt16BigEndian(Ram.AsSpan((int)address.Raw + offset));
        public uint ReadUInt32(APTR address, int offset = 0) => BinaryPrimitives.ReadUInt32BigEndian(Ram.AsSpan((int)address.Raw + offset));
        public void WriteUInt8(APTR address, int offset, byte value) => Ram[address.Raw + offset] = value;
        public void WriteUInt16(APTR address, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Ram.AsSpan((int)address.Raw + offset), value);
        public void WriteUInt32(APTR address, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Ram.AsSpan((int)address.Raw + offset), value);
        public bool IsMapped(APTR address, uint length) => address.Raw <= Ram.Length && length <= Ram.Length - address.Raw;
        public void Clear(APTR address, uint length) => Ram.AsSpan((int)address.Raw, (int)length).Clear();
        public void Copy(APTR source, APTR destination, uint length) => Ram.AsSpan((int)source.Raw, (int)length).CopyTo(Ram.AsSpan((int)destination.Raw));
        public APTR Name(string value)
        {
            Encoding.Latin1.GetBytes(value + '\0').CopyTo(Ram, 256);
            return APTR.FromPointer(256);
        }
        public string BString(APTR address, int offset) => Encoding.Latin1.GetString(Ram, (int)address.Raw + offset + 1, ReadUInt8(address, offset));
    }
}
