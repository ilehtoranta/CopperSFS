using Amiga;

namespace CopperSFS;

/// <summary>
/// Classic SFS (SFS\0) traversal. Keys are SFS object-node numbers.
/// The caller supplies two block-sized scratch buffers; no state is allocated.
/// Layout follows the original SmartFilesystem blockstructure/objects/nodes sources.
/// </summary>
public static partial class AmigaSmartFileSystemCore
{
    public const uint DosType = 0x53465300;
    public const uint RootNode = 1;
    private const uint RootId = DosType, ObjectId = 0x4F424A43, NodeId = 0x4E444320, TreeId = 0x424E4443;

    internal static uint BlockSum<T>(ref T p, APTR block, uint bytes) where T : struct, IAmigaFileSystemPlatform
    {
        var remaining = bytes;
        var cursor = block;
        uint sum = 0;
        while (remaining >= 32)
        {
            sum = unchecked(sum + p.ReadUInt32(cursor, 0) + p.ReadUInt32(cursor, 4) +
                p.ReadUInt32(cursor, 8) + p.ReadUInt32(cursor, 12) +
                p.ReadUInt32(cursor, 16) + p.ReadUInt32(cursor, 20) +
                p.ReadUInt32(cursor, 24) + p.ReadUInt32(cursor, 28));
            cursor = APTR.FromPointer(cursor.Raw + 32); remaining -= 32;
        }
        while (remaining >= 4)
        {
            sum = unchecked(sum + p.ReadUInt32(cursor));
            cursor = APTR.FromPointer(cursor.Raw + 4); remaining -= 4;
        }
        return sum;
    }

    // SFS CALCCHECKSUM starts at one; a valid block sums to 0xFFFFFFFF.
    private static bool Checksum<T>(ref T p, APTR block) where T : struct, IAmigaFileSystemPlatform
        => BlockSum(ref p, block, p.BlockBytes) == uint.MaxValue;

    internal static bool ValidChecksum<T>(ref T p, APTR block)
        where T : struct, IAmigaFileSystemPlatform => Checksum(ref p, block);

    internal static bool Block<T>(ref T p, uint number, uint id, APTR buffer) where T : struct, IAmigaFileSystemPlatform
    {
        var blockCount = p.BlockCount; var blockBytes = p.BlockBytes;
        return Block(ref p, number, id, buffer, blockCount, blockBytes);
    }

    internal static bool Block<T>(ref T p, uint number, uint id, APTR buffer, uint blockCount, uint blockBytes)
        where T : struct, IAmigaFileSystemPlatform =>
        number < blockCount && p.ReadMetadataBlock(number, id, buffer);

    public static bool Root<T>(ref T p, APTR buffer) where T : struct, IAmigaFileSystemPlatform
    {
        var source = RootView(ref p, buffer);
        if (source.IsNull) return false;
        if (source != buffer) p.Copy(source, buffer, p.BlockBytes);
        return true;
    }

    private static APTR View<T>(ref T p, uint block, uint id, APTR scratch, uint blockCount)
        where T : struct, IAmigaFileSystemPlatform => block < blockCount ? p.ReadMetadataView(block, id, scratch) : APTR.Null;

    private static APTR RootView<T>(ref T p, APTR scratch) where T : struct, IAmigaFileSystemPlatform
    {
        var blockCount = p.BlockCount; var blockBytes = p.BlockBytes;
        return RootView(ref p, scratch, blockCount, blockBytes);
    }

    private static APTR RootView<T>(ref T p, APTR scratch, uint blockCount, uint blockBytes)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (blockCount < 4 || blockBytes < 512) return APTR.Null;
        var buffer = View(ref p, 0, RootId, scratch, blockCount);
        var first = buffer.IsNotNull && p.ReadUInt16(buffer, 12) == 3 &&
            p.ReadUInt32(buffer, 48) == blockCount && p.ReadUInt32(buffer, 52) == blockBytes;
        var firstSequence = first ? p.ReadUInt16(buffer, 14) : 0;
        var last = blockCount - 1;
        buffer = View(ref p, last, RootId, scratch, blockCount);
        if (buffer.IsNotNull && p.ReadUInt16(buffer, 12) == 3 &&
            p.ReadUInt32(buffer, 48) == blockCount && p.ReadUInt32(buffer, 52) == blockBytes &&
            (!first || (short)(p.ReadUInt16(buffer, 14) - firstSequence) > 0)) return buffer;
        return first ? View(ref p, 0, RootId, scratch, blockCount) : APTR.Null;
    }

    public static bool Mount<T>(ref T p, APTR metadata) where T : struct, IAmigaFileSystemPlatform
    {
        if (!Root(ref p, metadata)) return false;
        var rootContainer = p.ReadUInt32(metadata, 104);
        // An unfinished transaction requires the original handler's replay logic.
        var transaction = rootContainer + 2;
        if (transaction >= p.BlockCount || !p.ReadBlock(transaction, metadata) ||
            p.ReadUInt32(metadata, 8) != transaction || !Checksum(ref p, metadata) ||
            p.ReadUInt32(metadata) == 0x54524641) return false; // TRFA
        if (!Block(ref p, rootContainer, ObjectId, metadata)) return false;
        return p.ReadUInt32(metadata, 28) == RootNode && (p.ReadUInt8(metadata, 48) & 128) != 0 &&
            p.ReadUInt8(metadata, 49) != 0;
    }

    private static bool Scan<T>(ref T p, APTR buffer, uint wanted, uint blockBytes, out int offset, out int next)
        where T : struct, IAmigaFileSystemPlatform
    {
        offset = 0; next = 0;
        var end = (int)blockBytes;
        for (var at = 24; at + 27 <= end;)
        {
            if (p.ReadUInt8(buffer, at + 25) == 0) return wanted == 0;
            if (!ObjectEnd(ref p, buffer, at, end, out next)) return false;
            if (p.ReadUInt32(buffer, at + 4) == wanted) { offset = at; return true; }
            if (next <= at || next > end) return false;
            at = next;
        }
        return wanted == 0;
    }

    private static bool ObjectEnd<T>(ref T p, APTR buffer, int at, int end, out int next)
        where T : struct, IAmigaFileSystemPlatform
    {
        next = 0;
        var nameEnd = at + 25;
        while (nameEnd < end && p.ReadUInt8(buffer, nameEnd) != 0) nameEnd++;
        if (nameEnd == end) return false;
        var commentEnd = nameEnd + 1;
        while (commentEnd < end && p.ReadUInt8(buffer, commentEnd) != 0) commentEnd++;
        if (commentEnd == end) return false;
        next = (commentEnd + 2) & ~1;
        return next > at && next <= end;
    }

    internal static bool Object<T>(ref T p, uint node, APTR buffer, out int offset)
        where T : struct, IAmigaFileSystemPlatform
    {
        var view = ObjectView(ref p, node, buffer, out offset);
        if (view.IsNull) return false;
        if (view != buffer) p.Copy(view, buffer, p.BlockBytes);
        return true;
    }

    private static APTR ObjectView<T>(ref T p, uint node, APTR scratch, out int offset)
        where T : struct, IAmigaFileSystemPlatform
    {
        offset = 0;
        if (node == 0) return APTR.Null;
        var blockCount = p.BlockCount; var blockBytes = p.BlockBytes;
        var buffer = RootView(ref p, scratch, blockCount, blockBytes);
        if (buffer.IsNull) return APTR.Null;
        var container = p.ReadUInt32(buffer, 104);
        return ObjectViewFromRoots(ref p, node, container, p.ReadUInt32(buffer, 112),
            blockCount, blockBytes, scratch, out offset);
    }

    internal static bool ObjectFromRoots<T>(ref T p, uint node, uint container, uint index,
        uint blockCount, uint bytes, APTR buffer, out int offset) where T : struct, IAmigaFileSystemPlatform
    {
        var view = ObjectViewFromRoots(ref p, node, container, index, blockCount, bytes, buffer, out offset);
        if (view.IsNull) return false;
        if (view != buffer) p.Copy(view, buffer, bytes);
        return true;
    }

    internal static APTR ObjectViewFromRoots<T>(ref T p, uint node, uint container, uint index,
        uint blockCount, uint bytes, APTR scratch, out int offset) where T : struct, IAmigaFileSystemPlatform
    {
        offset = 0;
        if (node == 0) return APTR.Null;
        var buffer = scratch;
        if (node != RootNode)
        {
            buffer = NodeView(ref p, node, index, blockCount, bytes, scratch, out var entry);
            if (buffer.IsNull) return APTR.Null;
            container = p.ReadUInt32(buffer, entry);
        }
        if (container == 0) return APTR.Null;
        buffer = View(ref p, container, ObjectId, scratch, blockCount);
        return buffer.IsNotNull && Scan(ref p, buffer, node, bytes, out offset, out _) ? buffer : APTR.Null;
    }

    private static APTR NodeView<T>(ref T p, uint node, uint root, uint blockCount, uint bytes,
        APTR scratch, out int offset) where T : struct, IAmigaFileSystemPlatform
    {
        offset = 0;
        var shift = 0;
        while ((1u << shift) < bytes) shift++;
        var childShift = shift - 5;
        var index = root;
        for (var depth = 0u; depth < blockCount; depth++)
        {
            var buffer = View(ref p, index, NodeId, scratch, blockCount);
            if (buffer.IsNull) return APTR.Null;
            var first = p.ReadUInt32(buffer, 12);
            var stride = p.ReadUInt32(buffer, 16);
            if (stride == 0 || node < first) return APTR.Null;
            var entry = (node - first) / stride;
            if (stride == 1)
            {
                if (entry >= (bytes - 20) / 10) return APTR.Null;
                offset = 20 + (int)entry * 10;
                return buffer;
            }
            if (entry >= (bytes - 20) / 4) return APTR.Null;
            index = p.ReadUInt32(buffer, 20 + (int)entry * 4) >> childShift;
            if (index == 0) return APTR.Null;
        }
        return APTR.Null;
    }

    public static bool Describe<T>(ref T p, uint node, APTR metadata, out uint size, out bool directory,
        out uint parent) where T : struct, IAmigaFileSystemPlatform
    {
        size = 0; directory = false; parent = 0;
        var view = ObjectView(ref p, node, metadata, out var at);
        if (view.IsNull) return false;
        var bits = p.ReadUInt8(view, at + 24);
        directory = (bits & 128) != 0;
        size = directory ? 0 : p.ReadUInt32(view, at + 16);
        parent = p.ReadUInt32(view, 12);
        return true;
    }

    public static uint Resolve<T>(ref T p, uint directory, APTR name, uint length, APTR metadata,
        out DOS.Error error) where T : struct, IAmigaFileSystemPlatform
        => Resolve(ref p, directory, name, length, metadata, out error, out _, out _);

    /// <summary>Also returns the verified object record, avoiding a second lookup
    /// when the caller needs its size or type. The view is read-only and expires
    /// at the next block I/O, like ReadMetadataView.</summary>
    public static uint Resolve<T>(ref T p, uint directory, APTR name, uint length, APTR metadata,
        out DOS.Error error, out APTR record, out int recordOffset) where T : struct, IAmigaFileSystemPlatform
    {
        record = APTR.Null; recordOffset = 0;
        error = DOS.Error.None;
        if (length > 255) { error = DOS.Error.InvalidComponentName; return 0; }
        var blockCount = p.BlockCount; var blockBytes = p.BlockBytes;
        var view = RootView(ref p, metadata, blockCount, blockBytes);
        if (view.IsNull) { error = DOS.Error.DiskNotValidated; return 0; }
        var caseSensitive = (p.ReadUInt8(view, 20) & 128) != 0;
        var rootContainer = p.ReadUInt32(view, 104);
        var nodeRoot = p.ReadUInt32(view, 112);
        var start = 0u;
        for (var i = 0u; i < length; i++) if (p.ReadUInt8(name, (int)i) == ':') { directory = RootNode; start = i + 1; }
        while (start < length)
        {
            if (p.ReadUInt8(name, (int)start) == '/')
            {
                view = ObjectViewFromRoots(ref p, directory, rootContainer, nodeRoot,
                    blockCount, blockBytes, metadata, out _);
                if (view.IsNull)
                { error = DOS.Error.DiskNotValidated; return 0; }
                var parent = p.ReadUInt32(view, 12);
                directory = parent == 0 ? RootNode : parent; start++; continue;
            }
            var end = start;
            while (end < length && p.ReadUInt8(name, (int)end) != '/') end++;
            if (end - start > 100) { error = DOS.Error.InvalidComponentName; return 0; }
            view = ObjectViewFromRoots(ref p, directory, rootContainer, nodeRoot,
                blockCount, blockBytes, metadata, out var at);
            if (view.IsNull) { error = DOS.Error.DiskNotValidated; return 0; }
            if ((p.ReadUInt8(view, at + 24) & 128) == 0) { error = DOS.Error.ObjectWrongType; return 0; }
            var next = p.ReadUInt32(view, at + 16);
            var matched = 0u;
            var hashTable = p.ReadUInt32(view, at + 12);
            if (hashTable != 0)
            {
                matched = ResolveHashed(ref p, directory, nodeRoot, hashTable, name, start, end - start,
                    caseSensitive, blockCount, blockBytes, metadata, out error, out record, out recordOffset);
                if (matched == 0) return 0;
                directory = matched; start = end < length ? end + 1 : end;
                // The hash lookup already checked the node, container and
                // complete object record. No second traversal is needed.
                if (start == length) return directory;
                continue;
            }
            for (var visited = 0u; next != 0 && visited < blockCount; visited++)
            {
                view = View(ref p, next, ObjectId, metadata, blockCount);
                if (view.IsNull || p.ReadUInt32(view, 12) != directory)
                { error = DOS.Error.DiskNotValidated; return 0; }
                for (var pos = 24; pos + 27 <= blockBytes;)
                {
                    if (p.ReadUInt8(view, pos + 25) == 0) break;
                    if (!ObjectEnd(ref p, view, pos, (int)blockBytes, out var following))
                    { error = DOS.Error.DiskNotValidated; return 0; }
                    var count = following - pos;
                    var same = count >= 27 && following <= blockBytes && pos + 25 + end - start < blockBytes;
                    for (var i = 0u; same && i < end - start; i++)
                    {
                        var a = p.ReadUInt8(name, (int)(start + i)); var b = p.ReadUInt8(view, pos + 25 + (int)i);
                        if (!caseSensitive) { a = Upper(a); b = Upper(b); }
                        same = a == b;
                    }
                    if (same && p.ReadUInt8(view, pos + 25 + (int)(end - start)) == 0)
                    { matched = p.ReadUInt32(view, pos + 4); record = view; recordOffset = pos; break; }
                    pos = following;
                }
                if (matched != 0) break;
                next = p.ReadUInt32(view, 16);
            }
            if (matched == 0) { error = next != 0 ? DOS.Error.DiskNotValidated : DOS.Error.ObjectNotFound; return 0; }
            directory = matched; start = end < length ? end + 1 : end;
            if (start == length) return directory;
        }
        record = ObjectViewFromRoots(ref p, directory, rootContainer, nodeRoot, blockCount,
            blockBytes, metadata, out recordOffset);
        if (record.IsNull) { error = DOS.Error.DiskNotValidated; return 0; }
        return directory;
    }

    private static uint ResolveHashed<T>(ref T p, uint directory, uint nodeRoot, uint table,
        APTR name, uint start, uint length, bool sensitive, uint blockCount, uint bytes,
        APTR scratch, out DOS.Error error, out APTR record, out int recordOffset) where T : struct, IAmigaFileSystemPlatform
    {
        record = APTR.Null; recordOffset = 0;
        error = DOS.Error.DiskNotValidated;
        var hash = length & 65535u;
        for (var i = 0u; i < length; i++)
        {
            var value = (uint)p.ReadUInt8(name, (int)(start + i));
            if (!sensitive && ((value >= 97 && value <= 122) ||
                (value >= 224 && value <= 246) || (value >= 248 && value <= 254))) value -= 32;
            hash = unchecked(hash * 13 + value) & 65535u;
        }
        var view = View(ref p, table, 0x48544142, scratch, blockCount);
        if (view.IsNull || p.ReadUInt32(view, 12) != directory) return 0;
        var node = p.ReadUInt32(view, 16 + (int)(hash % ((bytes - 16) / 4)) * 4);
        for (var visited = 0u; node != 0 && visited < blockCount; visited++)
        {
            view = NodeView(ref p, node, nodeRoot, blockCount, bytes, scratch, out var slot);
            if (view.IsNull) return 0;
            var container = p.ReadUInt32(view, slot);
            var next = p.ReadUInt32(view, slot + 4);
            if (p.ReadUInt16(view, slot + 8) == hash)
            {
                view = View(ref p, container, ObjectId, scratch, blockCount);
                if (view.IsNull || p.ReadUInt32(view, 12) != directory ||
                    !Scan(ref p, view, node, bytes, out var at, out _)) return 0;
                var same = at + 25 + length < bytes;
                for (var i = 0u; same && i < length; i++)
                {
                    var a = p.ReadUInt8(name, (int)(start + i));
                    var b = p.ReadUInt8(view, at + 25 + (int)i);
                    if (!sensitive) { a = Upper(a); b = Upper(b); }
                    same = a == b;
                }
                if (same && p.ReadUInt8(view, at + 25 + (int)length) == 0)
                { record = view; recordOffset = at; error = DOS.Error.None; return node; }
            }
            node = next;
        }
        if (node == 0) error = DOS.Error.ObjectNotFound;
        return 0;
    }

    private static uint NameLength<T>(ref T p, APTR buffer, int offset, uint blockBytes) where T : struct, IAmigaFileSystemPlatform
    {
        var end = (int)blockBytes;
        var count = 0u;
        while (offset + count < end && p.ReadUInt8(buffer, offset + (int)count) != 0) count++;
        return count;
    }

    private static byte Upper(byte value)
    {
        // Keep the range tests wide: the native compiler's byte comparison
        // can otherwise interpret Latin-1 bounds as signed byte constants.
        var character = (uint)value;
        return (character >= 97 && character <= 122) || (character >= 224 && character <= 246) ||
            (character >= 248 && character <= 254) ? (byte)(character - 32) : value;
    }

    internal static bool Extent<T>(ref T p, uint root, uint key, APTR scratch,
        uint blockCount, uint blockBytes, out uint size, out uint next)
        where T : struct, IAmigaFileSystemPlatform
    {
        size = next = 0;
        var view = ExtentRecordView(ref p, root, key, scratch, blockCount, blockBytes, out _, out var at);
        if (view.IsNull) return false;
        next = p.ReadUInt32(view, at + 4);
        size = p.ReadUInt16(view, at + 12);
        return size != 0;
    }

    internal static APTR ExtentRecordView<T>(ref T p, uint root, uint key, APTR scratch,
        uint blockCount, uint blockBytes, out uint leafBlock, out int offset)
        where T : struct, IAmigaFileSystemPlatform
    {
        leafBlock = 0; offset = 0;
        var block = root;
        for (var depth = 0u; block != 0 && depth < blockCount; depth++)
        {
            var view = View(ref p, block, TreeId, scratch, blockCount);
            if (view.IsNull) return APTR.Null;
            var count = p.ReadUInt16(view, 12); var leaf = p.ReadUInt8(view, 14) != 0;
            var stride = p.ReadUInt8(view, 15);
            if (stride < (leaf ? 14 : 8) || count == 0 || (uint)count * stride > blockBytes - 16) return APTR.Null;
            var low = 0; var high = (int)count;
            while (low < high)
            {
                var middle = (low + high) >> 1;
                if (p.ReadUInt32(view, 16 + middle * stride) <= key) low = middle + 1;
                else high = middle;
            }
            if (low == 0) return APTR.Null;
            var selected = 16 + (low - 1) * stride;
            if (leaf)
            {
                if (p.ReadUInt32(view, selected) != key) return APTR.Null;
                leafBlock = block; offset = selected;
                return view;
            }
            block = p.ReadUInt32(view, selected + 4);
        }
        return APTR.Null;
    }

    public static int Read<T>(ref T p, uint node, uint position, APTR destination, uint length,
        APTR metadata, APTR data, out DOS.Error error) where T : struct, IAmigaFileSystemPlatform
    {
        var cursor = new ReadCursor();
        return Read(ref p, ref cursor, node, position, destination, length, metadata, data, out error);
    }

    /// <summary>Caller-owned file traversal state. Clear Node after any volume
    /// mutation, recovery, or media change before reusing this cursor.</summary>
    public struct ReadCursor
    {
        public uint Node, Size, First, Tree, Extent, Start, Count, Next, BlockBytes, BlockCount;
    }

    public static int Read<T>(ref T p, ref ReadCursor cursor, uint node, uint position, APTR destination,
        uint length, APTR metadata, APTR data, out DOS.Error error) where T : struct, IAmigaFileSystemPlatform
    {
        error = DOS.Error.None;
        if (length > int.MaxValue || !p.IsMapped(destination, length))
        { error = DOS.Error.DiskNotValidated; return -1; }
        if (cursor.Node != node || node == 0)
        {
            cursor.Node = 0;
            var view = ObjectView(ref p, node, metadata, out var at);
            if (view.IsNull) { error = DOS.Error.DiskNotValidated; return -1; }
            if ((p.ReadUInt8(view, at + 24) & (128 | 64)) != 0) { error = DOS.Error.ObjectWrongType; return -1; }
            cursor.Size = p.ReadUInt32(view, at + 16);
            cursor.First = p.ReadUInt32(view, at + 12);
            view = RootView(ref p, metadata);
            if (view.IsNull) { error = DOS.Error.DiskNotValidated; return -1; }
            cursor.Tree = p.ReadUInt32(view, 108);
            cursor.Extent = cursor.First; cursor.Start = 0; cursor.Count = 0; cursor.Next = 0;
            cursor.BlockBytes = p.BlockBytes; cursor.BlockCount = p.BlockCount;
            cursor.Node = node;
        }
        var size = cursor.Size;
        if (position > size) { error = DOS.Error.SeekError; return -1; }
        if (length > size - position) length = size - position;
        if (length == 0) return 0;
        var tree = cursor.Tree;
        var blockBytes = cursor.BlockBytes;
        var blockCount = cursor.BlockCount;
        var fileBlock = position / blockBytes;
        var extent = cursor.Extent; var extentStart = cursor.Start; var extentSize = cursor.Count; var next = cursor.Next;
        if (fileBlock < extentStart) { extent = cursor.First; extentStart = 0; extentSize = 0; next = 0; }
        var copied = 0u;
        while (copied < length)
        {
            while (extentSize == 0 || fileBlock >= extentStart + extentSize)
            {
                if (extentSize != 0) { extentStart += extentSize; extent = next; }
                if (extent == 0 || !Extent(ref p, tree, extent, metadata,
                    blockCount, blockBytes, out extentSize, out next)) goto failed;
            }
            var diskBlock = extent + fileBlock - extentStart;
            var within = position % blockBytes;
            var count = blockBytes - within;
            if (count > length - copied) count = length - copied;
            var target = APTR.FromPointer(destination.Raw + copied);
            if (within == 0 && count == blockBytes && (target.Raw & 3) == 0)
            {
                var blocks = (length - copied) / blockBytes;
                var available = extentStart + extentSize - fileBlock;
                if (blocks > available) blocks = available;
                if (diskBlock >= blockCount || blocks > blockCount - diskBlock) goto failed;
                var read = p.ReadDataBlocks(diskBlock, blocks, target);
                if (read > blocks) goto failed;
                var bytes = read * blockBytes;
                copied += bytes; position += bytes; fileBlock += read;
                if (read != blocks) goto failed;
                continue;
            }
            // ReadBlock owns any device/DMA bounce buffering. A complete,
            // aligned caller range can receive the block without a byte copy.
            var direct = within == 0 && count == blockBytes && (target.Raw & 3) == 0;
            var readTarget = data;
            if (direct) readTarget = target;
            if (diskBlock >= blockCount || !p.ReadBlock(diskBlock, readTarget)) goto failed;
            if (!direct) p.Copy(APTR.FromPointer(data.Raw + within), target, count);
            copied += count; position += count; fileBlock++;
        }
        cursor.Extent = extent; cursor.Start = extentStart; cursor.Count = extentSize; cursor.Next = next;
        return (int)copied;
    failed:
        cursor.Node = 0;
        error = DOS.Error.DiskNotValidated;
        return copied == 0 ? -1 : (int)copied;
    }

    public static bool Examine<T>(ref T p, uint node, APTR fib, APTR metadata) where T : struct, IAmigaFileSystemPlatform
    {
        if (!p.IsMapped(fib, FileInfoBlock.SizeInBytes)) return false;
        var view = ObjectView(ref p, node, metadata, out var at);
        return view.IsNotNull && ExamineObject(ref p, node, fib, view, at, p.BlockBytes);
    }

    private static bool ExamineObject<T>(ref T p, uint node, APTR fib, APTR metadata, int at, uint blockBytes)
        where T : struct, IAmigaFileSystemPlatform
    {
        p.Clear(fib, FileInfoBlock.SizeInBytes);
        var bits = p.ReadUInt8(metadata, at + 24);
        var type = node == RootNode ? 1u : (bits & 64) != 0 ? 3u : (bits & 128) != 0 ? 2u : 0xFFFFFFFD;
        var size = node == RootNode || (bits & 128) == 0 ? p.ReadUInt32(metadata, at + 16) : 0u;
        p.WriteUInt32(fib, 0, node); p.WriteUInt32(fib, 4, type); p.WriteUInt32(fib, 120, type);
        var nameLength = CopyBString(ref p, metadata, at + 25, blockBytes, fib, 8, 107);
        if (nameLength < 0) return false;
        p.WriteUInt32(fib, 116, p.ReadUInt32(metadata, at + 8) ^ 15);
        p.WriteUInt32(fib, 124, size);
        p.WriteUInt32(fib, 128, node != RootNode && (bits & 128) != 0 ? 1u :
            size / blockBytes + (size % blockBytes == 0 ? 0u : 1u));
        var seconds = p.ReadUInt32(metadata, at + 20);
        p.WriteUInt32(fib, 132, seconds / 86400); p.WriteUInt32(fib, 136, (seconds % 86400) / 60);
        p.WriteUInt32(fib, 140, (seconds % 60) * 50);
        var commentAt = at + 26 + nameLength;
        if (CopyBString(ref p, metadata, commentAt, blockBytes, fib, 144, 79) < 0) return false;
        p.WriteUInt16(fib, 224, p.ReadUInt16(metadata, at));
        p.WriteUInt16(fib, 226, p.ReadUInt16(metadata, at + 2));
        return true;
    }

    private static int CopyBString<T>(ref T p, APTR source, int at, uint end, APTR destination, int output, int limit)
        where T : struct, IAmigaFileSystemPlatform
    {
        for (var length = 0; length <= limit && at + length < end; length++)
        {
            var value = p.ReadUInt8(source, at + length);
            if (value == 0) { p.WriteUInt8(destination, output, (byte)length); return length; }
            if (length == limit) break;
            p.WriteUInt8(destination, output + 1 + length, value);
        }
        return -1;
    }

    public static DOS.Error ExamineNext<T>(ref T p, uint directory, APTR fib, APTR metadata, APTR data)
        where T : struct, IAmigaFileSystemPlatform
    {
        if (!p.IsMapped(fib, FileInfoBlock.SizeInBytes)) return DOS.Error.InvalidLock;
        var view = ObjectView(ref p, directory, metadata, out var at);
        if (view.IsNull) return DOS.Error.InvalidLock;
        if ((p.ReadUInt8(view, at + 24) & 128) == 0) return DOS.Error.ObjectWrongType;
        var blockCount = p.BlockCount; var blockBytes = p.BlockBytes;
        var wanted = p.ReadUInt32(fib);
        var next = p.ReadUInt32(view, at + 16);
        var seen = wanted == directory;
        for (var visited = 0u; next != 0 && visited < blockCount; visited++)
        {
            view = View(ref p, next, ObjectId, metadata, blockCount);
            if (view.IsNull || p.ReadUInt32(view, 12) != directory) return DOS.Error.DiskNotValidated;
            var candidate = 0u;
            var candidateOffset = 0;
            var foundWanted = false;
            for (var pos = 24; pos + 27 <= blockBytes;)
            {
                if (p.ReadUInt8(view, pos + 25) == 0) break;
                var node = p.ReadUInt32(view, pos + 4);
                if (!ObjectEnd(ref p, view, pos, (int)blockBytes, out var following)) return DOS.Error.DiskNotValidated;
                if (node == wanted)
                {
                    foundWanted = true;
                    break;
                }
                if ((p.ReadUInt8(view, pos + 24) & 1) == 0) { candidate = node; candidateOffset = pos; }
                pos = following;
            }
            if ((seen || foundWanted) && candidate != 0)
                return ExamineObject(ref p, candidate, fib, view, candidateOffset, blockBytes)
                    ? DOS.Error.None : DOS.Error.DiskNotValidated;
            if (foundWanted) seen = true;
            next = p.ReadUInt32(view, 16);
        }
        return next != 0 ? DOS.Error.DiskNotValidated : seen ? DOS.Error.NoMoreEntries : DOS.Error.DiskNotValidated;
    }

    public static bool CountUsed<T>(ref T p, APTR buffer, out uint used) where T : struct, IAmigaFileSystemPlatform
    {
        used = 0;
        var view = RootView(ref p, buffer);
        if (view.IsNull) return false;
        var blockCount = p.BlockCount; var blockBytes = p.BlockBytes;
        var rootContainer = p.ReadUInt32(view, 104);
        view = View(ref p, rootContainer, ObjectId, buffer, blockCount);
        if (view.IsNull) return false;
        var free = p.ReadUInt32(view, (int)blockBytes - 28);
        var reserved = p.ReservedBlocks + 1; // SFS keeps one root at the end.
        if (reserved > blockCount || free > blockCount - reserved) return false;
        used = blockCount - reserved - free;
        return true;
    }
}
