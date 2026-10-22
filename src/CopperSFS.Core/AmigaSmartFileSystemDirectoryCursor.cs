using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemCore
{
    /// <summary>Optional private enumeration cache. Clear Wanted after mutations
    /// or media changes. FIB tokens remain freely copyable; a mismatched token
    /// uses the ordinary lookup. Entries supplies Capacity longword offsets.</summary>
    public struct DirectoryCursor
    {
        public uint Directory, Wanted, Container, Remaining, Visited, BlockBytes, BlockCount, Capacity;
        public APTR Entries;
    }

    public static DOS.Error ExamineNext<T>(ref T p, ref DirectoryCursor cursor, uint directory,
        APTR fib, APTR metadata, APTR data) where T : struct, IAmigaFileSystemPlatform
    {
        if (cursor.Entries.IsNull || cursor.Capacity == 0)
            return ExamineNext(ref p, directory, fib, metadata, data);
        if (!p.IsMapped(fib, FileInfoBlock.SizeInBytes)) return DOS.Error.InvalidLock;
        var wanted = p.ReadUInt32(fib);
        if (cursor.Wanted == 0 || cursor.Wanted != wanted || cursor.Directory != directory)
        {
            cursor.Wanted = 0;
            if (wanted == directory)
            {
                var initial = ObjectView(ref p, directory, metadata, out var at);
                if (initial.IsNull) return DOS.Error.InvalidLock;
                if ((p.ReadUInt8(initial, at + 24) & 128) == 0) return DOS.Error.ObjectWrongType;
                var first = p.ReadUInt32(initial, at + 16);

                cursor.BlockBytes = p.BlockBytes; cursor.BlockCount = p.BlockCount;
                cursor.Directory = directory; cursor.Container = first; cursor.Visited = 1;
                cursor.Remaining = 0; cursor.Wanted = wanted;
                if (first == 0) return DOS.Error.NoMoreEntries;
                initial = View(ref p, first, ObjectId, metadata, cursor.BlockCount);
                if (initial.IsNull || p.ReadUInt32(initial, 12) != directory ||
                    !IndexDirectory(ref p, ref cursor, initial, cursor.BlockBytes)) return DOS.Error.DiskNotValidated;
                if (cursor.Remaining > cursor.Capacity)
                {
                    cursor.Wanted = 0;
                    return ExamineNext(ref p, directory, fib, metadata, data);
                }
            }
            else
            {
                var result = ExamineNext(ref p, directory, fib, metadata, data);
                if (result != DOS.Error.None) return result;
                var found = p.ReadUInt32(fib);
                var initial = ObjectView(ref p, found, metadata, out var at);
                if (initial.IsNull) return DOS.Error.DiskNotValidated;

                cursor.BlockBytes = p.BlockBytes; cursor.BlockCount = p.BlockCount;
                cursor.Directory = directory; cursor.Container = p.ReadUInt32(initial, 8); cursor.Visited = 1;
                if (!IndexDirectory(ref p, ref cursor, initial, (uint)at)) return DOS.Error.DiskNotValidated;
                if (cursor.Remaining <= cursor.Capacity) cursor.Wanted = found;
                return DOS.Error.None;
            }
        }
        if (cursor.Container == 0) return DOS.Error.NoMoreEntries;
        var view = View(ref p, cursor.Container, ObjectId, metadata, cursor.BlockCount);
        if (view.IsNull || p.ReadUInt32(view, 12) != directory) return DOS.Error.DiskNotValidated;
        while (cursor.Remaining == 0)
        {
            cursor.Container = p.ReadUInt32(view, 16);
            if (cursor.Container == 0) return DOS.Error.NoMoreEntries;
            if (++cursor.Visited > cursor.BlockCount) return DOS.Error.DiskNotValidated;
            view = View(ref p, cursor.Container, ObjectId, metadata, cursor.BlockCount);
            if (view.IsNull || p.ReadUInt32(view, 12) != directory) return DOS.Error.DiskNotValidated;
            if (!IndexDirectory(ref p, ref cursor, view, cursor.BlockBytes)) return DOS.Error.DiskNotValidated;
            if (cursor.Remaining > cursor.Capacity)
            {
                cursor.Wanted = 0;
                return ExamineNext(ref p, directory, fib, metadata, data);
            }
        }
        var offset = p.ReadUInt32(cursor.Entries, (int)(--cursor.Remaining * 4));
        var node = p.ReadUInt32(view, (int)offset + 4);
        if (!ExamineObject(ref p, node, fib, view, (int)offset, cursor.BlockBytes)) return DOS.Error.DiskNotValidated;
        cursor.Wanted = node;
        return DOS.Error.None;
    }

    private static bool IndexDirectory<T>(ref T p, ref DirectoryCursor cursor, APTR metadata, uint limit)
        where T : struct, IAmigaFileSystemPlatform
    {
        cursor.Remaining = 0;
        for (var at = 24; at < limit && at + 27 <= cursor.BlockBytes;)
        {
            if (p.ReadUInt8(metadata, at + 25) == 0) break;
            if (!ObjectEnd(ref p, metadata, at, (int)cursor.BlockBytes, out var next)) return false;
            if ((p.ReadUInt8(metadata, at + 24) & 1) == 0)
            {
                if (cursor.Remaining == cursor.Capacity) { cursor.Remaining++; return true; }
                p.WriteUInt32(cursor.Entries, (int)(cursor.Remaining++ * 4), (uint)at);
            }
            at = next;
        }
        return true;
    }
}
