using Amiga;

namespace CopperStart.Dos;

/// <summary>
/// Allocation-free writes to classic SFS volumes. A write-ahead undo chain
/// records each old block before it is changed. The caller supplies four
/// block-sized scratch buffers.
/// </summary>
public static partial class AmigaSmartFileSystemWriteCore
{
    private const uint ObjectId = 0x4F424A43, NodeId = 0x4E444320, TreeId = 0x424E4443;
    private const uint BitmapId = 0x42544D50, AdminId = 0x41444D43, HashId = 0x48544142;
    private const uint TransactionOk = 0x54524F4B, TransactionFailed = 0x54524641;
    private const uint UndoId = 0x4353554E; // CSUN; distinct from original SFS redo records (TRST).
    private const uint NativeRedoId = 0x54525354;

    public struct Work
    {
        public APTR Meta, Data, Bitmap, Journal;
        public uint RootContainer, BitmapBase, AdminContainer, NodeRoot, ExtentRoot;
        public uint JournalHead, JournalCursor;
        public int FreeDelta;
        public bool CaseSensitive, PreserveBitmap, JournalActive;
        public DOS.Error Error;
    }

    public struct Request
    {
        public uint Directory, Node, Position, Length;
        public APTR Name, Buffer;
    }

    private static bool Fail(ref Work w, DOS.Error error) { w.Error = error; return false; }

    private static void Seal<T>(ref T p, APTR source) where T : struct, IAmigaFileSystemPlatform
    {
        p.WriteUInt32(source, 4, 0);
        uint sum = 1;
        for (var at = 0; at < p.Geometry.BlockBytes; at += 4)
            sum = unchecked(sum + p.ReadUInt32(source, (int)at));
        p.WriteUInt32(source, 4, unchecked(0u - sum));
    }

    private static uint BlockSum<T>(ref T p, APTR source) where T : struct, IAmigaFileSystemPlatform
    {
        uint sum = 0;
        for (var at = 0; at < p.Geometry.BlockBytes; at += 4)
            sum = unchecked(sum + p.ReadUInt32(source, (int)at));
        return sum;
    }

    private static bool Put<T>(ref T p, ref Work w, uint block, APTR source)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Error != DOS.Error.None) return false;
        Seal(ref p, source);
        return (w.JournalActive ? JournalWrite(ref p, ref w, block, source) : p.WriteBlock(block, source)) ||
            Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool JournalContains<T>(ref T p, ref Work w, uint candidate, out bool found)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        found = false;
        var entry = w.JournalHead;
        for (var guard = 0u; entry != 0 && guard < p.Geometry.BlockCount; guard++)
        {
            if (!AmigaSmartFileSystemCore.Block(ref p, entry, UndoId, w.Journal))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            if (candidate == entry || candidate == p.ReadUInt32(w.Journal, 20))
            { found = true; return true; }
            entry = p.ReadUInt32(w.Journal, 12);
        }
        return entry == 0 || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool JournalHasTarget<T>(ref T p, ref Work w, uint target, out bool found)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        found = false;
        var entry = w.JournalHead;
        for (var guard = 0u; entry != 0 && guard < p.Geometry.BlockCount; guard++)
        {
            if (!AmigaSmartFileSystemCore.Block(ref p, entry, UndoId, w.Journal))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            if (p.ReadUInt32(w.Journal, 16) == target) { found = true; return true; }
            entry = p.ReadUInt32(w.Journal, 12);
        }
        return entry == 0 || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool OriginallyFree<T>(ref T p, ref Work w, uint map, int offset,
        uint mask, out bool free) where T : struct, IAmigaWritableFileSystemPlatform
    {
        free = true;
        var entry = w.JournalHead;
        for (var guard = 0u; entry != 0 && guard < p.Geometry.BlockCount; guard++)
        {
            if (!AmigaSmartFileSystemCore.Block(ref p, entry, UndoId, w.Journal))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            var next = p.ReadUInt32(w.Journal, 12);
            if (p.ReadUInt32(w.Journal, 16) == map)
            {
                var image = p.ReadUInt32(w.Journal, 20);
                if (image >= p.Geometry.BlockCount || !p.ReadBlock(image, w.Journal))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                free = (p.ReadUInt32(w.Journal, offset) & mask) != 0;
                return true;
            }
            entry = next;
        }
        return entry == 0 || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static uint JournalSpace<T>(ref T p, ref Work w, uint target, APTR source, uint avoid)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var low = p.Geometry.ReservedBlocks;
        var high = p.Geometry.BlockCount - 1;
        if (w.JournalCursor < low || w.JournalCursor >= high) w.JournalCursor = low;
        for (var scan = 0u; scan < high - low; scan++)
        {
            var candidate = w.JournalCursor++;
            if (w.JournalCursor >= high) w.JournalCursor = low;
            if (candidate == target || candidate == avoid || candidate == w.RootContainer + 2) continue;
            if (!BitmapPosition(ref p, ref w, candidate, out var map, out var offset, out var mask)) return 0;
            if (map != target && !Read(ref p, ref w, map, BitmapId, w.Journal)) return 0;
            var bitmapSource = w.Journal;
            if (map == target) bitmapSource = source;
            if ((p.ReadUInt32(bitmapSource, offset) & mask) == 0) continue;
            if (!JournalContains(ref p, ref w, candidate, out var inUse)) return 0;
            if (inUse) continue;
            if (!OriginallyFree(ref p, ref w, map, offset, mask, out var originallyFree)) return 0;
            if (originallyFree) return candidate;
        }
        Fail(ref w, DOS.Error.DiskFull); return 0;
    }

    private static bool JournalWrite<T>(ref T p, ref Work w, uint target, APTR source)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!JournalHasTarget(ref p, ref w, target, out var alreadyLogged)) return false;
        if (alreadyLogged) return p.WriteBlock(target, source) || Fail(ref w, DOS.Error.DiskNotValidated);
        var image = JournalSpace(ref p, ref w, target, source, 0);
        if (image == 0) return false;
        var entry = JournalSpace(ref p, ref w, target, source, image);
        if (entry == 0 || !p.ReadBlock(target, w.Journal))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var originalSum = BlockSum(ref p, w.Journal);
        if (!p.WriteBlock(image, w.Journal)) return Fail(ref w, DOS.Error.DiskNotValidated);
        p.Clear(w.Journal, p.Geometry.BlockBytes);
        p.WriteUInt32(w.Journal, 0, UndoId); p.WriteUInt32(w.Journal, 8, entry);
        p.WriteUInt32(w.Journal, 12, w.JournalHead);
        p.WriteUInt32(w.Journal, 16, target); p.WriteUInt32(w.Journal, 20, image);
        p.WriteUInt32(w.Journal, 24, originalSum);
        Seal(ref p, w.Journal);
        if (!p.WriteBlock(entry, w.Journal) || !p.Flush())
            return Fail(ref w, DOS.Error.DiskNotValidated);
        p.Clear(w.Journal, p.Geometry.BlockBytes);
        p.WriteUInt32(w.Journal, 0, TransactionFailed);
        p.WriteUInt32(w.Journal, 8, w.RootContainer + 2);
        p.WriteUInt32(w.Journal, 12, entry);
        Seal(ref p, w.Journal);
        if (!p.WriteBlock(w.RootContainer + 2, w.Journal) || !p.Flush())
            return Fail(ref w, DOS.Error.DiskNotValidated);
        w.JournalHead = entry;
        return p.WriteBlock(target, source) || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    public static bool Recover<T>(ref T p, ref Work w)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        w.Error = DOS.Error.None;
        if (!AmigaSmartFileSystemCore.Root(ref p, w.Meta))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var marker = p.ReadUInt32(w.Meta, 104) + 2;
        if (marker >= p.Geometry.BlockCount || !p.ReadBlock(marker, w.Meta) ||
            p.ReadUInt32(w.Meta, 8) != marker || !AmigaSmartFileSystemCore.ValidChecksum(ref p, w.Meta))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (p.ReadUInt32(w.Meta) == TransactionOk) return true;
        if (p.ReadUInt32(w.Meta) != TransactionFailed || p.WriteProtected() ||
            w.Journal.IsNull || !p.IsMapped(w.Journal, p.Geometry.BlockBytes))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var entry = p.ReadUInt32(w.Meta, 12);
        if (entry != 0)
        {
            if (entry >= p.Geometry.BlockCount || !p.ReadBlock(entry, w.Journal))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            if (p.ReadUInt32(w.Journal) == NativeRedoId)
                return ReplayNative(ref p, ref w, marker, entry);
        }
        for (var guard = 0u; entry != 0 && guard < p.Geometry.BlockCount; guard++)
        {
            if (!AmigaSmartFileSystemCore.Block(ref p, entry, UndoId, w.Journal))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            var next = p.ReadUInt32(w.Journal, 12);
            var target = p.ReadUInt32(w.Journal, 16);
            var image = p.ReadUInt32(w.Journal, 20);
            var originalSum = p.ReadUInt32(w.Journal, 24);
            if (target < p.Geometry.ReservedBlocks || target >= p.Geometry.BlockCount - 1 ||
                target == marker || image < p.Geometry.ReservedBlocks ||
                image >= p.Geometry.BlockCount - 1 || image == entry ||
                !p.ReadBlock(image, w.Data) || BlockSum(ref p, w.Data) != originalSum ||
                !p.WriteBlock(target, w.Data))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            entry = next;
        }
        if (entry != 0 || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        p.Clear(w.Meta, p.Geometry.BlockBytes);
        p.WriteUInt32(w.Meta, 0, TransactionOk); p.WriteUInt32(w.Meta, 8, marker);
        Seal(ref p, w.Meta);
        return (p.WriteBlock(marker, w.Meta) && p.Flush()) || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool Read<T>(ref T p, ref Work w, uint block, uint id, APTR destination)
        where T : struct, IAmigaWritableFileSystemPlatform =>
        AmigaSmartFileSystemCore.Block(ref p, block, id, destination) || Fail(ref w, DOS.Error.DiskNotValidated);

    private static bool Prepare<T>(ref T p, ref Work w)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        w.Error = DOS.Error.None; w.FreeDelta = 0; w.PreserveBitmap = false;
        w.JournalActive = false; w.JournalHead = 0;
        if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);
        if (!Recover(ref p, ref w)) return false;
        if (!AmigaSmartFileSystemCore.Mount(ref p, w.Meta) || !AmigaSmartFileSystemCore.Root(ref p, w.Meta))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        w.BitmapBase = p.ReadUInt32(w.Meta, 96); w.AdminContainer = p.ReadUInt32(w.Meta, 100);
        w.RootContainer = p.ReadUInt32(w.Meta, 104); w.ExtentRoot = p.ReadUInt32(w.Meta, 108);
        w.NodeRoot = p.ReadUInt32(w.Meta, 112); w.CaseSensitive = (p.ReadUInt8(w.Meta, 20) & 128) != 0;
        if (w.BitmapBase == 0 || w.AdminContainer == 0 || w.RootContainer == 0 ||
            w.ExtentRoot == 0 || w.NodeRoot == 0 ||
            !Read(ref p, ref w, w.RootContainer + 2, TransactionOk, w.Meta))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        return true;
    }

    private static bool Marker<T>(ref T p, ref Work w, uint id)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        p.Clear(w.Meta, p.Geometry.BlockBytes);
        p.WriteUInt32(w.Meta, 0, id); p.WriteUInt32(w.Meta, 8, w.RootContainer + 2);
        if (id == TransactionFailed) p.WriteUInt32(w.Meta, 12, w.JournalHead);
        return Put(ref p, ref w, w.RootContainer + 2, w.Meta);
    }

    private static bool Begin<T>(ref T p, ref Work w) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Journal.IsNull || !p.IsMapped(w.Journal, p.Geometry.BlockBytes))
            return Fail(ref w, DOS.Error.NoFreeStore);
        if (!Marker(ref p, ref w, TransactionFailed) || !p.Flush())
            return Fail(ref w, DOS.Error.DiskNotValidated);
        w.JournalActive = true;
        return true;
    }

    private static bool Finish<T>(ref T p, ref Work w) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Error != DOS.Error.None) return false;
        if (w.FreeDelta != 0)
        {
            if (!Read(ref p, ref w, w.RootContainer, ObjectId, w.Meta)) return false;
            var oldFree = p.ReadUInt32(w.Meta, (int)p.Geometry.BlockBytes - 28);
            var updated = (long)oldFree + w.FreeDelta;
            if (updated < 0 || updated > p.Geometry.BlockCount) return Fail(ref w, DOS.Error.DiskNotValidated);
            p.WriteUInt32(w.Meta, (int)p.Geometry.BlockBytes - 28, (uint)updated);
            if (!Put(ref p, ref w, w.RootContainer, w.Meta)) return false;
        }
        if (!p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        w.JournalActive = false;
        if (!Marker(ref p, ref w, TransactionOk) || !p.Flush())
            return Fail(ref w, DOS.Error.DiskNotValidated);
        w.JournalHead = 0;
        return true;
    }

    private static bool BitmapPosition<T>(ref T p, ref Work w, uint block, out uint bitmapBlock, out int offset, out uint mask)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        bitmapBlock = 0; offset = 0; mask = 0;
        if (block < p.Geometry.ReservedBlocks || block >= p.Geometry.BlockCount - 1)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var bits = (p.Geometry.BlockBytes - 12) * 8;
        bitmapBlock = w.BitmapBase + block / bits;
        var index = block % bits;
        offset = 12 + (int)(index / 32) * 4;
        mask = 1u << (int)(31 - index % 32);
        return bitmapBlock < p.Geometry.BlockCount || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool IsFree<T>(ref T p, ref Work w, uint block, out bool free)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        free = false;
        if (!BitmapPosition(ref p, ref w, block, out var map, out var offset, out var mask) ||
            !Read(ref p, ref w, map, BitmapId, w.Bitmap)) return false;
        free = (p.ReadUInt32(w.Bitmap, offset) & mask) != 0;
        if (free && w.JournalActive)
        {
            if (!JournalContains(ref p, ref w, block, out var inUse)) return false;
            free = !inUse;
            if (free && !OriginallyFree(ref p, ref w, map, offset, mask, out free)) return false;
        }
        return true;
    }

    private static bool Mark<T>(ref T p, ref Work w, uint block, bool free)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!BitmapPosition(ref p, ref w, block, out var map, out var offset, out var mask) ||
            !Read(ref p, ref w, map, BitmapId, w.Bitmap)) return false;
        var old = p.ReadUInt32(w.Bitmap, offset);
        if (((old & mask) != 0) == free) return Fail(ref w, DOS.Error.DiskNotValidated);
        p.WriteUInt32(w.Bitmap, offset, free ? old | mask : old & ~mask);
        if (!Put(ref p, ref w, map, w.Bitmap)) return false;
        w.FreeDelta += free ? 1 : -1;
        return true;
    }

    private static uint AllocateData<T>(ref T p, ref Work w, uint preferred)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (preferred >= p.Geometry.ReservedBlocks && preferred < p.Geometry.BlockCount - 1 &&
            IsFree(ref p, ref w, preferred, out var free) && free)
            return Mark(ref p, ref w, preferred, false) ? preferred : 0;
        for (var block = p.Geometry.ReservedBlocks; block < p.Geometry.BlockCount - 1; block++)
        {
            if (!IsFree(ref p, ref w, block, out free)) return 0;
            if (free) return Mark(ref p, ref w, block, false) ? block : 0;
        }
        Fail(ref w, DOS.Error.DiskFull); return 0;
    }

    private static uint AllocateAdmin<T>(ref T p, ref Work w)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var container = w.AdminContainer;
        var tail = 0u; var emptyContainer = 0u; var emptyOffset = 0;
        for (var guard = 0u; container != 0 && guard < p.Geometry.BlockCount; guard++)
        {
            if (!Read(ref p, ref w, container, AdminId, w.Meta)) return 0;
            for (var at = 24; at + 8 <= p.Geometry.BlockBytes; at += 8)
            {
                var baseBlock = p.ReadUInt32(w.Meta, at);
                var used = p.ReadUInt32(w.Meta, at + 4);
                if (baseBlock == 0 && emptyContainer == 0) { emptyContainer = container; emptyOffset = at; }
                if (baseBlock == 0 || used == uint.MaxValue) continue;
                for (var bit = 0; bit < 32; bit++)
                {
                    var mask = 1u << (31 - bit);
                    if ((used & mask) != 0) continue;
                    var allocated = baseBlock + (uint)bit;
                    if (allocated >= p.Geometry.BlockCount - 1) { Fail(ref w, DOS.Error.DiskNotValidated); return 0; }
                    p.WriteUInt32(w.Meta, at + 4, used | mask);
                    return Put(ref p, ref w, container, w.Meta) ? allocated : 0;
                }
            }
            tail = container;
            container = p.ReadUInt32(w.Meta, 12);
        }
        if (container != 0) { Fail(ref w, DOS.Error.DiskNotValidated); return 0; }
        if (w.PreserveBitmap) { Fail(ref w, DOS.Error.DiskFull); return 0; }
        if (p.Geometry.BlockCount < 34) { Fail(ref w, DOS.Error.DiskFull); return 0; }
        uint span = 0;
        for (var candidate = p.Geometry.ReservedBlocks; candidate <= p.Geometry.BlockCount - 33; candidate++)
        {
            var found = true;
            for (var i = 0u; i < 32; i++)
            {
                if (!IsFree(ref p, ref w, candidate + i, out var free)) return 0;
                if (!free) { found = false; candidate += i; break; }
            }
            if (found) { span = candidate; break; }
        }
        if (span == 0) { Fail(ref w, DOS.Error.DiskFull); return 0; }
        for (var i = 0u; i < 32; i++) if (!Mark(ref p, ref w, span + i, false)) return 0;
        if (emptyContainer != 0)
        {
            if (!Read(ref p, ref w, emptyContainer, AdminId, w.Meta)) return 0;
            p.WriteUInt32(w.Meta, emptyOffset, span);
            p.WriteUInt32(w.Meta, emptyOffset + 4, 0x80000000);
            return Put(ref p, ref w, emptyContainer, w.Meta) ? span : 0;
        }
        if (tail == 0 || !Read(ref p, ref w, tail, AdminId, w.Meta)) return 0;
        p.WriteUInt32(w.Meta, 12, span);
        if (!Put(ref p, ref w, tail, w.Meta)) return 0;
        p.Clear(w.Meta, p.Geometry.BlockBytes);
        p.WriteUInt32(w.Meta, 0, AdminId); p.WriteUInt32(w.Meta, 8, span);
        p.WriteUInt32(w.Meta, 16, tail); p.WriteUInt8(w.Meta, 20, 32);
        p.WriteUInt32(w.Meta, 24, span); p.WriteUInt32(w.Meta, 28, 0xC0000000);
        return Put(ref p, ref w, span, w.Meta) ? span + 1 : 0;
    }

    private static ushort Hash<T>(ref T p, ref Work w, APTR name, uint length)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        ushort hash = (ushort)length;
        for (var i = 0u; i < length; i++)
        {
            var value = p.ReadUInt8(name, (int)i);
            if (!w.CaseSensitive && (value is >= 97 and <= 122 or >= 224 and <= 246 or >= 248 and <= 254)) value -= 32;
            hash = unchecked((ushort)(hash * 13 + value));
        }
        return hash;
    }

    private static bool Parent<T>(ref T p, ref Work w, ref Request r)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (r.Length == 0 || r.Length > 255 || !p.IsMapped(r.Name, r.Length))
            return Fail(ref w, DOS.Error.InvalidComponentName);
        var segment = 0u;
        for (var i = 0u; i < r.Length; i++)
            if (p.ReadUInt8(r.Name, (int)i) is (byte)'/' or (byte)':') segment = i + 1;
        if (segment != 0)
        {
            var parent = AmigaSmartFileSystemCore.Resolve(ref p, r.Directory, r.Name, segment - 1, w.Meta, out var error);
            if (parent == 0) return Fail(ref w, error);
            r.Directory = parent;
            r.Name = APTR.FromPointer(r.Name.Raw + segment); r.Length -= segment;
        }
        if (r.Length == 0 || r.Length > 100) return Fail(ref w, DOS.Error.InvalidComponentName);
        for (var i = 0u; i < r.Length; i++)
        {
            var value = p.ReadUInt8(r.Name, (int)i);
            if (value < 32 || value == ':' || value == '/') return Fail(ref w, DOS.Error.InvalidComponentName);
        }
        if (!AmigaSmartFileSystemCore.Describe(ref p, r.Directory, w.Meta, out _, out var directory, out _))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        return directory || Fail(ref w, DOS.Error.ObjectWrongType);
    }

    private static bool ContainerEnd<T>(ref T p, ref Work w, APTR buffer, out int end)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        end = 24;
        while (end + 27 <= p.Geometry.BlockBytes)
        {
            if (p.ReadUInt32(buffer, end + 4) == 0) return true;
            var nameEnd = end + 25;
            while (nameEnd < p.Geometry.BlockBytes && p.ReadUInt8(buffer, nameEnd) != 0) nameEnd++;
            if (nameEnd >= p.Geometry.BlockBytes) break;
            var commentEnd = nameEnd + 1;
            while (commentEnd < p.Geometry.BlockBytes && p.ReadUInt8(buffer, commentEnd) != 0) commentEnd++;
            if (commentEnd >= p.Geometry.BlockBytes) break;
            var next = (commentEnd + 2) & ~1;
            if (next <= end) break;
            end = next;
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool FindFreeNodeIn<T>(ref T p, ref Work w, uint block, uint depth,
        out uint node, out uint leaf, out int slot)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        node = leaf = 0; slot = 0;
        if (depth >= 8 || !Read(ref p, ref w, block, NodeId, w.Meta)) return false;
        var first = p.ReadUInt32(w.Meta, 12);
        var stride = p.ReadUInt32(w.Meta, 16);
        if (stride == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (stride == 1)
        {
            for (var at = 20; at + 10 <= p.Geometry.BlockBytes; at += 10)
            {
                var candidate = first + (uint)((at - 20) / 10);
                if (candidate == 0 || p.ReadUInt32(w.Meta, at) != 0) continue;
                node = candidate; leaf = block; slot = at; return true;
            }
            return false;
        }
        var shift = 0;
        while ((1u << shift) < p.Geometry.BlockBytes) shift++;
        shift -= 5;
        for (var index = 0; index < (p.Geometry.BlockBytes - 20) / 4; index++)
        {
            if (!Read(ref p, ref w, block, NodeId, w.Meta)) return false;
            var pointer = p.ReadUInt32(w.Meta, 20 + (int)index * 4);
            if (pointer == 0) continue;
            if (FindFreeNodeIn(ref p, ref w, pointer >> shift, depth + 1, out node, out leaf, out slot)) return true;
            if (w.Error != DOS.Error.None) return false;
        }
        return false;
    }

    private static bool FindFreeNode<T>(ref T p, ref Work w, out uint node, out uint leaf, out int slot)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (FindFreeNodeIn(ref p, ref w, w.NodeRoot, 0, out node, out leaf, out slot)) return true;
        if (w.Error != DOS.Error.None) return false;
        return Fail(ref w, DOS.Error.DiskFull);
    }

    private static bool InitializeNodeBranch<T>(ref T p, ref Work w, uint block, uint first, uint stride, uint depth)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (depth >= 8 || stride == 0) return Fail(ref w, DOS.Error.DiskFull);
        p.Clear(w.Meta, p.Geometry.BlockBytes);
        p.WriteUInt32(w.Meta, 0, NodeId); p.WriteUInt32(w.Meta, 8, block);
        p.WriteUInt32(w.Meta, 12, first); p.WriteUInt32(w.Meta, 16, stride);
        if (!Put(ref p, ref w, block, w.Meta)) return false;
        if (stride == 1) return true;
        var leafCapacity = (p.Geometry.BlockBytes - 20) / 10;
        var indexCapacity = (p.Geometry.BlockBytes - 20) / 4;
        var childStride = stride == leafCapacity ? 1u : stride / indexCapacity;
        if (childStride == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        var child = AllocateAdmin(ref p, ref w);
        if (child == 0 || !InitializeNodeBranch(ref p, ref w, child, first, childStride, depth + 1)) return false;
        if (!Read(ref p, ref w, block, NodeId, w.Meta)) return false;
        var shift = 0;
        while ((1u << shift) < p.Geometry.BlockBytes) shift++;
        shift -= 5;
        if (child > uint.MaxValue >> shift) return Fail(ref w, DOS.Error.DiskFull);
        p.WriteUInt32(w.Meta, 20, child << shift);
        return Put(ref p, ref w, block, w.Meta);
    }

    private static bool GrowNodeRoot<T>(ref T p, ref Work w)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!Read(ref p, ref w, w.NodeRoot, NodeId, w.Data)) return false;
        var first = p.ReadUInt32(w.Data, 12); var oldStride = p.ReadUInt32(w.Data, 16);
        var leafCapacity = (p.Geometry.BlockBytes - 20) / 10;
        var indexCapacity = (p.Geometry.BlockBytes - 20) / 4;
        if (oldStride > uint.MaxValue / indexCapacity) return Fail(ref w, DOS.Error.DiskFull);
        var newStride = oldStride == 1 ? leafCapacity : oldStride * indexCapacity;
        if (newStride == 0 || newStride <= oldStride || first > uint.MaxValue - newStride)
            return Fail(ref w, DOS.Error.DiskFull);
        var saved = AllocateAdmin(ref p, ref w);
        if (saved == 0 || !Read(ref p, ref w, w.NodeRoot, NodeId, w.Data)) return false;
        p.WriteUInt32(w.Data, 8, saved);
        if (!Put(ref p, ref w, saved, w.Data)) return false;
        var branch = AllocateAdmin(ref p, ref w);
        if (branch == 0 || !InitializeNodeBranch(ref p, ref w, branch, first + newStride, oldStride, 0)) return false;
        var shift = 0;
        while ((1u << shift) < p.Geometry.BlockBytes) shift++;
        shift -= 5;
        if (saved > uint.MaxValue >> shift || branch > uint.MaxValue >> shift)
            return Fail(ref w, DOS.Error.DiskFull);
        p.Clear(w.Meta, p.Geometry.BlockBytes);
        p.WriteUInt32(w.Meta, 0, NodeId); p.WriteUInt32(w.Meta, 8, w.NodeRoot);
        p.WriteUInt32(w.Meta, 12, first); p.WriteUInt32(w.Meta, 16, newStride);
        p.WriteUInt32(w.Meta, 20, (saved << shift) | 1);
        p.WriteUInt32(w.Meta, 24, branch << shift);
        return Put(ref p, ref w, w.NodeRoot, w.Meta);
    }

    private static bool InsertObject<T>(ref T p, ref Work w, ref Request r, bool directory, uint node,
        APTR preserved, int preservedAt, int commentAt, uint commentLength,
        out uint container, out uint hashTable)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        container = hashTable = 0;
        if (!AmigaSmartFileSystemCore.Object(ref p, r.Directory, w.Meta, out var parentAt))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        hashTable = p.ReadUInt32(w.Meta, parentAt + 12);
        var first = p.ReadUInt32(w.Meta, parentAt + 16);
        var entryBytes = ((int)r.Length + (int)commentLength + 27 + 1) & ~1;
        if (first == 0)
        {
            first = AllocateAdmin(ref p, ref w);
            if (first == 0) return false;
            if (!AmigaSmartFileSystemCore.Object(ref p, r.Directory, w.Meta, out parentAt))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            p.WriteUInt32(w.Meta, parentAt + 16, first);
            if (!Put(ref p, ref w, p.ReadUInt32(w.Meta, 8), w.Meta)) return false;
            p.Clear(w.Meta, p.Geometry.BlockBytes);
            p.WriteUInt32(w.Meta, 0, ObjectId); p.WriteUInt32(w.Meta, 8, first);
            p.WriteUInt32(w.Meta, 12, r.Directory);
            container = first;
        }
        else
        {
            var current = first;
            for (var guard = 0u; guard < p.Geometry.BlockCount; guard++)
            {
                if (!Read(ref p, ref w, current, ObjectId, w.Meta) ||
                    p.ReadUInt32(w.Meta, 12) != r.Directory || !ContainerEnd(ref p, ref w, w.Meta, out var end)) return false;
                if (end + entryBytes + 27 <= p.Geometry.BlockBytes) { container = current; break; }
                var next = p.ReadUInt32(w.Meta, 16);
                if (next != 0) { current = next; continue; }
                var allocated = AllocateAdmin(ref p, ref w);
                if (allocated == 0 || !Read(ref p, ref w, current, ObjectId, w.Meta)) return false;
                p.WriteUInt32(w.Meta, 16, allocated);
                if (!Put(ref p, ref w, current, w.Meta)) return false;
                p.Clear(w.Meta, p.Geometry.BlockBytes);
                p.WriteUInt32(w.Meta, 0, ObjectId); p.WriteUInt32(w.Meta, 8, allocated);
                p.WriteUInt32(w.Meta, 12, r.Directory); p.WriteUInt32(w.Meta, 20, current);
                container = allocated; break;
            }
            if (container == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        if (!ContainerEnd(ref p, ref w, w.Meta, out var at)) return false;
        if (at + entryBytes > p.Geometry.BlockBytes) return Fail(ref w, DOS.Error.DiskFull);
        for (var i = 0; i < entryBytes; i++) p.WriteUInt8(w.Meta, at + i, 0);
        if (preserved.IsNotNull)
            for (var i = 0; i < 25; i++) p.WriteUInt8(w.Meta, at + i, p.ReadUInt8(preserved, preservedAt + i));
        else p.WriteUInt32(w.Meta, at + 8, 15);
        p.WriteUInt32(w.Meta, at + 4, node);
        if (preserved.IsNull) p.WriteUInt8(w.Meta, at + 24, directory ? (byte)128 : (byte)0);
        for (var i = 0u; i < r.Length; i++) p.WriteUInt8(w.Meta, at + 25 + (int)i, p.ReadUInt8(r.Name, (int)i));
        for (var i = 0u; i < commentLength; i++)
            p.WriteUInt8(w.Meta, at + 26 + (int)r.Length + (int)i, p.ReadUInt8(preserved, commentAt + (int)i));
        return Put(ref p, ref w, container, w.Meta);
    }

    public static bool Create<T>(ref T p, ref Work w, ref Request r, bool directory)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!Prepare(ref p, ref w) || !Parent(ref p, ref w, ref r)) return false;
        var existing = AmigaSmartFileSystemCore.Resolve(ref p, r.Directory, r.Name, r.Length, w.Meta, out var error);
        if (existing != 0) return Fail(ref w, DOS.Error.ObjectExists);
        if (error != DOS.Error.ObjectNotFound) return Fail(ref w, error);
        var foundNode = FindFreeNode(ref p, ref w, out var node, out var leaf, out var slot);
        if (!foundNode && w.Error != DOS.Error.DiskFull) return false;
        if (!foundNode) w.Error = DOS.Error.None;
        if (!Begin(ref p, ref w)) return false;
        if (!foundNode && (!GrowNodeRoot(ref p, ref w) ||
            !FindFreeNode(ref p, ref w, out node, out leaf, out slot))) return false;
        if (!InsertObject(ref p, ref w, ref r, directory, node, APTR.Null, 0, 0, 0,
            out var container, out var hashTable)) return false;
        var hash = Hash(ref p, ref w, r.Name, r.Length);
        var oldHead = 0u;
        var hashOffset = 0;
        if (hashTable != 0)
        {
            if (!Read(ref p, ref w, hashTable, HashId, w.Meta)) return false;
            hashOffset = 16 + (hash % (int)((p.Geometry.BlockBytes - 16) / 4)) * 4;
            oldHead = p.ReadUInt32(w.Meta, hashOffset);
        }
        if (!Read(ref p, ref w, leaf, NodeId, w.Meta)) return false;
        p.WriteUInt32(w.Meta, slot, container); p.WriteUInt32(w.Meta, slot + 4, oldHead);
        p.WriteUInt16(w.Meta, slot + 8, hash);
        if (!Put(ref p, ref w, leaf, w.Meta)) return false;
        if (hashTable != 0)
        {
            if (!Read(ref p, ref w, hashTable, HashId, w.Meta)) return false;
            p.WriteUInt32(w.Meta, hashOffset, node);
            if (!Put(ref p, ref w, hashTable, w.Meta)) return false;
        }
        if (!Finish(ref p, ref w)) return false;
        r.Node = node; return true;
    }

    private static bool FileBlock<T>(ref T p, ref Work w, uint first, uint index, out uint diskBlock,
        out uint lastKey, out uint lastSize)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        diskBlock = lastKey = lastSize = 0;
        var key = first; var start = 0u;
        for (var guard = 0u; key != 0 && guard < p.Geometry.BlockCount; guard++)
        {
            if (!AmigaSmartFileSystemCore.Extent(ref p, w.ExtentRoot, key, w.Meta, out var count, out var next))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            lastKey = key; lastSize = count;
            if (index < start + count) { diskBlock = key + index - start; return true; }
            start += count; key = next;
        }
        if (key != 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        return true;
    }

    private static bool ExtentLeaf<T>(ref T p, ref Work w, uint key, out uint leaf)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        leaf = w.ExtentRoot;
        for (var depth = 0u; depth < p.Geometry.BlockCount; depth++)
        {
            if (!Read(ref p, ref w, leaf, TreeId, w.Meta)) return false;
            var count = p.ReadUInt16(w.Meta, 12); var isLeaf = p.ReadUInt8(w.Meta, 14) != 0;
            var stride = p.ReadUInt8(w.Meta, 15);
            if ((!isLeaf && count == 0) || stride < (isLeaf ? 14 : 8) ||
                16u + (uint)count * stride > p.Geometry.BlockBytes)
                return Fail(ref w, DOS.Error.DiskNotValidated);
            if (isLeaf) return true;
            var selected = 16;
            for (var i = 1; i < count && p.ReadUInt32(w.Meta, 16 + i * stride) <= key; i++)
                selected = 16 + i * stride;
            leaf = p.ReadUInt32(w.Meta, selected + 4);
            if (leaf == 0 || leaf >= p.Geometry.BlockCount) return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static void InsertBnode<T>(ref T p, APTR buffer, int count, int stride, uint key,
        uint value, uint previous, bool isLeaf) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var index = 0;
        while (index < count && p.ReadUInt32(buffer, 16 + index * stride) < key) index++;
        for (var at = 16 + count * stride - 1; at >= 16 + index * stride; at--)
            p.WriteUInt8(buffer, at + stride, p.ReadUInt8(buffer, at));
        var offset = 16 + index * stride;
        for (var i = 0; i < stride; i++) p.WriteUInt8(buffer, offset + i, 0);
        p.WriteUInt32(buffer, offset, key);
        if (isLeaf)
        {
            p.WriteUInt32(buffer, offset + 8, previous);
            p.WriteUInt16(buffer, offset + 12, 1);
        }
        else p.WriteUInt32(buffer, offset + 4, value);
        p.WriteUInt16(buffer, 12, (ushort)(count + 1));
    }

    private static bool InsertBnode<T>(ref T p, ref Work w, uint block, uint key,
        uint value, uint previous, uint depth, out uint splitKey, out uint splitBlock)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        splitKey = splitBlock = 0;
        if (depth >= 32) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!Read(ref p, ref w, block, TreeId, w.Meta)) return false;
        var count = p.ReadUInt16(w.Meta, 12); var isLeaf = p.ReadUInt8(w.Meta, 14) != 0;
        var stride = p.ReadUInt8(w.Meta, 15);
        if ((!isLeaf && count == 0) || stride < (isLeaf ? 14 : 8) ||
            16u + (uint)count * stride > p.Geometry.BlockBytes)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!isLeaf)
        {
            var selected = 16;
            for (var i = 1; i < count && p.ReadUInt32(w.Meta, 16 + i * stride) <= key; i++)
                selected = 16 + i * stride;
            var child = p.ReadUInt32(w.Meta, selected + 4);
            if (child == 0 || child >= p.Geometry.BlockCount ||
                !InsertBnode(ref p, ref w, child, key, value, previous, depth + 1, out key, out value)) return false;
            if (value == 0) return true;
            if (!Read(ref p, ref w, block, TreeId, w.Meta)) return false;
        }
        else
        {
            for (var i = 0; i < count; i++)
                if (p.ReadUInt32(w.Meta, 16 + i * stride) == key)
                    return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        if (16u + (uint)(count + 1) * stride <= p.Geometry.BlockBytes)
        {
            InsertBnode(ref p, w.Meta, count, stride, key, value, previous, isLeaf);
            return Put(ref p, ref w, block, w.Meta);
        }
        var right = AllocateAdmin(ref p, ref w);
        if (right == 0 || !Read(ref p, ref w, block, TreeId, w.Meta)) return false;
        p.Clear(w.Data, p.Geometry.BlockBytes);
        p.WriteUInt32(w.Data, 0, TreeId); p.WriteUInt32(w.Data, 8, right);
        p.WriteUInt8(w.Data, 14, isLeaf ? (byte)1 : (byte)0); p.WriteUInt8(w.Data, 15, (byte)stride);
        var leftCount = (count + 1) / 2; var rightCount = count - leftCount;
        for (var i = 0; i < rightCount * stride; i++)
        {
            p.WriteUInt8(w.Data, 16 + i, p.ReadUInt8(w.Meta, 16 + leftCount * stride + i));
            p.WriteUInt8(w.Meta, 16 + leftCount * stride + i, 0);
        }
        p.WriteUInt16(w.Meta, 12, (ushort)leftCount); p.WriteUInt16(w.Data, 12, (ushort)rightCount);
        var rightMinimum = p.ReadUInt32(w.Data, 16);
        if (key < rightMinimum) InsertBnode(ref p, w.Meta, leftCount, stride, key, value, previous, isLeaf);
        else InsertBnode(ref p, w.Data, rightCount, stride, key, value, previous, isLeaf);
        splitKey = p.ReadUInt32(w.Data, 16); splitBlock = right;
        if (!Put(ref p, ref w, block, w.Meta) || !Put(ref p, ref w, right, w.Data)) return false;
        if (depth != 0) return true;
        var left = AllocateAdmin(ref p, ref w);
        if (left == 0 || !Read(ref p, ref w, block, TreeId, w.Meta)) return false;
        p.WriteUInt32(w.Meta, 8, left);
        if (!Put(ref p, ref w, left, w.Meta)) return false;
        p.Clear(w.Meta, p.Geometry.BlockBytes);
        p.WriteUInt32(w.Meta, 0, TreeId); p.WriteUInt32(w.Meta, 8, block);
        p.WriteUInt16(w.Meta, 12, 2); p.WriteUInt8(w.Meta, 15, 8);
        p.WriteUInt32(w.Meta, 16, 0); p.WriteUInt32(w.Meta, 20, left);
        p.WriteUInt32(w.Meta, 24, splitKey); p.WriteUInt32(w.Meta, 28, right);
        splitKey = splitBlock = 0;
        return Put(ref p, ref w, block, w.Meta);
    }

    private static bool InsertExtent<T>(ref T p, ref Work w, uint key, uint previous)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!InsertBnode(ref p, ref w, w.ExtentRoot, key, 0, previous, 0, out _, out _)) return false;
        if (previous == 0) return true;
        if (!ExtentLeaf(ref p, ref w, previous, out var leaf)) return false;
        var count = p.ReadUInt16(w.Meta, 12); var stride = p.ReadUInt8(w.Meta, 15);
        for (var i = 0; i < count; i++)
        {
            var at = 16 + i * stride;
            if (p.ReadUInt32(w.Meta, at) != previous) continue;
            p.WriteUInt32(w.Meta, at + 4, key);
            return Put(ref p, ref w, leaf, w.Meta);
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool GrowExtent<T>(ref T p, ref Work w, uint key, uint newBlock)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!ExtentLeaf(ref p, ref w, key, out var leaf)) return false;
        var count = p.ReadUInt16(w.Meta, 12); var stride = p.ReadUInt8(w.Meta, 15);
        for (var i = 0; i < count; i++)
        {
            var at = 16 + i * stride;
            if (p.ReadUInt32(w.Meta, at) != key) continue;
            var blocks = p.ReadUInt16(w.Meta, at + 12);
            if (blocks == ushort.MaxValue || key + blocks != newBlock) return Fail(ref w, DOS.Error.DiskNotValidated);
            p.WriteUInt16(w.Meta, at + 12, (ushort)(blocks + 1));
            return Put(ref p, ref w, leaf, w.Meta);
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool PruneExtent<T>(ref T p, ref Work w, uint block, uint key, uint depth,
        out bool empty) where T : struct, IAmigaWritableFileSystemPlatform
    {
        empty = false;
        if (depth >= 32) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!Read(ref p, ref w, block, TreeId, w.Meta)) return false;
        var count = p.ReadUInt16(w.Meta, 12);
        if (p.ReadUInt8(w.Meta, 14) != 0) { empty = count == 0; return true; }
        if (count == 0) { empty = true; return true; }
        var stride = p.ReadUInt8(w.Meta, 15);
        if (stride < 8 || 16u + (uint)count * stride > p.Geometry.BlockBytes)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var selected = 0;
        for (var i = 1; i < count && p.ReadUInt32(w.Meta, 16 + i * stride) <= key; i++) selected = i;
        var child = p.ReadUInt32(w.Meta, 16 + selected * stride + 4);
        if (child == 0 || child >= p.Geometry.BlockCount ||
            !PruneExtent(ref p, ref w, child, key, depth + 1, out var childEmpty)) return false;
        if (!childEmpty) return true;
        if (!Read(ref p, ref w, block, TreeId, w.Meta)) return false;
        var end = 16 + count * stride;
        var at = 16 + selected * stride;
        for (var offset = at; offset < end - stride; offset++)
            p.WriteUInt8(w.Meta, offset, p.ReadUInt8(w.Meta, offset + stride));
        for (var offset = end - stride; offset < end; offset++) p.WriteUInt8(w.Meta, offset, 0);
        count--;
        if (count == 0 && depth == 0)
        {
            p.WriteUInt8(w.Meta, 14, 1); p.WriteUInt8(w.Meta, 15, 14);
        }
        p.WriteUInt16(w.Meta, 12, (ushort)count);
        if (!Put(ref p, ref w, block, w.Meta) || !FreeAdmin(ref p, ref w, child)) return false;
        empty = count == 0;
        return true;
    }

    private static bool RemoveExtent<T>(ref T p, ref Work w, uint key, out uint next)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        next = 0;
        if (!ExtentLeaf(ref p, ref w, key, out var leaf)) return false;
        var count = p.ReadUInt16(w.Meta, 12); var stride = p.ReadUInt8(w.Meta, 15);
        for (var index = 0; index < count; index++)
        {
            var at = 16 + index * stride;
            if (p.ReadUInt32(w.Meta, at) != key) continue;
            next = p.ReadUInt32(w.Meta, at + 4);
            var previous = p.ReadUInt32(w.Meta, at + 8);
            var blocks = p.ReadUInt16(w.Meta, at + 12);
            if (blocks == 0 || key > p.Geometry.BlockCount - blocks) return Fail(ref w, DOS.Error.DiskNotValidated);
            var end = 16 + count * stride;
            for (var offset = at; offset < end - stride; offset++)
                p.WriteUInt8(w.Meta, offset, p.ReadUInt8(w.Meta, offset + stride));
            for (var offset = end - stride; offset < end; offset++) p.WriteUInt8(w.Meta, offset, 0);
            p.WriteUInt16(w.Meta, 12, (ushort)(count - 1));
            if (!Put(ref p, ref w, leaf, w.Meta)) return false;
            if (count == 1 && leaf != w.ExtentRoot &&
                !PruneExtent(ref p, ref w, w.ExtentRoot, key, 0, out _)) return false;
            if (previous != 0)
            {
                if (!ExtentLeaf(ref p, ref w, previous, out var neighbor)) return false;
                var nCount = p.ReadUInt16(w.Meta, 12); var nStride = p.ReadUInt8(w.Meta, 15);
                var found = false;
                for (var i = 0; i < nCount; i++)
                    if (p.ReadUInt32(w.Meta, 16 + i * nStride) == previous)
                    { p.WriteUInt32(w.Meta, 16 + i * nStride + 4, next); found = true; break; }
                if (!found || !Put(ref p, ref w, neighbor, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
            }
            if (next != 0)
            {
                if (!ExtentLeaf(ref p, ref w, next, out var neighbor)) return false;
                var nCount = p.ReadUInt16(w.Meta, 12); var nStride = p.ReadUInt8(w.Meta, 15);
                var found = false;
                for (var i = 0; i < nCount; i++)
                    if (p.ReadUInt32(w.Meta, 16 + i * nStride) == next)
                    { p.WriteUInt32(w.Meta, 16 + i * nStride + 8, previous); found = true; break; }
                if (!found || !Put(ref p, ref w, neighbor, w.Meta)) return Fail(ref w, DOS.Error.DiskNotValidated);
            }
            for (var block = key; block < key + blocks; block++) if (!Mark(ref p, ref w, block, true)) return false;
            return true;
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool FreeFile<T>(ref T p, ref Work w, uint first)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        for (var guard = 0u; first != 0 && guard < p.Geometry.BlockCount; guard++)
        {
            if (!RemoveExtent(ref p, ref w, first, out var next)) return false;
            first = next;
        }
        return first == 0 || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    public static bool Truncate<T>(ref T p, ref Work w, uint node)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!Prepare(ref p, ref w) || !AmigaSmartFileSystemCore.Object(ref p, node, w.Meta, out var at))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if ((p.ReadUInt8(w.Meta, at + 24) & (128 | 64)) != 0) return Fail(ref w, DOS.Error.ObjectWrongType);
        if (((p.ReadUInt32(w.Meta, at + 8) ^ 15) & 4) != 0) return Fail(ref w, DOS.Error.WriteProtected);
        var first = p.ReadUInt32(w.Meta, at + 12);
        if (first == 0 && p.ReadUInt32(w.Meta, at + 16) == 0) return true;
        if (!Begin(ref p, ref w) || !FreeFile(ref p, ref w, first) ||
            !AmigaSmartFileSystemCore.Object(ref p, node, w.Meta, out at)) return false;
        p.WriteUInt32(w.Meta, at + 12, 0); p.WriteUInt32(w.Meta, at + 16, 0);
        return Put(ref p, ref w, p.ReadUInt32(w.Meta, 8), w.Meta) && Finish(ref p, ref w);
    }

    private static bool NodeSlot<T>(ref T p, ref Work w, uint node, APTR buffer, out uint leaf, out int slot)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        leaf = w.NodeRoot; slot = 0;
        var shift = 0;
        while ((1u << shift) < p.Geometry.BlockBytes) shift++;
        shift -= 5;
        for (var depth = 0u; depth < p.Geometry.BlockCount; depth++)
        {
            if (!Read(ref p, ref w, leaf, NodeId, buffer)) return false;
            var first = p.ReadUInt32(buffer, 12); var stride = p.ReadUInt32(buffer, 16);
            if (stride == 0 || node < first) return Fail(ref w, DOS.Error.DiskNotValidated);
            var index = (node - first) / stride;
            if (stride == 1)
            {
                if (index >= (p.Geometry.BlockBytes - 20) / 10) return Fail(ref w, DOS.Error.DiskNotValidated);
                slot = 20 + (int)index * 10;
                return p.ReadUInt32(buffer, slot) != 0 || Fail(ref w, DOS.Error.DiskNotValidated);
            }
            if (index >= (p.Geometry.BlockBytes - 20) / 4) return Fail(ref w, DOS.Error.DiskNotValidated);
            leaf = p.ReadUInt32(buffer, 20 + (int)index * 4) >> shift;
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool RemoveHash<T>(ref T p, ref Work w, uint parent, uint node, ushort hash)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!NodeSlot(ref p, ref w, node, w.Data, out _, out var nodeAt)) return false;
        var successor = p.ReadUInt32(w.Data, nodeAt + 4);
        if (!AmigaSmartFileSystemCore.Object(ref p, parent, w.Meta, out var at))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var table = p.ReadUInt32(w.Meta, at + 12);
        if (table == 0) return true;
        if (!Read(ref p, ref w, table, HashId, w.Meta)) return false;
        var hashAt = 16 + (hash % (int)((p.Geometry.BlockBytes - 16) / 4)) * 4;
        var current = p.ReadUInt32(w.Meta, hashAt);
        if (current == node)
        {
            p.WriteUInt32(w.Meta, hashAt, successor);
            return Put(ref p, ref w, table, w.Meta);
        }
        for (var guard = 0u; current != 0 && guard < p.Geometry.BlockCount; guard++)
        {
            if (!NodeSlot(ref p, ref w, current, w.Data, out var leaf, out var slot)) return false;
            var next = p.ReadUInt32(w.Data, slot + 4);
            if (next == node)
            {
                p.WriteUInt32(w.Data, slot + 4, successor);
                return Put(ref p, ref w, leaf, w.Data);
            }
            current = next;
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool FreeAdmin<T>(ref T p, ref Work w, uint block)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var container = w.AdminContainer;
        for (var guard = 0u; container != 0 && guard < p.Geometry.BlockCount; guard++)
        {
            if (!Read(ref p, ref w, container, AdminId, w.Meta)) return false;
            for (var at = 24; at + 8 <= p.Geometry.BlockBytes; at += 8)
            {
                var first = p.ReadUInt32(w.Meta, at);
                if (first == 0 || block < first || block - first >= 32) continue;
                var mask = 1u << (int)(31 - (block - first));
                var old = p.ReadUInt32(w.Meta, at + 4);
                if ((old & mask) == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
                p.WriteUInt32(w.Meta, at + 4, old & ~mask);
                return Put(ref p, ref w, container, w.Meta);
            }
            container = p.ReadUInt32(w.Meta, 12);
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool ExciseObject<T>(ref T p, ref Work w, uint container, int at, uint node)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!Read(ref p, ref w, container, ObjectId, w.Meta) || p.ReadUInt32(w.Meta, at + 4) != node)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!ContainerEnd(ref p, ref w, w.Meta, out var endAt)) return false;
        var comment = at + 25;
        while (comment < endAt && p.ReadUInt8(w.Meta, comment) != 0) comment++;
        comment++;
        while (comment < endAt && p.ReadUInt8(w.Meta, comment) != 0) comment++;
        var after = (comment + 2) & ~1;
        if (after > endAt) return Fail(ref w, DOS.Error.DiskNotValidated);
        var count = after - at;
        for (var offset = at; offset < endAt - count; offset++)
            p.WriteUInt8(w.Meta, offset, p.ReadUInt8(w.Meta, offset + count));
        for (var offset = endAt - count; offset < endAt; offset++) p.WriteUInt8(w.Meta, offset, 0);
        return Put(ref p, ref w, container, w.Meta);
    }

    public static bool Remove<T>(ref T p, ref Work w, uint node)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (node == AmigaSmartFileSystemCore.RootNode) return Fail(ref w, DOS.Error.ObjectInUse);
        if (!Prepare(ref p, ref w) || !AmigaSmartFileSystemCore.Object(ref p, node, w.Meta, out var at))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (((p.ReadUInt32(w.Meta, at + 8) ^ 15) & 1) != 0) return Fail(ref w, DOS.Error.DeleteProtected);
        var bits = p.ReadUInt8(w.Meta, at + 24);
        var first = p.ReadUInt32(w.Meta, at + ((bits & 128) != 0 ? 16 : 12));
        var directoryHash = (bits & 128) != 0 ? p.ReadUInt32(w.Meta, at + 12) : 0u;
        var objectContainer = p.ReadUInt32(w.Meta, 8);
        var parent = p.ReadUInt32(w.Meta, 12);
        var nameLength = 0u;
        while (at + 25 + nameLength < p.Geometry.BlockBytes && p.ReadUInt8(w.Meta, at + 25 + (int)nameLength) != 0)
            nameLength++;
        var hash = Hash(ref p, ref w, APTR.FromPointer(w.Meta.Raw + (uint)at + 25), nameLength);
        if ((bits & 128) != 0)
        {
            var block = first;
            for (var guard = 0u; block != 0 && guard < p.Geometry.BlockCount; guard++)
            {
                if (!Read(ref p, ref w, block, ObjectId, w.Data) || !ContainerEnd(ref p, ref w, w.Data, out var end)) return false;
                if (end != 24) return Fail(ref w, DOS.Error.DirectoryNotEmpty);
                block = p.ReadUInt32(w.Data, 16);
            }
            if (block != 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        else if ((bits & 64) != 0) return Fail(ref w, DOS.Error.ObjectWrongType);
        else if (first != 0 && (!Read(ref p, ref w, w.ExtentRoot, TreeId, w.Data) || p.ReadUInt8(w.Data, 14) == 0))
            return Fail(ref w, DOS.Error.ActionNotKnown);
        if (!Begin(ref p, ref w)) return false;
        if ((bits & 128) != 0)
        {
            var block = first;
            while (block != 0)
            {
                if (!Read(ref p, ref w, block, ObjectId, w.Data)) return false;
                var next = p.ReadUInt32(w.Data, 16);
                if (!FreeAdmin(ref p, ref w, block)) return false;
                block = next;
            }
            if (directoryHash != 0 && !FreeAdmin(ref p, ref w, directoryHash)) return false;
        }
        else if (!FreeFile(ref p, ref w, first)) return false;
        if (!RemoveHash(ref p, ref w, parent, node, hash) ||
            !NodeSlot(ref p, ref w, node, w.Data, out var leafBlock, out var nodeSlot)) return false;
        p.WriteUInt32(w.Data, nodeSlot, 0); p.WriteUInt32(w.Data, nodeSlot + 4, 0); p.WriteUInt16(w.Data, nodeSlot + 8, 0);
        return Put(ref p, ref w, leafBlock, w.Data) &&
            ExciseObject(ref p, ref w, objectContainer, at, node) && Finish(ref p, ref w);
    }

    public static bool Rename<T>(ref T p, ref Work w, ref Request r)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (r.Node == AmigaSmartFileSystemCore.RootNode) return Fail(ref w, DOS.Error.ObjectInUse);
        if (!Prepare(ref p, ref w) || !Parent(ref p, ref w, ref r)) return false;
        var existing = AmigaSmartFileSystemCore.Resolve(ref p, r.Directory, r.Name, r.Length, w.Meta, out var error);
        if (existing != 0 && existing != r.Node) return Fail(ref w, DOS.Error.ObjectExists);
        if (existing == 0 && error != DOS.Error.ObjectNotFound) return Fail(ref w, error);
        if (!AmigaSmartFileSystemCore.Object(ref p, r.Node, w.Meta, out var oldAt))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (((p.ReadUInt32(w.Meta, oldAt + 8) ^ 15) & 1) != 0) return Fail(ref w, DOS.Error.DeleteProtected);
        var oldContainer = p.ReadUInt32(w.Meta, 8);
        var oldParent = p.ReadUInt32(w.Meta, 12);
        var directory = (p.ReadUInt8(w.Meta, oldAt + 24) & 128) != 0;
        var oldNameLength = 0u;
        while (oldAt + 25 + oldNameLength < p.Geometry.BlockBytes &&
            p.ReadUInt8(w.Meta, oldAt + 25 + (int)oldNameLength) != 0) oldNameLength++;
        var commentAt = oldAt + 26 + (int)oldNameLength;
        var commentLength = 0u;
        while (commentAt + commentLength < p.Geometry.BlockBytes &&
            p.ReadUInt8(w.Meta, commentAt + (int)commentLength) != 0) commentLength++;
        if (commentAt + commentLength >= p.Geometry.BlockBytes)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        p.Copy(w.Meta, w.Bitmap, p.Geometry.BlockBytes);
        w.PreserveBitmap = true;
        var oldHash = Hash(ref p, ref w, APTR.FromPointer(w.Bitmap.Raw + (uint)oldAt + 25), oldNameLength);
        if (directory)
        {
            var ancestor = r.Directory;
            for (var guard = 0u; ancestor != AmigaSmartFileSystemCore.RootNode && guard < p.Geometry.BlockCount; guard++)
            {
                if (ancestor == r.Node) return Fail(ref w, DOS.Error.ObjectInUse);
                if (!AmigaSmartFileSystemCore.Describe(ref p, ancestor, w.Meta, out _, out _, out ancestor))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
            }
            if (ancestor != AmigaSmartFileSystemCore.RootNode) return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        if (!Begin(ref p, ref w) || !RemoveHash(ref p, ref w, oldParent, r.Node, oldHash) ||
            !ExciseObject(ref p, ref w, oldContainer, oldAt, r.Node)) return false;
        if (!InsertObject(ref p, ref w, ref r, directory, r.Node, w.Bitmap, oldAt, commentAt, commentLength,
            out var newContainer, out var hashTable)) return false;
        var newHash = Hash(ref p, ref w, r.Name, r.Length);
        var hashAt = 0; var oldHead = 0u;
        if (hashTable != 0)
        {
            if (!Read(ref p, ref w, hashTable, HashId, w.Meta)) return false;
            hashAt = 16 + (newHash % (int)((p.Geometry.BlockBytes - 16) / 4)) * 4;
            oldHead = p.ReadUInt32(w.Meta, hashAt);
        }
        if (!NodeSlot(ref p, ref w, r.Node, w.Data, out var nodeBlock, out var slot)) return false;
        p.WriteUInt32(w.Data, slot, newContainer); p.WriteUInt32(w.Data, slot + 4, oldHead);
        p.WriteUInt16(w.Data, slot + 8, newHash);
        if (!Put(ref p, ref w, nodeBlock, w.Data)) return false;
        if (hashTable != 0)
        {
            if (!Read(ref p, ref w, hashTable, HashId, w.Meta)) return false;
            p.WriteUInt32(w.Meta, hashAt, r.Node);
            if (!Put(ref p, ref w, hashTable, w.Meta)) return false;
        }
        return Finish(ref p, ref w);
    }

    public static bool SetMetadata<T>(ref T p, ref Work w, ref Request r, DosPacketAction action)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (action != DosPacketAction.SetProtection && action != DosPacketAction.SetDate &&
            action != DosPacketAction.SetComment) return Fail(ref w, DOS.Error.ActionNotKnown);
        if (action == DosPacketAction.SetComment && r.Length > 79) return Fail(ref w, DOS.Error.CommentTooBig);
        if (action == DosPacketAction.SetDate && !p.IsMapped(r.Buffer, 12)) return Fail(ref w, DOS.Error.BadNumber);
        if (action == DosPacketAction.SetComment && !p.IsMapped(r.Buffer, r.Length)) return Fail(ref w, DOS.Error.BadNumber);
        if (!Prepare(ref p, ref w) || !AmigaSmartFileSystemCore.Object(ref p, r.Node, w.Meta, out var at))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (action == DosPacketAction.SetProtection)
        {
            if (!Begin(ref p, ref w) || !AmigaSmartFileSystemCore.Object(ref p, r.Node, w.Meta, out at)) return false;
            p.WriteUInt32(w.Meta, at + 8, r.Position ^ 15);
            return Put(ref p, ref w, p.ReadUInt32(w.Meta, 8), w.Meta) && Finish(ref p, ref w);
        }
        if (action == DosPacketAction.SetDate)
        {
            var days = p.ReadUInt32(r.Buffer, 0); var minutes = p.ReadUInt32(r.Buffer, 4);
            var ticks = p.ReadUInt32(r.Buffer, 8);
            if (minutes >= 1440 || ticks >= 3000 || days > (uint.MaxValue - minutes * 60 - ticks / 50) / 86400)
                return Fail(ref w, DOS.Error.BadNumber);
            var seconds = days * 86400 + minutes * 60 + ticks / 50;
            if (!Begin(ref p, ref w) || !AmigaSmartFileSystemCore.Object(ref p, r.Node, w.Meta, out at)) return false;
            p.WriteUInt32(w.Meta, at + 20, seconds);
            return Put(ref p, ref w, p.ReadUInt32(w.Meta, 8), w.Meta) && Finish(ref p, ref w);
        }
        if (!ContainerEnd(ref p, ref w, w.Meta, out var end)) return false;
        var nameEnd = at + 25;
        while (nameEnd < end && p.ReadUInt8(w.Meta, nameEnd) != 0) nameEnd++;
        if (nameEnd >= end) return Fail(ref w, DOS.Error.DiskNotValidated);
        var commentAt = nameEnd + 1;
        var oldCommentEnd = commentAt;
        while (oldCommentEnd < end && p.ReadUInt8(w.Meta, oldCommentEnd) != 0) oldCommentEnd++;
        if (oldCommentEnd >= end) return Fail(ref w, DOS.Error.DiskNotValidated);
        var oldAfter = (oldCommentEnd + 2) & ~1;
        var newAfter = (commentAt + (int)r.Length + 2) & ~1;
        var shift = newAfter - oldAfter;
        if (end + shift + 27 > p.Geometry.BlockBytes) return Fail(ref w, DOS.Error.DiskFull);
        if (!Begin(ref p, ref w) || !AmigaSmartFileSystemCore.Object(ref p, r.Node, w.Meta, out at) ||
            !ContainerEnd(ref p, ref w, w.Meta, out end)) return false;
        if (shift > 0)
            for (var offset = end - 1; offset >= oldAfter; offset--)
                p.WriteUInt8(w.Meta, offset + shift, p.ReadUInt8(w.Meta, offset));
        else if (shift < 0)
            for (var offset = oldAfter; offset < end; offset++)
                p.WriteUInt8(w.Meta, offset + shift, p.ReadUInt8(w.Meta, offset));
        for (var i = 0u; i < r.Length; i++) p.WriteUInt8(w.Meta, commentAt + (int)i, p.ReadUInt8(r.Buffer, (int)i));
        p.WriteUInt8(w.Meta, commentAt + (int)r.Length, 0);
        for (var offset = commentAt + (int)r.Length + 1; offset < newAfter; offset++) p.WriteUInt8(w.Meta, offset, 0);
        if (shift < 0) for (var offset = end + shift; offset < end; offset++) p.WriteUInt8(w.Meta, offset, 0);
        return Put(ref p, ref w, p.ReadUInt32(w.Meta, 8), w.Meta) && Finish(ref p, ref w);
    }

    public static bool Write<T>(ref T p, ref Work w, ref Request r, out uint actual)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        actual = 0;
        if (r.Length > int.MaxValue || !p.IsMapped(r.Buffer, r.Length)) return Fail(ref w, DOS.Error.BadNumber);
        if (!Prepare(ref p, ref w) || !AmigaSmartFileSystemCore.Object(ref p, r.Node, w.Meta, out var at))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if ((p.ReadUInt8(w.Meta, at + 24) & (128 | 64)) != 0) return Fail(ref w, DOS.Error.ObjectWrongType);
        if (((p.ReadUInt32(w.Meta, at + 8) ^ 15) & 4) != 0) return Fail(ref w, DOS.Error.WriteProtected);
        var size = p.ReadUInt32(w.Meta, at + 16); var first = p.ReadUInt32(w.Meta, at + 12);
        if (r.Position > size) return Fail(ref w, DOS.Error.SeekError);
        if (r.Length > uint.MaxValue - r.Position) return Fail(ref w, DOS.Error.ObjectTooLarge);
        if (r.Length == 0) return true;
        var blockBytes = p.Geometry.BlockBytes;
        var end = r.Position + r.Length;
        var oldBlocks = size / blockBytes + (size % blockBytes == 0 ? 0u : 1u);
        var newBlocks = end / blockBytes + (end % blockBytes == 0 ? 0u : 1u);
        if (newBlocks > oldBlocks)
        {
            if (!Read(ref p, ref w, w.RootContainer, ObjectId, w.Meta)) return false;
            if (newBlocks - oldBlocks > p.ReadUInt32(w.Meta, (int)blockBytes - 28)) return Fail(ref w, DOS.Error.DiskFull);
        }
        if (!Begin(ref p, ref w)) return false;
        var position = r.Position;
        while (actual < r.Length)
        {
            var index = position / blockBytes;
            if (!FileBlock(ref p, ref w, first, index, out var diskBlock, out var lastKey, out var lastSize)) return false;
            var newlyAllocated = diskBlock == 0;
            if (newlyAllocated)
            {
                var preferred = lastKey == 0 ? 0u : lastKey + lastSize;
                diskBlock = AllocateData(ref p, ref w, preferred);
                if (diskBlock == 0) return false;
                if (lastKey != 0 && diskBlock == preferred && lastSize < ushort.MaxValue)
                {
                    if (!GrowExtent(ref p, ref w, lastKey, diskBlock)) return false;
                }
                else if (!InsertExtent(ref p, ref w, diskBlock, lastKey)) return false;
                if (first == 0) first = diskBlock;
                p.Clear(w.Data, blockBytes);
            }
            else if (!p.ReadBlock(diskBlock, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            var within = position % blockBytes;
            var count = blockBytes - within;
            if (count > r.Length - actual) count = r.Length - actual;
            for (var i = 0u; i < count; i++)
                p.WriteUInt8(w.Data, (int)(within + i), p.ReadUInt8(r.Buffer, (int)(actual + i)));
            if (!JournalWrite(ref p, ref w, diskBlock, w.Data)) return false;
            actual += count; position += count;
        }
        if (!AmigaSmartFileSystemCore.Object(ref p, r.Node, w.Meta, out at))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (first != p.ReadUInt32(w.Meta, at + 12)) p.WriteUInt32(w.Meta, at + 12, first);
        if (end > size) p.WriteUInt32(w.Meta, at + 16, end);
        if (!Put(ref p, ref w, p.ReadUInt32(w.Meta, 8), w.Meta)) return false;
        return Finish(ref p, ref w);
    }

    public static bool Resize<T>(ref T p, ref Work w, uint node, uint newSize)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!Prepare(ref p, ref w) || !AmigaSmartFileSystemCore.Object(ref p, node, w.Meta, out var at))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if ((p.ReadUInt8(w.Meta, at + 24) & (128 | 64)) != 0) return Fail(ref w, DOS.Error.ObjectWrongType);
        if (((p.ReadUInt32(w.Meta, at + 8) ^ 15) & 4) != 0) return Fail(ref w, DOS.Error.WriteProtected);
        var oldSize = p.ReadUInt32(w.Meta, at + 16);
        if (newSize == oldSize) return true;
        var first = p.ReadUInt32(w.Meta, at + 12);
        var blockBytes = p.Geometry.BlockBytes;
        var oldBlocks = oldSize / blockBytes + (oldSize % blockBytes == 0 ? 0u : 1u);
        var newBlocks = newSize / blockBytes + (newSize % blockBytes == 0 ? 0u : 1u);
        if (newBlocks > oldBlocks)
        {
            if (!Read(ref p, ref w, w.RootContainer, ObjectId, w.Meta)) return false;
            if (newBlocks - oldBlocks > p.ReadUInt32(w.Meta, (int)blockBytes - 28))
                return Fail(ref w, DOS.Error.DiskFull);
        }
        if (!Begin(ref p, ref w)) return false;
        if (newBlocks < oldBlocks)
        {
            var current = first; var start = 0u;
            for (var guard = 0u; current != 0 && guard < p.Geometry.BlockCount; guard++)
            {
                if (!AmigaSmartFileSystemCore.Extent(ref p, w.ExtentRoot, current, w.Meta,
                    out var count, out var next)) return Fail(ref w, DOS.Error.DiskNotValidated);
                if (start >= newBlocks)
                {
                    if (!RemoveExtent(ref p, ref w, current, out _)) return false;
                }
                else if (start + count > newBlocks)
                {
                    if (!ExtentLeaf(ref p, ref w, current, out var leaf)) return false;
                    var entries = p.ReadUInt16(w.Meta, 12); var stride = p.ReadUInt8(w.Meta, 15);
                    var keep = newBlocks - start;
                    var found = false;
                    for (var i = 0; i < entries; i++)
                    {
                        var offset = 16 + i * stride;
                        if (p.ReadUInt32(w.Meta, offset) != current) continue;
                        p.WriteUInt16(w.Meta, offset + 12, (ushort)keep);
                        found = true; break;
                    }
                    if (!found || !Put(ref p, ref w, leaf, w.Meta))
                        return Fail(ref w, DOS.Error.DiskNotValidated);
                    for (var block = current + keep; block < current + count; block++)
                        if (!Mark(ref p, ref w, block, true)) return false;
                }
                start += count; current = next;
            }
            if (current != 0) return Fail(ref w, DOS.Error.DiskNotValidated);
            if (newBlocks == 0) first = 0;
        }
        else if (newSize > oldSize)
        {
            if (oldBlocks != 0 && oldSize % blockBytes != 0)
            {
                if (!FileBlock(ref p, ref w, first, oldBlocks - 1, out var diskBlock, out _, out _) ||
                    diskBlock == 0 || !p.ReadBlock(diskBlock, w.Data))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                var stop = newBlocks == oldBlocks ? newSize % blockBytes : blockBytes;
                if (stop == 0) stop = blockBytes;
                for (var offset = oldSize % blockBytes; offset < stop; offset++)
                    p.WriteUInt8(w.Data, (int)offset, 0);
                if (!JournalWrite(ref p, ref w, diskBlock, w.Data)) return false;
            }
            for (var index = oldBlocks; index < newBlocks; index++)
            {
                if (!FileBlock(ref p, ref w, first, index, out var existing, out var lastKey, out var lastSize) ||
                    existing != 0) return Fail(ref w, DOS.Error.DiskNotValidated);
                var preferred = lastKey == 0 ? 0u : lastKey + lastSize;
                var diskBlock = AllocateData(ref p, ref w, preferred);
                if (diskBlock == 0) return false;
                if (lastKey != 0 && diskBlock == preferred && lastSize < ushort.MaxValue)
                {
                    if (!GrowExtent(ref p, ref w, lastKey, diskBlock)) return false;
                }
                else if (!InsertExtent(ref p, ref w, diskBlock, lastKey)) return false;
                if (first == 0) first = diskBlock;
                p.Clear(w.Data, blockBytes);
                if (!JournalWrite(ref p, ref w, diskBlock, w.Data)) return false;
            }
        }
        if (!AmigaSmartFileSystemCore.Object(ref p, node, w.Meta, out at))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        p.WriteUInt32(w.Meta, at + 12, first); p.WriteUInt32(w.Meta, at + 16, newSize);
        return Put(ref p, ref w, p.ReadUInt32(w.Meta, 8), w.Meta) && Finish(ref p, ref w);
    }
}
