using Amiga;

namespace CopperSFS;

/// <summary>
/// Classic SFS (SFS\0) traversal. Keys are SFS object-node numbers.
/// The caller supplies two block-sized scratch buffers; no state is allocated.
/// Layout follows the original SmartFilesystem blockstructure/objects/nodes sources.
/// </summary>
public static class AmigaSmartFileSystemCore
{
    public const uint DosType = 0x53465300;
    public const uint RootNode = 1;
    private const uint RootId = DosType, ObjectId = 0x4F424A43, NodeId = 0x4E444320, TreeId = 0x424E4443;

    private static bool Checksum<T>(ref T p, APTR block) where T : struct, IAmigaFileSystemPlatform
    {
        uint sum = 0;
        for (var i = 0; i < p.Geometry.BlockBytes; i += 4) sum = unchecked(sum + p.ReadUInt32(block, (int)i));
        // SFS CALCCHECKSUM starts at one; a valid block sums to 0xFFFFFFFF.
        return sum == uint.MaxValue;
    }

    internal static bool ValidChecksum<T>(ref T p, APTR block)
        where T : struct, IAmigaFileSystemPlatform => Checksum(ref p, block);

    internal static bool Block<T>(ref T p, uint number, uint id, APTR buffer) where T : struct, IAmigaFileSystemPlatform =>
        number < p.Geometry.BlockCount && p.ReadBlock(number, buffer) &&
        p.ReadUInt32(buffer) == id && p.ReadUInt32(buffer, 8) == number && Checksum(ref p, buffer);

    public static bool Root<T>(ref T p, APTR buffer) where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = p.Geometry;
        if (geometry.BlockCount < 4 || geometry.BlockBytes < 512) return false;
        var first = Block(ref p, 0, RootId, buffer) && p.ReadUInt16(buffer, 12) == 3 &&
            p.ReadUInt32(buffer, 48) == geometry.BlockCount && p.ReadUInt32(buffer, 52) == geometry.BlockBytes;
        var firstSequence = first ? p.ReadUInt16(buffer, 14) : 0;
        var last = geometry.BlockCount - 1;
        if (Block(ref p, last, RootId, buffer) && p.ReadUInt16(buffer, 12) == 3 &&
            p.ReadUInt32(buffer, 48) == geometry.BlockCount && p.ReadUInt32(buffer, 52) == geometry.BlockBytes &&
            (!first || (short)(p.ReadUInt16(buffer, 14) - firstSequence) > 0)) return true;
        return first && Block(ref p, 0, RootId, buffer);
    }

    public static bool Mount<T>(ref T p, APTR metadata) where T : struct, IAmigaFileSystemPlatform
    {
        if (!Root(ref p, metadata)) return false;
        var rootContainer = p.ReadUInt32(metadata, 104);
        // An unfinished transaction requires the original handler's replay logic.
        var transaction = rootContainer + 2;
        if (transaction >= p.Geometry.BlockCount || !p.ReadBlock(transaction, metadata) ||
            p.ReadUInt32(metadata, 8) != transaction || !Checksum(ref p, metadata) ||
            p.ReadUInt32(metadata) == 0x54524641) return false; // TRFA
        if (!Block(ref p, rootContainer, ObjectId, metadata)) return false;
        return p.ReadUInt32(metadata, 28) == RootNode && (p.ReadUInt8(metadata, 48) & 128) != 0 &&
            p.ReadUInt8(metadata, 49) != 0;
    }

    private static bool Scan<T>(ref T p, APTR buffer, uint wanted, out int offset, out int next)
        where T : struct, IAmigaFileSystemPlatform
    {
        offset = 0; next = 0;
        var end = (int)p.Geometry.BlockBytes;
        for (var at = 24; at + 27 <= end;)
        {
            if (p.ReadUInt8(buffer, at + 25) == 0) return wanted == 0;
            var nameEnd = at + 25;
            while (nameEnd < end && p.ReadUInt8(buffer, nameEnd) != 0) nameEnd++;
            if (nameEnd == end) return false;
            var commentEnd = nameEnd + 1;
            while (commentEnd < end && p.ReadUInt8(buffer, commentEnd) != 0) commentEnd++;
            if (commentEnd == end) return false;
            next = (commentEnd + 2) & ~1;
            if (p.ReadUInt32(buffer, at + 4) == wanted) { offset = at; return true; }
            if (next <= at || next > end) return false;
            at = next;
        }
        return wanted == 0;
    }

    internal static bool Object<T>(ref T p, uint node, APTR buffer, out int offset)
        where T : struct, IAmigaFileSystemPlatform
    {
        offset = 0;
        if (node == 0 || !Root(ref p, buffer)) return false;
        var container = p.ReadUInt32(buffer, 104);
        if (node != RootNode)
        {
            var index = p.ReadUInt32(buffer, 112);
            var bytes = p.Geometry.BlockBytes;
            var shift = 0;
            while ((1u << shift) < bytes) shift++;
            var childShift = shift - 5;
            for (var depth = 0u; depth < p.Geometry.BlockCount; depth++)
            {
                if (!Block(ref p, index, NodeId, buffer)) return false;
                var first = p.ReadUInt32(buffer, 12);
                var stride = p.ReadUInt32(buffer, 16);
                if (stride == 0 || node < first) return false;
                var entry = (node - first) / stride;
                if (stride == 1)
                {
                    if (entry >= (bytes - 20) / 10) return false;
                    container = p.ReadUInt32(buffer, 20 + (int)entry * 10);
                    break;
                }
                if (entry >= (bytes - 20) / 4) return false;
                index = p.ReadUInt32(buffer, 20 + (int)entry * 4) >> childShift;
                if (index == 0) return false;
            }
        }
        return container != 0 && Block(ref p, container, ObjectId, buffer) &&
            Scan(ref p, buffer, node, out offset, out _);
    }

    public static bool Describe<T>(ref T p, uint node, APTR metadata, out uint size, out bool directory,
        out uint parent) where T : struct, IAmigaFileSystemPlatform
    {
        size = 0; directory = false; parent = 0;
        if (!Object(ref p, node, metadata, out var at)) return false;
        var bits = p.ReadUInt8(metadata, at + 24);
        directory = (bits & 128) != 0;
        size = directory ? 0 : p.ReadUInt32(metadata, at + 16);
        parent = p.ReadUInt32(metadata, 12);
        return true;
    }

    public static uint Resolve<T>(ref T p, uint directory, APTR name, uint length, APTR metadata,
        out DOS.Error error) where T : struct, IAmigaFileSystemPlatform
    {
        error = DOS.Error.None;
        if (length > 255) { error = DOS.Error.InvalidComponentName; return 0; }
        if (!Root(ref p, metadata)) { error = DOS.Error.DiskNotValidated; return 0; }
        var caseSensitive = (p.ReadUInt8(metadata, 20) & 128) != 0;
        var start = 0u;
        for (var i = 0u; i < length; i++) if (p.ReadUInt8(name, (int)i) == ':') { directory = RootNode; start = i + 1; }
        while (start < length)
        {
            if (p.ReadUInt8(name, (int)start) == '/')
            {
                if (!Describe(ref p, directory, metadata, out _, out _, out var parent))
                { error = DOS.Error.DiskNotValidated; return 0; }
                directory = parent == 0 ? RootNode : parent; start++; continue;
            }
            var end = start;
            while (end < length && p.ReadUInt8(name, (int)end) != '/') end++;
            if (end - start > 100) { error = DOS.Error.InvalidComponentName; return 0; }
            if (!Object(ref p, directory, metadata, out var at)) { error = DOS.Error.DiskNotValidated; return 0; }
            if ((p.ReadUInt8(metadata, at + 24) & 128) == 0) { error = DOS.Error.ObjectWrongType; return 0; }
            var next = p.ReadUInt32(metadata, at + 16);
            var matched = 0u;
            for (var visited = 0u; next != 0 && visited < p.Geometry.BlockCount; visited++)
            {
                if (!Block(ref p, next, ObjectId, metadata) || p.ReadUInt32(metadata, 12) != directory)
                { error = DOS.Error.DiskNotValidated; return 0; }
                for (var pos = 24; pos + 27 <= p.Geometry.BlockBytes;)
                {
                    if (p.ReadUInt8(metadata, pos + 25) == 0) break;
                    if (!Scan(ref p, metadata, p.ReadUInt32(metadata, pos + 4), out _, out var following))
                    { error = DOS.Error.DiskNotValidated; return 0; }
                    var count = following - pos;
                    var same = count >= 27 && following <= p.Geometry.BlockBytes && end - start == NameLength(ref p, metadata, pos + 25);
                    for (var i = 0u; same && i < end - start; i++)
                    {
                        var a = p.ReadUInt8(name, (int)(start + i)); var b = p.ReadUInt8(metadata, pos + 25 + (int)i);
                        if (!caseSensitive) { a = Upper(a); b = Upper(b); }
                        same = a == b;
                    }
                    if (same) { matched = p.ReadUInt32(metadata, pos + 4); break; }
                    pos = following;
                }
                if (matched != 0) break;
                next = p.ReadUInt32(metadata, 16);
            }
            if (matched == 0) { error = next != 0 ? DOS.Error.DiskNotValidated : DOS.Error.ObjectNotFound; return 0; }
            directory = matched; start = end < length ? end + 1 : end;
        }
        if (!Object(ref p, directory, metadata, out _)) { error = DOS.Error.DiskNotValidated; return 0; }
        return directory;
    }

    private static uint NameLength<T>(ref T p, APTR buffer, int offset) where T : struct, IAmigaFileSystemPlatform
    {
        var end = (int)p.Geometry.BlockBytes;
        var count = 0u;
        while (offset + count < end && p.ReadUInt8(buffer, offset + (int)count) != 0) count++;
        return count;
    }

    private static byte Upper(byte value) =>
        value is >= 97 and <= 122 or >= 224 and <= 246 or >= 248 and <= 254 ? (byte)(value - 32) : value;

    internal static bool Extent<T>(ref T p, uint root, uint key, APTR scratch, out uint size, out uint next)
        where T : struct, IAmigaFileSystemPlatform
    {
        size = next = 0;
        var block = root;
        for (var depth = 0u; block != 0 && depth < p.Geometry.BlockCount; depth++)
        {
            if (!Block(ref p, block, TreeId, scratch)) return false;
            var count = p.ReadUInt16(scratch, 12); var leaf = p.ReadUInt8(scratch, 14) != 0;
            var stride = p.ReadUInt8(scratch, 15);
            if (stride < (leaf ? 14 : 8) || count == 0 || (uint)count * stride > p.Geometry.BlockBytes - 16) return false;
            var selected = -1;
            for (var i = 0; i < count; i++)
            {
                var at = 16 + i * stride;
                if (p.ReadUInt32(scratch, at) <= key) selected = at; else break;
            }
            if (selected < 0) return false;
            if (leaf)
            {
                if (p.ReadUInt32(scratch, selected) != key) return false;
                next = p.ReadUInt32(scratch, selected + 4);
                size = p.ReadUInt16(scratch, selected + 12);
                return size != 0;
            }
            block = p.ReadUInt32(scratch, selected + 4);
        }
        return false;
    }

    public static int Read<T>(ref T p, uint node, uint position, APTR destination, uint length,
        APTR metadata, APTR data, out DOS.Error error) where T : struct, IAmigaFileSystemPlatform
    {
        error = DOS.Error.None;
        if (length > int.MaxValue || !p.IsMapped(destination, length) || !Object(ref p, node, metadata, out var at))
        { error = DOS.Error.DiskNotValidated; return -1; }
        if ((p.ReadUInt8(metadata, at + 24) & (128 | 64)) != 0) { error = DOS.Error.ObjectWrongType; return -1; }
        var size = p.ReadUInt32(metadata, at + 16);
        var first = p.ReadUInt32(metadata, at + 12);
        if (position > size) { error = DOS.Error.SeekError; return -1; }
        if (length > size - position) length = size - position;
        if (length == 0) return 0;
        if (!Root(ref p, metadata)) { error = DOS.Error.DiskNotValidated; return -1; }
        var tree = p.ReadUInt32(metadata, 108);
        var blockBytes = p.Geometry.BlockBytes;
        var fileBlock = position / blockBytes;
        var extent = first; var extentStart = 0u; var extentSize = 0u; var next = 0u;
        var copied = 0u;
        while (copied < length)
        {
            while (extentSize == 0 || fileBlock >= extentStart + extentSize)
            {
                if (extentSize != 0) { extentStart += extentSize; extent = next; }
                if (extent == 0 || !Extent(ref p, tree, extent, metadata, out extentSize, out next)) goto failed;
            }
            var diskBlock = extent + fileBlock - extentStart;
            if (diskBlock >= p.Geometry.BlockCount || !p.ReadBlock(diskBlock, data)) goto failed;
            var within = position % blockBytes;
            var count = blockBytes - within;
            if (count > length - copied) count = length - copied;
            for (var i = 0u; i < count; i++) p.WriteUInt8(destination, (int)(copied + i), p.ReadUInt8(data, (int)(within + i)));
            copied += count; position += count; fileBlock++;
        }
        return (int)copied;
    failed:
        error = DOS.Error.DiskNotValidated;
        return copied == 0 ? -1 : (int)copied;
    }

    public static bool Examine<T>(ref T p, uint node, APTR fib, APTR metadata) where T : struct, IAmigaFileSystemPlatform
    {
        if (!p.IsMapped(fib, FileInfoBlock.SizeInBytes) || !Object(ref p, node, metadata, out var at)) return false;
        p.Clear(fib, FileInfoBlock.SizeInBytes);
        var bits = p.ReadUInt8(metadata, at + 24);
        var type = node == RootNode ? 1u : (bits & 64) != 0 ? 3u : (bits & 128) != 0 ? 2u : 0xFFFFFFFD;
        var size = node == RootNode || (bits & 128) == 0 ? p.ReadUInt32(metadata, at + 16) : 0u;
        p.WriteUInt32(fib, 0, node); p.WriteUInt32(fib, 4, type); p.WriteUInt32(fib, 120, type);
        var nameLength = NameLength(ref p, metadata, at + 25);
        if (nameLength > 107) return false;
        p.WriteUInt8(fib, 8, (byte)nameLength);
        for (var i = 0u; i < nameLength; i++) p.WriteUInt8(fib, 9 + (int)i, p.ReadUInt8(metadata, at + 25 + (int)i));
        p.WriteUInt32(fib, 116, p.ReadUInt32(metadata, at + 8) ^ 15);
        p.WriteUInt32(fib, 124, size);
        p.WriteUInt32(fib, 128, node != RootNode && (bits & 128) != 0 ? 1u :
            size / p.Geometry.BlockBytes + (size % p.Geometry.BlockBytes == 0 ? 0u : 1u));
        var seconds = p.ReadUInt32(metadata, at + 20);
        p.WriteUInt32(fib, 132, seconds / 86400); p.WriteUInt32(fib, 136, (seconds % 86400) / 60);
        p.WriteUInt32(fib, 140, (seconds % 60) * 50);
        var commentAt = at + 26 + (int)nameLength;
        var commentLength = NameLength(ref p, metadata, commentAt);
        if (commentLength > 79) return false;
        p.WriteUInt8(fib, 144, (byte)commentLength);
        for (var i = 0u; i < commentLength; i++) p.WriteUInt8(fib, 145 + (int)i, p.ReadUInt8(metadata, commentAt + (int)i));
        p.WriteUInt16(fib, 224, p.ReadUInt16(metadata, at));
        p.WriteUInt16(fib, 226, p.ReadUInt16(metadata, at + 2));
        return true;
    }

    public static DOS.Error ExamineNext<T>(ref T p, uint directory, APTR fib, APTR metadata, APTR data)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (!p.IsMapped(fib, FileInfoBlock.SizeInBytes) || !Object(ref p, directory, metadata, out var at)) return DOS.Error.InvalidLock;
        if ((p.ReadUInt8(metadata, at + 24) & 128) == 0) return DOS.Error.ObjectWrongType;
        var wanted = p.ReadUInt32(fib);
        var next = p.ReadUInt32(metadata, at + 16);
        var seen = wanted == directory;
        for (var visited = 0u; next != 0 && visited < p.Geometry.BlockCount; visited++)
        {
            if (!Block(ref p, next, ObjectId, metadata) || p.ReadUInt32(metadata, 12) != directory) return DOS.Error.DiskNotValidated;
            var candidate = 0u;
            var foundWanted = false;
            for (var pos = 24; pos + 27 <= p.Geometry.BlockBytes;)
            {
                if (p.ReadUInt8(metadata, pos + 25) == 0) break;
                var node = p.ReadUInt32(metadata, pos + 4);
                if (!Scan(ref p, metadata, node, out _, out var following)) return DOS.Error.DiskNotValidated;
                if (node == wanted)
                {
                    foundWanted = true;
                    break;
                }
                if ((p.ReadUInt8(metadata, pos + 24) & 1) == 0) candidate = node;
                pos = following;
            }
            if ((seen || foundWanted) && candidate != 0)
                return Examine(ref p, candidate, fib, data) ? DOS.Error.None : DOS.Error.DiskNotValidated;
            if (foundWanted) seen = true;
            next = p.ReadUInt32(metadata, 16);
        }
        return next != 0 ? DOS.Error.DiskNotValidated : seen ? DOS.Error.NoMoreEntries : DOS.Error.DiskNotValidated;
    }

    public static bool CountUsed<T>(ref T p, APTR buffer, out uint used) where T : struct, IAmigaFileSystemPlatform
    {
        used = 0;
        if (!Root(ref p, buffer)) return false;
        var rootContainer = p.ReadUInt32(buffer, 104);
        if (!Block(ref p, rootContainer, ObjectId, buffer)) return false;
        var free = p.ReadUInt32(buffer, (int)p.Geometry.BlockBytes - 28);
        var reserved = p.Geometry.ReservedBlocks + 1; // SFS keeps one root at the end.
        if (reserved > p.Geometry.BlockCount || free > p.Geometry.BlockCount - reserved) return false;
        used = p.Geometry.BlockCount - reserved - free;
        return true;
    }
}
