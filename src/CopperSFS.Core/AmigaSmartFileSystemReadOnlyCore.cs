using Amiga;

namespace CopperSFS;

public enum AmigaSmartFileSystemFormat : uint
{
    Classic = 0x53465300,
    Sfs2 = 0x53465302
}

/// <summary>
/// Allocation-free traversal of clean classic SFS version 3 and SFS2 version 4 volumes.
/// This API never writes or recovers a volume. Existing classic APIs remain unchanged.
/// The caller supplies distinct block-sized metadata/data buffers and mapped guest memory.
/// </summary>
public static partial class AmigaSmartFileSystemReadOnlyCore
{
    private const uint Objects = 0x4F424A43, Nodes = 0x4E444320, Tree = 0x424E4443, Clean = 0x54524F4B;

    /// <summary>Name and Comment point into the metadata buffer, until it is reused.</summary>
    public struct ReadEntry
    {
        public uint Node, Parent, Protection, ModifiedSeconds;
        public ulong Size;
        public ushort OwnerUid, OwnerGid;
        public byte Flags;
        public APTR Name, Comment;
        public uint NameLength, CommentLength;
        public readonly bool Directory => (Flags & 128) != 0;
        public readonly bool SoftLink => (Flags & 64) != 0;
    }

    internal struct Volume
    {
        public AmigaSmartFileSystemFormat Format;
        public uint Root, NodeTree, ExtentTree;
        public bool CaseSensitive;
        public readonly int Extra => Format == AmigaSmartFileSystemFormat.Sfs2 ? 2 : 0;
    }

    internal static bool Header<T>(ref T p, uint number, uint id, APTR buffer, AmigaSmartFileSystemFormat format)
        where T : struct, IAmigaFileSystemPlatform =>
        number < p.Geometry.BlockCount && p.ReadBlock(number, buffer) &&
        p.ReadUInt32(buffer) == id && p.ReadUInt32(buffer, 8) == number && Checksum(ref p, buffer, format);

    internal static bool Checksum<T>(ref T p, APTR buffer, AmigaSmartFileSystemFormat format)
        where T : struct, IAmigaFileSystemPlatform
    {
        uint sum = 0;
        for (var i = 0; i < p.Geometry.BlockBytes; i += 4) sum = unchecked(sum + p.ReadUInt32(buffer, (int)i));
        return sum == (format == AmigaSmartFileSystemFormat.Sfs2 ? 0xFFFFFFFEu : uint.MaxValue);
    }

    private static bool RootAt<T>(ref T p, uint number, APTR buffer, out AmigaSmartFileSystemFormat format,
        out ushort sequence) where T : struct, IAmigaFileSystemPlatform
    {
        format = default; sequence = 0;
        if (!p.ReadBlock(number, buffer)) return false;
        var id = p.ReadUInt32(buffer);
        if (id != (uint)AmigaSmartFileSystemFormat.Classic && id != (uint)AmigaSmartFileSystemFormat.Sfs2) return false;
        format = (AmigaSmartFileSystemFormat)id;
        var version = format == AmigaSmartFileSystemFormat.Sfs2 ? 4 : 3;
        if (p.ReadUInt32(buffer, 8) != number || p.ReadUInt16(buffer, 12) != version ||
            p.ReadUInt32(buffer, 48) != p.Geometry.BlockCount || p.ReadUInt32(buffer, 52) != p.Geometry.BlockBytes ||
            !Checksum(ref p, buffer, format)) return false;
        sequence = p.ReadUInt16(buffer, 14);
        return true;
    }

    /// <summary>Probe both root copies without modifying media. Does not establish a clean mount.</summary>
    public static bool Root<T>(ref T p, APTR metadata, out AmigaSmartFileSystemFormat format)
        where T : struct, IAmigaFileSystemPlatform
    {
        format = default;
        var g = p.Geometry;
        if (g.BlockCount < 4 || g.ReservedBlocks == 0 || g.ReservedBlocks >= g.BlockCount - 1 ||
            g.BlockBytes < 512 || g.BlockBytes > 32768 || (g.BlockBytes & (g.BlockBytes - 1)) != 0 ||
            !p.IsMapped(metadata, g.BlockBytes)) return false;
        var first = RootAt(ref p, 0, metadata, out var firstFormat, out var firstSequence);
        var last = RootAt(ref p, g.BlockCount - 1, metadata, out var lastFormat, out var lastSequence);
        if (first && last && firstFormat != lastFormat) return false;
        if (last && (!first || (short)(lastSequence - firstSequence) > 0)) { format = lastFormat; return true; }
        if (!first || !RootAt(ref p, 0, metadata, out format, out _)) return false;
        return true;
    }

    private static bool Open<T>(ref T p, APTR metadata, out Volume volume)
        where T : struct, IAmigaFileSystemPlatform
    {
        volume = default;
        if (!Root(ref p, metadata, out volume.Format)) return false;
        volume.Root = p.ReadUInt32(metadata, 104); volume.NodeTree = p.ReadUInt32(metadata, 112);
        volume.ExtentTree = p.ReadUInt32(metadata, 108); volume.CaseSensitive = (p.ReadUInt8(metadata, 20) & 128) != 0;
        if (volume.Root < p.Geometry.ReservedBlocks || volume.Root >= p.Geometry.BlockCount - 3 ||
            !Header(ref p, volume.Root + 2, Clean, metadata, volume.Format) || p.ReadUInt32(metadata, 12) != 0 ||
            !Header(ref p, volume.Root, Objects, metadata, volume.Format) || p.ReadUInt32(metadata, 12) != 0 ||
            !Scan(ref p, volume, metadata, 1, out var at, out _) ||
            (p.ReadUInt8(metadata, at + 24 + volume.Extra) & 128) == 0) return false;
        return true;
    }

    /// <summary>Dirty volumes are refused; even a writable provider receives no writes or flushes.</summary>
    public static bool Mount<T>(ref T p, APTR metadata, out AmigaSmartFileSystemFormat format)
        where T : struct, IAmigaFileSystemPlatform
    {
        var ok = Open(ref p, metadata, out var volume); format = volume.Format; return ok;
    }

    private static bool Strings<T>(ref T p, Volume v, APTR buffer, int at, out int next,
        out uint nameLength, out uint commentLength) where T : struct, IAmigaFileSystemPlatform
    {
        next = 0; nameLength = commentLength = 0;
        var end = (int)p.Geometry.BlockBytes - (p.ReadUInt32(buffer, 8) == v.Root ? 36 : 0);
        var name = at + 25 + v.Extra;
        if (at < 24 || name >= end || p.ReadUInt8(buffer, name) == 0) return false;
        var pos = name;
        while (pos < end && p.ReadUInt8(buffer, pos) != 0) pos++;
        nameLength = (uint)(pos - name);
        if (pos >= end || nameLength > 107) return false;
        var comment = ++pos;
        while (pos < end && p.ReadUInt8(buffer, pos) != 0) pos++;
        commentLength = (uint)(pos - comment);
        if (pos >= end || commentLength > 79) return false;
        next = (pos + 2) & ~1;
        return next > at && next <= end;
    }

    private static bool Scan<T>(ref T p, Volume v, APTR buffer, uint node, out int at, out int next)
        where T : struct, IAmigaFileSystemPlatform
    {
        at = 24; next = 0;
        var end = (int)p.Geometry.BlockBytes - (p.ReadUInt32(buffer, 8) == v.Root ? 36 : 0);
        while (at + 27 + v.Extra <= end && p.ReadUInt8(buffer, at + 25 + v.Extra) != 0)
        {
            if (!Strings(ref p, v, buffer, at, out next, out _, out _)) return false;
            if (p.ReadUInt32(buffer, at + 4) == node) return true;
            at = next;
        }
        return false;
    }

    internal static bool Object<T>(ref T p, Volume v, uint node, APTR buffer, out int at)
        where T : struct, IAmigaFileSystemPlatform
    {
        at = 0;
        if (node == 0) return false;
        var container = v.Root;
        if (node != 1)
        {
            var index = v.NodeTree; var bytes = p.Geometry.BlockBytes;
            var shift = 0; while ((1u << shift) < bytes) shift++;
            uint precedingStride = uint.MaxValue;
            var found = false;
            for (var depth = 0; depth < 32; depth++)
            {
                if (!Header(ref p, index, Nodes, buffer, v.Format)) return false;
                var first = p.ReadUInt32(buffer, 12); var stride = p.ReadUInt32(buffer, 16);
                if (stride == 0 || stride >= precedingStride || node < first) return false;
                var entry = (node - first) / stride;
                if (stride == 1)
                {
                    if (entry >= (bytes - 20) / 10) return false;
                    container = p.ReadUInt32(buffer, 20 + (int)entry * 10); found = true; break;
                }
                if (entry >= (bytes - 20) / 4) return false;
                // Version 4 reserves one low fullness bit. Version 3 packs
                // blocksize/32 fullness bits. Verified against native tree growth.
                var pointer = p.ReadUInt32(buffer, 20 + (int)entry * 4);
                index = pointer >> (v.Extra == 0 ? shift - 5 : 1);
                precedingStride = stride;
            }
            if (!found) return false;
        }
        return container >= p.Geometry.ReservedBlocks && container < p.Geometry.BlockCount - 1 &&
            Header(ref p, container, Objects, buffer, v.Format) && Scan(ref p, v, buffer, node, out at, out _);
    }

    private static bool Describe<T>(ref T p, Volume v, uint node, APTR metadata, out ReadEntry entry)
        where T : struct, IAmigaFileSystemPlatform
    {
        entry = default;
        if (!Object(ref p, v, node, metadata, out var at) ||
            !Strings(ref p, v, metadata, at, out _, out var nameLength, out var commentLength)) return false;
        var flags = p.ReadUInt8(metadata, at + 24 + v.Extra);
        var size = (flags & 128) != 0 ? 0ul : v.Extra == 0 ? p.ReadUInt32(metadata, at + 16) :
            ((ulong)p.ReadUInt32(metadata, at + 16) << 16) | p.ReadUInt16(metadata, at + 20);
        if (size > (ulong)(p.Geometry.BlockCount - p.Geometry.ReservedBlocks - 1) * p.Geometry.BlockBytes) return false;
        entry = new ReadEntry
        {
            Node = node, Parent = p.ReadUInt32(metadata, 12), Flags = flags, Size = size,
            Protection = p.ReadUInt32(metadata, at + 8) ^ 15,
            ModifiedSeconds = p.ReadUInt32(metadata, at + 20 + v.Extra),
            OwnerUid = p.ReadUInt16(metadata, at), OwnerGid = p.ReadUInt16(metadata, at + 2),
            Name = APTR.FromPointer(metadata.Raw + (uint)(at + 25 + v.Extra)), NameLength = nameLength,
            Comment = APTR.FromPointer(metadata.Raw + (uint)(at + 26 + v.Extra) + nameLength), CommentLength = commentLength
        };
        return true;
    }

    public static bool Describe<T>(ref T p, uint node, APTR metadata, out ReadEntry entry)
        where T : struct, IAmigaFileSystemPlatform
    {
        entry = default;
        return Open(ref p, metadata, out var v) && Describe(ref p, v, node, metadata, out entry);
    }

    private static byte Upper(byte c) => c is >= 97 and <= 122 or >= 224 and <= 246 or >= 248 and <= 254 ? (byte)(c - 32) : c;

    public static uint Resolve<T>(ref T p, uint directory, APTR name, uint length, APTR metadata, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform
    {
        error = DOS.Error.None;
        if (length > 255 || !p.IsMapped(name, length)) { error = DOS.Error.InvalidComponentName; return 0; }
        if (!Open(ref p, metadata, out var v)) { error = DOS.Error.DiskNotValidated; return 0; }
        var start = 0u;
        for (var i = 0u; i < length; i++) if (p.ReadUInt8(name, (int)i) == ':') { directory = 1; start = i + 1; }
        while (start < length)
        {
            if (!Describe(ref p, v, directory, metadata, out var parent)) goto corrupt;
            if (!parent.Directory) { error = DOS.Error.ObjectWrongType; return 0; }
            if (p.ReadUInt8(name, (int)start) == '/') { directory = parent.Parent == 0 ? 1 : parent.Parent; start++; continue; }
            var end = start; while (end < length && p.ReadUInt8(name, (int)end) != '/') end++;
            if (end - start > 107) { error = DOS.Error.InvalidComponentName; return 0; }
            if (!Object(ref p, v, directory, metadata, out var at)) goto corrupt;
            var next = p.ReadUInt32(metadata, at + 16); var matched = 0u; uint previous = 0;
            for (var visited = 0u; next != 0 && visited < p.Geometry.BlockCount; visited++)
            {
                if (!Header(ref p, next, Objects, metadata, v.Format) || p.ReadUInt32(metadata, 12) != directory ||
                    p.ReadUInt32(metadata, 20) != previous) goto corrupt;
                for (var pos = 24; pos + 27 + v.Extra <= p.Geometry.BlockBytes && p.ReadUInt8(metadata, pos + 25 + v.Extra) != 0;)
                {
                    if (!Strings(ref p, v, metadata, pos, out var following, out var count, out _)) goto corrupt;
                    var same = count == end - start;
                    for (var i = 0u; same && i < count; i++)
                    {
                        var a = p.ReadUInt8(name, (int)(start + i)); var b = p.ReadUInt8(metadata, pos + 25 + v.Extra + (int)i);
                        same = v.CaseSensitive ? a == b : Upper(a) == Upper(b);
                    }
                    if (same) { matched = p.ReadUInt32(metadata, pos + 4); break; }
                    pos = following;
                }
                if (matched != 0) break;
                previous = next; next = p.ReadUInt32(metadata, 16);
            }
            if (matched == 0) { error = next == 0 ? DOS.Error.ObjectNotFound : DOS.Error.DiskNotValidated; return 0; }
            directory = matched; start = end < length ? end + 1 : end;
        }
        if (Object(ref p, v, directory, metadata, out _)) return directory;
    corrupt:
        error = DOS.Error.DiskNotValidated; return 0;
    }

    internal static bool Extent<T>(ref T p, Volume v, uint key, APTR scratch, out uint count, out uint next, out uint previous)
        where T : struct, IAmigaFileSystemPlatform
    {
        count = next = previous = 0;
        var block = v.ExtentTree;
        for (var depth = 0; depth < 32; depth++)
        {
            if (!Header(ref p, block, Tree, scratch, v.Format)) return false;
            var entries = p.ReadUInt16(scratch, 12); var leaf = p.ReadUInt8(scratch, 14) != 0;
            var stride = p.ReadUInt8(scratch, 15);
            if (entries == 0 || stride != (leaf ? 14 + v.Extra : 8) ||
                (uint)entries * stride > p.Geometry.BlockBytes - 16) return false;
            var selected = -1; uint lastKey = 0;
            for (var i = 0; i < entries; i++)
            {
                var at = 16 + i * stride; var candidate = p.ReadUInt32(scratch, at);
                if (i != 0 && candidate <= lastKey) return false;
                if (candidate <= key) selected = at;
                lastKey = candidate;
            }
            if (selected < 0) return false;
            if (!leaf) { block = p.ReadUInt32(scratch, selected + 4); continue; }
            if (p.ReadUInt32(scratch, selected) != key) return false;
            next = p.ReadUInt32(scratch, selected + 4); previous = p.ReadUInt32(scratch, selected + 8);
            count = v.Extra == 0 ? p.ReadUInt16(scratch, selected + 12) : p.ReadUInt32(scratch, selected + 12);
            return key >= p.Geometry.ReservedBlocks && key < p.Geometry.BlockCount - 1 && count != 0 &&
                count <= p.Geometry.BlockCount - 1 - key &&
                (next == 0 || next >= p.Geometry.ReservedBlocks && next < p.Geometry.BlockCount - 1);
        }
        return false;
    }

    /// <summary>64-bit byte position; each transfer remains bounded by the mapped buffer and Int32.MaxValue.</summary>
    public static int Read<T>(ref T p, uint node, ulong position, APTR destination, uint length,
        APTR metadata, APTR data, out DOS.Error error) where T : struct, IAmigaFileSystemPlatform
    {
        error = DOS.Error.None;
        if (length > int.MaxValue || !p.IsMapped(destination, length) || !p.IsMapped(data, p.Geometry.BlockBytes) ||
            !Open(ref p, metadata, out var v) || !Describe(ref p, v, node, metadata, out var entry))
        { error = DOS.Error.DiskNotValidated; return -1; }
        if (entry.Directory || entry.SoftLink) { error = DOS.Error.ObjectWrongType; return -1; }
        if (position > entry.Size) { error = DOS.Error.SeekError; return -1; }
        if (length > entry.Size - position) length = (uint)(entry.Size - position);
        if (length == 0) return 0;
        if (!Object(ref p, v, node, metadata, out var at)) { error = DOS.Error.DiskNotValidated; return -1; }
        var extent = p.ReadUInt32(metadata, at + 12); var bytes = p.Geometry.BlockBytes;
        ulong extentStart = 0; uint extentSize = 0, next = 0, copied = 0, previous = node | 0x80000000;
        while (copied < length)
        {
            var fileBlock = position / bytes;
            while (extentSize == 0 || fileBlock >= extentStart + extentSize)
            {
                if (extentSize != 0) { extentStart += extentSize; previous = extent; extent = next; }
                if (!Extent(ref p, v, extent, metadata, out extentSize, out next, out var back) ||
                    (back != previous && !(v.Extra == 0 && extentStart == 0 && back == 0)) ||
                    extentStart + extentSize > p.Geometry.BlockCount) goto failed;
            }
            var physical = (ulong)extent + fileBlock - extentStart;
            if (physical >= p.Geometry.BlockCount - 1 || !p.ReadBlock((uint)physical, data)) goto failed;
            var within = (uint)(position % bytes); var take = bytes - within;
            if (take > length - copied) take = length - copied;
            for (var i = 0u; i < take; i++) p.WriteUInt8(destination, (int)(copied + i), p.ReadUInt8(data, (int)(within + i)));
            copied += take; position += take;
        }
        return (int)copied;
    failed:
        error = DOS.Error.DiskNotValidated; return copied == 0 ? -1 : (int)copied;
    }

    /// <summary>Initialize cursor to the directory node. Hidden entries are skipped. ReadEntry strings use metadata.</summary>
    public static DOS.Error ExamineNext<T>(ref T p, uint directory, ref uint cursor, APTR metadata, out ReadEntry entry)
        where T : struct, IAmigaFileSystemPlatform
    {
        entry = default;
        if (!Open(ref p, metadata, out var v)) return DOS.Error.DiskNotValidated;
        if (!Describe(ref p, v, directory, metadata, out var parent)) return DOS.Error.InvalidLock;
        if (!parent.Directory) return DOS.Error.ObjectWrongType;
        if (!Object(ref p, v, directory, metadata, out var at)) return DOS.Error.DiskNotValidated;
        var block = p.ReadUInt32(metadata, at + 16); uint previous = 0;
        var seen = cursor == directory;
        for (var visited = 0u; block != 0 && visited < p.Geometry.BlockCount; visited++)
        {
            if (!Header(ref p, block, Objects, metadata, v.Format) || p.ReadUInt32(metadata, 12) != directory ||
                p.ReadUInt32(metadata, 20) != previous) return DOS.Error.DiskNotValidated;
            for (var pos = 24; pos + 27 + v.Extra <= p.Geometry.BlockBytes && p.ReadUInt8(metadata, pos + 25 + v.Extra) != 0;)
            {
                if (!Strings(ref p, v, metadata, pos, out var following, out _, out _)) return DOS.Error.DiskNotValidated;
                var node = p.ReadUInt32(metadata, pos + 4); var flags = p.ReadUInt8(metadata, pos + 24 + v.Extra);
                if (seen && (flags & 1) == 0)
                {
                    if (!Describe(ref p, v, node, metadata, out entry) || entry.Parent != directory) return DOS.Error.DiskNotValidated;
                    cursor = node; return DOS.Error.None;
                }
                if (node == cursor) seen = true;
                pos = following;
            }
            previous = block; block = p.ReadUInt32(metadata, 16);
        }
        return block == 0 && seen ? DOS.Error.NoMoreEntries : DOS.Error.DiskNotValidated;
    }

    public static bool CountUsed<T>(ref T p, APTR metadata, out uint used)
        where T : struct, IAmigaFileSystemPlatform
    {
        used = 0;
        if (!Open(ref p, metadata, out _)) return false;
        var free = p.ReadUInt32(metadata, (int)p.Geometry.BlockBytes - 28);
        var available = p.Geometry.BlockCount - p.Geometry.ReservedBlocks - 1;
        if (free > available) return false;
        used = available - free; return true;
    }
}
