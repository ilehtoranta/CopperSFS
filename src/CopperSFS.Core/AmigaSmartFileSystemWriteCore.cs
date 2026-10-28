using Amiga;
using BufferHeader = CopperSFS.AmigaSmartFileSystemWriteBufferLayout.Header;
using BufferEntry = CopperSFS.AmigaSmartFileSystemWriteBufferLayout.Entry;
using TransactionEntry = CopperSFS.AmigaSmartFileSystemTransactionBufferLayout.Entry;

namespace CopperSFS;

/// <summary>
/// Allocation-free writes to classic SFS volumes. A write-ahead undo chain
/// records each old block before it is changed. The caller supplies four
/// block-sized scratch buffers, and may supply a transaction staging buffer
/// also consulted by the platform's ReadBlock implementation.
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
        /// <summary>Optional shared mount RAM; see OverwriteReservationBytes.</summary>
        public APTR Reservations;
        public APTR Meta, Data, Bitmap, Journal, Batch;
        /// <summary>Optional mount-owned write-back buffer. Null retains strict durability.</summary>
        public APTR Buffered;
        public uint RootContainer, BitmapBase, AdminContainer, NodeRoot, ExtentRoot;
        public uint JournalHead, JournalCursor, AllocationCursor, PublishedHead;
        public int FreeDelta;
        internal int RecycledFilesDelta, RecycledBlocksDelta;
        public bool CaseSensitive, PreserveBitmap, JournalActive;
        public DOS.Error Error;
        internal uint BlockBytes, BlockCount, ReservedBlocks;
        internal uint OriginalBitmap, OriginalBitmapImage;
        internal uint OriginalBitmapWord;
        internal int OriginalBitmapWordOffset;
        internal bool JournalWrapped;
        internal bool BufferCommitting, BufferFull;
        internal uint AdminReservation;
        internal bool RedoStaging;
        internal APTR RedoCheckpoint;
        // A group-owned scalar record hint, valid only during WriteTrstGroup.
        // It never holds a borrowed metadata pointer.
        internal APTR TrstObjectHint;
        internal APTR TrstContinuation;
    }

    public struct Request
    {
        public uint Directory, Node, Position, Length;
        public APTR Name, Buffer;
    }

    private static bool Fail(ref Work w, DOS.Error error) { w.Error = error; return false; }

    private static void Seal<T>(ref T p, APTR source, uint bytes) where T : struct, IAmigaFileSystemPlatform
    {
        p.WriteUInt32(source, 4, 0);
        p.WriteUInt32(source, 4, unchecked(0u - BlockSum(ref p, source, bytes) - 1));
    }

    private static uint BlockSum<T>(ref T p, APTR source, uint bytes) where T : struct, IAmigaFileSystemPlatform
        => AmigaSmartFileSystemCore.BlockSum(ref p, source, bytes);

    private static bool Put<T>(ref T p, ref Work w, uint block, APTR source)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Error != DOS.Error.None) return false;
        Seal(ref p, source, w.BlockBytes);
        return PutSealed(ref p, ref w, block, source);
    }

    private static bool PutSealed<T>(ref T p, ref Work w, uint block, APTR source)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Error != DOS.Error.None) return false;
        if (w.RedoStaging)
            return AmigaSmartFileSystemRedoArena.StoreSealed(ref p, p.RedoOverlay, block, source) ||
                Fail(ref w, DOS.Error.NoFreeStore);
        return (w.JournalActive ? JournalWrite(ref p, ref w, block, source,
            p.ReadUInt32(source, 8) == block ? p.ReadUInt32(source) : 0) : p.WriteBlock(block, source)) ||
            Fail(ref w, DOS.Error.DiskNotValidated);
    }

    // A verified metadata image remains sealed when replacing a known word:
    // its checksum changes by old - new in the same modulo-2^32 arithmetic.
    private static void ReplaceWord<T>(ref T p, APTR image, int offset, uint value)
        where T : struct, IAmigaGuestMemory
    {
        var old = p.ReadUInt32(image, offset);
        p.WriteUInt32(image, offset, value);
        // Object records are word-aligned, so a longword field can straddle
        // two checksum words. Its two halves then contribute in reverse order.
        var before = (offset & 2) == 0 ? old : (old << 16) | (old >> 16);
        var after = (offset & 2) == 0 ? value : (value << 16) | (value >> 16);
        p.WriteUInt32(image, 4, unchecked(p.ReadUInt32(image, 4) + before - after));
    }

    private static void ReplaceHalf<T>(ref T p, APTR image, int offset, ushort value)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var at = offset & ~3; var word = p.ReadUInt32(image, at);
        ReplaceWord(ref p, image, at, (offset & 2) == 0 ? (word & 65535) | ((uint)value << 16) : (word & 0xFFFF0000) | value);
    }

    private static bool JournalContains<T>(ref T p, ref Work w, uint candidate, out bool found)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        found = false;
        if (w.Buffered.IsNotNull)
        {
            var count = AmigaSmartFileSystemWriteBuffer.Count(ref p, w.Buffered);
            var slot = AmigaSmartFileSystemWriteBuffer.First(w.Buffered);
            for (var i = 0u; i < count; i++, slot = AmigaSmartFileSystemWriteBuffer.Next(slot, w.BlockBytes))
            {
                if (candidate == p.ReadUInt32(slot, BufferEntry.Block) || candidate == p.ReadUInt32(slot, BufferEntry.UndoImage) || candidate == p.ReadUInt32(slot, BufferEntry.UndoEntry))
                { found = true; return true; }
            }
        }
        var entry = w.JournalHead;
        for (var guard = 0u; entry != 0 && guard < w.BlockCount; guard++)
        {
            if (!AmigaSmartFileSystemCore.Block(ref p, entry, UndoId, w.Journal, w.BlockCount, w.BlockBytes))
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
        if (target == w.OriginalBitmap && w.OriginalBitmapImage != 0)
        { found = true; return true; }
        found = false;
        var entry = w.JournalHead;
        for (var guard = 0u; entry != 0 && guard < w.BlockCount; guard++)
        {
            if (!AmigaSmartFileSystemCore.Block(ref p, entry, UndoId, w.Journal, w.BlockCount, w.BlockBytes))
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
        if (w.Buffered.IsNotNull)
        {
            var slot = AmigaSmartFileSystemWriteBuffer.Find(ref p, w.Buffered, w.BlockBytes, map);
            if (slot.IsNotNull)
                free = (p.ReadUInt32(AmigaSmartFileSystemWriteBuffer.Checkpoint(slot, w.BlockBytes), offset) & mask) != 0;
            return true;
        }
        if (map == w.OriginalBitmap && w.OriginalBitmapImage != 0)
        {
            if (offset == w.OriginalBitmapWordOffset)
            { free = (w.OriginalBitmapWord & mask) != 0; return true; }
            if (!p.ReadBlock(w.OriginalBitmapImage, w.Journal))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            w.OriginalBitmapWord = p.ReadUInt32(w.Journal, offset);
            w.OriginalBitmapWordOffset = offset;
            free = (w.OriginalBitmapWord & mask) != 0;
            return true;
        }
        var entry = w.JournalHead;
        for (var guard = 0u; entry != 0 && guard < w.BlockCount; guard++)
        {
            if (!AmigaSmartFileSystemCore.Block(ref p, entry, UndoId, w.Journal, w.BlockCount, w.BlockBytes))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            var next = p.ReadUInt32(w.Journal, 12);
            if (p.ReadUInt32(w.Journal, 16) == map)
            {
                var image = p.ReadUInt32(w.Journal, 20);
                if (image >= w.BlockCount || !p.ReadBlock(image, w.Journal))
                    return Fail(ref w, DOS.Error.DiskNotValidated);
                w.OriginalBitmap = map; w.OriginalBitmapImage = image;
                w.OriginalBitmapWord = p.ReadUInt32(w.Journal, offset);
                w.OriginalBitmapWordOffset = offset;
                free = (w.OriginalBitmapWord & mask) != 0;
                return true;
            }
            entry = next;
        }
        return entry == 0 || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static uint JournalSpace<T>(ref T p, ref Work w, uint target, APTR source, uint avoid)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var low = w.ReservedBlocks;
        var high = w.BlockCount - 1;
        if (w.JournalCursor < low || w.JournalCursor >= high)
        { w.JournalCursor = low; w.JournalWrapped = true; }
        var cachedMap = uint.MaxValue;
        for (var scan = 0u; scan < high - low; scan++)
        {
            var candidate = w.JournalCursor++;
            if (w.JournalCursor >= high) { w.JournalCursor = low; w.JournalWrapped = true; }
            if (candidate == target || candidate == avoid || candidate == w.RootContainer + 2 || OverwriteReserved(ref p, ref w, candidate)) continue;
            if (w.AdminReservation != 0 && candidate >= w.AdminReservation && candidate - w.AdminReservation < 32) continue;
            if (!BitmapPosition(ref w, candidate, out var map, out var offset, out var mask)) return 0;
            if (map != target && map != cachedMap)
            {
                if (!Read(ref p, ref w, map, BitmapId, w.Journal)) return 0;
                cachedMap = map;
            }
            var bitmapSource = w.Journal;
            if (map == target) bitmapSource = source;
            var available = p.ReadUInt32(bitmapSource, offset);
            if ((available & mask) == 0)
            {
                // Bitmap bits run most-significant first. Skip only the used
                // suffix of this word, bounded by the scan and volume ends.
                var skip = UsedSpan(ref p, bitmapSource, offset, mask, w.BlockBytes);
                if (skip > high - candidate - 1) skip = high - candidate - 1;
                if (skip > high - low - scan - 1) skip = high - low - scan - 1;
                scan += skip;
                w.JournalCursor = candidate + skip + 1;
                if (w.JournalCursor >= high) { w.JournalCursor = low; w.JournalWrapped = true; }
                continue;
            }
            if (map == target)
            {
                // The proposed bitmap can already free live file blocks before
                // its first undo record exists. Rollback still needs their bytes.
                if (!Read(ref p, ref w, map, BitmapId, w.Journal)) return 0;
                if ((p.ReadUInt32(w.Journal, offset) & mask) == 0) continue;
            }
            // The journal lookups reuse w.Journal, invalidating its cached bitmap.
            cachedMap = uint.MaxValue;
            // Before the first wrap, each candidate is beyond every undo
            // sector allocated by this transaction. After wrapping, retain
            // the complete chain check.
            var inUse = false;
            if ((w.JournalWrapped || w.Buffered.IsNotNull) && !JournalContains(ref p, ref w, candidate, out inUse)) return 0;
            if (inUse) continue;
            if (!OriginallyFree(ref p, ref w, map, offset, mask, out var originallyFree)) return 0;
            if (originallyFree) return candidate;
        }
        if (HasOverwriteReservations(ref p, ref w))
        { ReleaseOverwriteReservation(ref p, ref w); return JournalSpace(ref p, ref w, target, source, avoid); }
        Fail(ref w, DOS.Error.DiskFull); return 0;
    }

    private static bool JournalWrite<T>(ref T p, ref Work w, uint target, APTR source, uint verifiedId = 0)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.RedoStaging) return p.WriteBlock(target, source) || Fail(ref w, DOS.Error.NoFreeStore);
        if (w.Buffered.IsNotNull && !w.BufferCommitting) return BufferWrite(ref p, ref w, target, source, verifiedId);
        var batched = w.Batch.IsNotNull;
        if (batched && !AmigaSmartFileSystemTransactionBuffer.HasRoom(ref p, w.Batch, w.BlockBytes, target) &&
            !PublishBatch(ref p, ref w)) return false;
        if (!JournalHasTarget(ref p, ref w, target, out var alreadyLogged)) return false;
        if (alreadyLogged)
        {
            if (!batched) return p.WriteBlock(target, source) || Fail(ref w, DOS.Error.DiskNotValidated);
            AmigaSmartFileSystemTransactionBuffer.Store(ref p, w.Batch, w.BlockBytes, target, source);
            return true;
        }
        var saved = w.Buffered.IsNull ? APTR.Null :
            AmigaSmartFileSystemWriteBuffer.Find(ref p, w.Buffered, w.BlockBytes, target);
        var image = saved.IsNull ? JournalSpace(ref p, ref w, target, source, 0) : p.ReadUInt32(saved, BufferEntry.UndoImage);
        if (image == 0) return false;
        var entry = saved.IsNull ? JournalSpace(ref p, ref w, target, source, image) : p.ReadUInt32(saved, BufferEntry.UndoEntry);
        if (entry == 0) return false;
        if (saved.IsNotNull) p.Copy(AmigaSmartFileSystemWriteBuffer.Checkpoint(saved, w.BlockBytes), w.Journal, w.BlockBytes);
        else if (!p.ReadBlock(target, w.Journal))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var originalSum = BlockSum(ref p, w.Journal, w.BlockBytes);
        var originalBitmap = p.ReadUInt32(w.Journal) == BitmapId;
        if (!p.WriteBlock(image, w.Journal)) return Fail(ref w, DOS.Error.DiskNotValidated);
        p.Clear(w.Journal, w.BlockBytes);
        p.WriteUInt32(w.Journal, 0, UndoId); p.WriteUInt32(w.Journal, 8, entry);
        p.WriteUInt32(w.Journal, 12, w.JournalHead);
        p.WriteUInt32(w.Journal, 16, target); p.WriteUInt32(w.Journal, 20, image);
        p.WriteUInt32(w.Journal, 24, originalSum);
        // This newly cleared block has only seven populated longwords. Its
        // zero tail contributes nothing; reads and replay still check the
        // entire on-disk block, including that tail.
        Seal(ref p, w.Journal, 28);
        if (!p.WriteBlock(entry, w.Journal))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        w.JournalHead = entry;
        if (originalBitmap)
        { w.OriginalBitmap = target; w.OriginalBitmapImage = image; w.OriginalBitmapWordOffset = -1; }
        if (batched)
        {
            AmigaSmartFileSystemTransactionBuffer.Store(ref p, w.Batch, w.BlockBytes, target, source);
            return true;
        }
        if (!PublishJournalHead(ref p, ref w)) return false;
        return p.WriteBlock(target, source) || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool PublishJournalHead<T>(ref T p, ref Work w)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        // All old images and chain links must be durable before the marker
        // can reference them, even if unflushed writes reach media early.
        if (!p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        p.Clear(w.Journal, w.BlockBytes);
        p.WriteUInt32(w.Journal, 0, TransactionFailed);
        p.WriteUInt32(w.Journal, 8, w.RootContainer + 2);
        p.WriteUInt32(w.Journal, 12, w.JournalHead);
        // The rest of this freshly cleared marker is zero.
        Seal(ref p, w.Journal, 16);
        if (!p.WriteBlock(w.RootContainer + 2, w.Journal) || !p.Flush())
            return Fail(ref w, DOS.Error.DiskNotValidated);
        w.PublishedHead = w.JournalHead;
        return true;
    }

    private static bool PublishBatch<T>(ref T p, ref Work w)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var count = AmigaSmartFileSystemTransactionBuffer.Count(ref p, w.Batch);
        if (count == 0) return true;
        if (w.PublishedHead != w.JournalHead && !PublishJournalHead(ref p, ref w)) return false;
        // The durable chain covers every target in this batch. Keep that chain
        // across buffer drains so the entire operation remains undoable.
        for (var index = 0u; index < count; index++)
        {
            var slot = AmigaSmartFileSystemTransactionBuffer.Slot(w.Batch, w.BlockBytes, index);
            if (!p.WriteBlock(p.ReadUInt32(slot, TransactionEntry.Block), APTR.FromPointer(slot.Raw + TransactionEntry.Image)))
                return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        AmigaSmartFileSystemTransactionBuffer.Discard(ref p, w.Batch);
        return true;
    }

    private static bool RecoverDisk<T>(ref T p, ref Work w)
        where T : struct, IAmigaWritableFileSystemPlatform => RecoverDisk(ref p, ref w, out _);

    private static void RootFields<T>(ref T p, ref Work w, APTR root)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        w.BitmapBase = p.ReadUInt32(root, 96); w.AdminContainer = p.ReadUInt32(root, 100);
        w.RootContainer = p.ReadUInt32(root, 104); w.ExtentRoot = p.ReadUInt32(root, 108);
        w.NodeRoot = p.ReadUInt32(root, 112); w.CaseSensitive = (p.ReadUInt8(root, 20) & 128) != 0;
    }

    private static bool RecoverDisk<T>(ref T p, ref Work w, out bool cleanRoots)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        cleanRoots = false;
        w.Error = DOS.Error.None;
        w.OriginalBitmap = 0; w.OriginalBitmapImage = 0; w.JournalWrapped = true;
        w.OriginalBitmapWordOffset = -1;
        // The mounted geometry is stable throughout recovery and the following
        // mutation. Keep the scalars here; native aggregate getters otherwise
        // reconstruct the complete device geometry at every lookup.

        w.BlockBytes = p.BlockBytes;
        w.BlockCount = p.BlockCount;
        w.ReservedBlocks = p.ReservedBlocks;
        if (w.Batch.IsNotNull)
        {
            if (!AmigaSmartFileSystemTransactionBuffer.Valid(ref p, w.Batch, w.BlockBytes))
                return Fail(ref w, DOS.Error.NoFreeStore);
            // Recovery must read the media, never a failed operation's staged view.
            AmigaSmartFileSystemTransactionBuffer.Discard(ref p, w.Batch);
        }
        var root = AmigaSmartFileSystemCore.RootView(ref p, w.Meta);
        if (root.IsNull)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        RootFields(ref p, ref w, root);
        var marker = w.RootContainer + 2;
        if (marker >= w.BlockCount || !p.ReadBlock(marker, w.Meta) ||
            p.ReadUInt32(w.Meta, 8) != marker || !AmigaSmartFileSystemCore.ValidChecksum(ref p, w.Meta))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (p.ReadUInt32(w.Meta) == TransactionOk) { cleanRoots = true; return true; }
        ReleaseOverwriteReservation(ref p, ref w);
        // Original SmartFilesystem can store the redo stream directly in the
        // transaction marker (root container + 2), with no TRFA indirection.
        if (p.ReadUInt32(w.Meta) == NativeRedoId)
            return p.WriteProtected() || w.Journal.IsNull ||
                !p.IsMapped(w.Journal, w.BlockBytes)
                ? Fail(ref w, DOS.Error.DiskNotValidated)
                : ReplayNative(ref p, ref w, marker, marker);
        if (p.ReadUInt32(w.Meta) != TransactionFailed || p.WriteProtected() ||
            w.Journal.IsNull || !p.IsMapped(w.Journal, w.BlockBytes))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var entry = p.ReadUInt32(w.Meta, 12);
        if (entry != 0)
        {
            if (entry >= w.BlockCount || !p.ReadBlock(entry, w.Journal))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            if (p.ReadUInt32(w.Journal) == NativeRedoId)
                return ReplayNative(ref p, ref w, marker, entry);
        }
        for (var guard = 0u; entry != 0 && guard < w.BlockCount; guard++)
        {
            if (!AmigaSmartFileSystemCore.Block(ref p, entry, UndoId, w.Journal, w.BlockCount, w.BlockBytes))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            var next = p.ReadUInt32(w.Journal, 12);
            var target = p.ReadUInt32(w.Journal, 16);
            var image = p.ReadUInt32(w.Journal, 20);
            var originalSum = p.ReadUInt32(w.Journal, 24);
            if (target < w.ReservedBlocks || target >= w.BlockCount - 1 ||
                target == marker || image < w.ReservedBlocks ||
                image >= w.BlockCount - 1 || image == entry ||
                !p.ReadBlock(image, w.Data) || BlockSum(ref p, w.Data, w.BlockBytes) != originalSum ||
                !p.WriteBlock(target, w.Data))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            entry = next;
        }
        if (entry != 0 || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        p.Clear(w.Meta, w.BlockBytes);
        p.WriteUInt32(w.Meta, 0, TransactionOk); p.WriteUInt32(w.Meta, 8, marker);
        // The rest of this freshly cleared marker is zero.
        Seal(ref p, w.Meta, 12);
        return (p.WriteBlock(marker, w.Meta) && p.Flush()) || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool Read<T>(ref T p, ref Work w, uint block, uint id, APTR destination)
        where T : struct, IAmigaWritableFileSystemPlatform =>
        AmigaSmartFileSystemCore.Block(ref p, block, id, destination, w.BlockCount, w.BlockBytes) ||
        Fail(ref w, DOS.Error.DiskNotValidated);

    private static bool Prepare<T>(ref T p, ref Work w)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        w.Error = DOS.Error.None; w.FreeDelta = 0; w.PreserveBitmap = false;
        w.RecycledFilesDelta = w.RecycledBlocksDelta = 0;
        w.JournalActive = false; w.JournalHead = 0; w.PublishedHead = 0; w.AdminReservation = 0;
        // ExecuteTrst already recovered and validated the roots before enabling
        // the overlay. Do not repeat that work inside the same operation.
        if (w.RedoStaging) return true;
        if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);
        if (w.Buffered.IsNotNull && p.ReadUInt32(w.Buffered, BufferHeader.GeometryValid) != 0)
        {
            // These roots stay at fixed block numbers when their trees grow.
            // Recovery/media changes invalidate this mount-owned descriptor.
            w.RootContainer = p.ReadUInt32(w.Buffered, BufferHeader.RootContainer); w.BitmapBase = p.ReadUInt32(w.Buffered, BufferHeader.BitmapBase);
            w.AdminContainer = p.ReadUInt32(w.Buffered, BufferHeader.AdminContainer); w.NodeRoot = p.ReadUInt32(w.Buffered, BufferHeader.NodeRoot);
            w.ExtentRoot = p.ReadUInt32(w.Buffered, BufferHeader.ExtentRoot); w.CaseSensitive = p.ReadUInt32(w.Buffered, BufferHeader.CaseSensitive) != 0;
            w.BlockCount = p.ReadUInt32(w.Buffered, BufferHeader.BlockCount); w.BlockBytes = p.ReadUInt32(w.Buffered, BufferHeader.BlockBytes);
            w.ReservedBlocks = p.ReadUInt32(w.Buffered, BufferHeader.ReservedBlocks);
            return true;
        }
        var cleanRoots = false;
        if (w.Buffered.IsNull)
        { if (!RecoverDisk(ref p, ref w, out cleanRoots)) return false; }
        else
        {
            p.WriteUInt32(w.Buffered, BufferHeader.CachedNode, 0);

            w.BlockBytes = p.BlockBytes; w.BlockCount = p.BlockCount;
            w.ReservedBlocks = p.ReservedBlocks;
        }
        // A clean marker confirms that recovery's root selection is still
        // current. A replay may have changed roots, so select them again then.
        // Nothing is cached across public operations or media changes.
        if (!cleanRoots)
        {
            var root = AmigaSmartFileSystemCore.RootView(ref p, w.Meta);
            if (root.IsNull) return Fail(ref w, DOS.Error.DiskNotValidated);
            RootFields(ref p, ref w, root);
        }
        if (w.BitmapBase == 0 || w.AdminContainer == 0 || w.RootContainer == 0 ||
            w.ExtentRoot == 0 || w.NodeRoot == 0)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var container = View(ref p, ref w, w.RootContainer, ObjectId, w.Meta);
        if (container.IsNull || p.ReadUInt32(container, 28) != AmigaSmartFileSystemCore.RootNode ||
            (p.ReadUInt8(container, 48) & 128) == 0 || p.ReadUInt8(container, 49) == 0 ||
            View(ref p, ref w, w.RootContainer + 2, TransactionOk, w.Meta).IsNull)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (w.Buffered.IsNotNull)
        {
            p.WriteUInt32(w.Buffered, BufferHeader.RootContainer, w.RootContainer); p.WriteUInt32(w.Buffered, BufferHeader.BitmapBase, w.BitmapBase);
            p.WriteUInt32(w.Buffered, BufferHeader.AdminContainer, w.AdminContainer); p.WriteUInt32(w.Buffered, BufferHeader.NodeRoot, w.NodeRoot);
            p.WriteUInt32(w.Buffered, BufferHeader.ExtentRoot, w.ExtentRoot); p.WriteUInt32(w.Buffered, BufferHeader.CaseSensitive, w.CaseSensitive ? 1u : 0u);
            p.WriteUInt32(w.Buffered, BufferHeader.BlockCount, w.BlockCount); p.WriteUInt32(w.Buffered, BufferHeader.BlockBytes, w.BlockBytes);
            p.WriteUInt32(w.Buffered, BufferHeader.ReservedBlocks, w.ReservedBlocks); p.WriteUInt32(w.Buffered, BufferHeader.GeometryValid, 1);
        }
        return true;
    }

    private static bool Object<T>(ref T p, ref Work w, uint node, APTR buffer, out int at)
        where T : struct, IAmigaWritableFileSystemPlatform => AmigaSmartFileSystemCore.ObjectFromRoots(ref p, node,
            w.RootContainer, w.NodeRoot, w.BlockCount, w.BlockBytes, buffer, out at);

    private static bool Marker<T>(ref T p, ref Work w, uint id)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        p.Clear(w.Meta, w.BlockBytes);
        p.WriteUInt32(w.Meta, 0, id); p.WriteUInt32(w.Meta, 8, w.RootContainer + 2);
        if (id == TransactionFailed) p.WriteUInt32(w.Meta, 12, w.JournalHead);
        return Put(ref p, ref w, w.RootContainer + 2, w.Meta);
    }

    private static bool Begin<T>(ref T p, ref Work w) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.RedoStaging) return true;
        if (w.Buffered.IsNotNull) { w.JournalActive = true; return true; }
        if (w.Journal.IsNull || !p.IsMapped(w.Journal, w.BlockBytes))
            return Fail(ref w, DOS.Error.NoFreeStore);
        if (w.JournalCursor < w.ReservedBlocks || w.JournalCursor >= w.BlockCount - 1)
            w.JournalCursor = w.ReservedBlocks;
        w.JournalWrapped = false;
        // With staging, only unused data/journal sectors are written until
        // PublishBatch makes the undo chain durable. No empty marker is needed.
        if (w.Batch.IsNull && (!Marker(ref p, ref w, TransactionFailed) || !p.Flush()))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        w.JournalActive = true;
        return true;
    }

    private static bool Finish<T>(ref T p, ref Work w) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Error != DOS.Error.None) return false;
        if (w.FreeDelta != 0 || w.RecycledFilesDelta != 0 || w.RecycledBlocksDelta != 0)
        {
            var root = Edit(ref p, ref w, w.RootContainer, ObjectId, w.Meta);
            if (root.IsNull) return false;
            var oldFree = p.ReadUInt32(root, (int)w.BlockBytes - 28);
            var updated = (long)oldFree + w.FreeDelta;
            if (updated < 0 || updated > w.BlockCount) return Fail(ref w, DOS.Error.DiskNotValidated);
            ReplaceWord(ref p, root, (int)w.BlockBytes - 28, (uint)updated);
            if (!UpdateRecycledCounters(ref p, ref w, root)) return false;
            if (!PutSealed(ref p, ref w, w.RootContainer, root)) return false;
        }
        if (w.RedoStaging) return true;
        if (w.Buffered.IsNotNull && !w.BufferCommitting) { w.JournalActive = false; return true; }
        if (w.Batch.IsNotNull && !PublishBatch(ref p, ref w)) return false;
        if (!p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        w.JournalActive = false;
        if (!Marker(ref p, ref w, TransactionOk) || !p.Flush())
            return Fail(ref w, DOS.Error.DiskNotValidated);
        w.JournalHead = 0; w.PublishedHead = 0;
        return true;
    }

    private static bool BitmapPosition<T>(ref T p, ref Work w, uint block, out uint bitmapBlock, out int offset, out uint mask)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        return BitmapPosition(ref w, block, out bitmapBlock, out offset, out mask);
    }

    private static bool BitmapPosition(ref Work w,
        uint block, out uint bitmapBlock, out int offset, out uint mask)
    {
        bitmapBlock = 0; offset = 0; mask = 0;
        if (block < w.ReservedBlocks || block >= w.BlockCount - 1)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var bits = (w.BlockBytes - 12) * 8;
        var mapIndex = block / bits;
        var index = block % bits;
        bitmapBlock = w.BitmapBase + mapIndex;
        offset = 12 + (int)(index / 32) * 4;
        mask = 1u << (int)(31 - index % 32);
        return bitmapBlock < w.BlockCount || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    // Scan empty bitmap words directly. Recomputing division, bitmap position
    // and bounds for every 32 occupied sectors dominates a mostly-full volume.
    private static uint UsedSpan<T>(ref T p, APTR bitmap, int offset, uint mask, uint bytes)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var word = p.ReadUInt32(bitmap, offset) & (mask - 1);
        var leading = LeadingZeros(mask);
        if (word != 0) return LeadingZeros(word) - leading - 1;
        var skipped = 31u - leading;
        for (var next = offset + 4; next < bytes; next += 4)
        {
            word = p.ReadUInt32(bitmap, next);
            if (word != 0) return skipped + LeadingZeros(word);
            skipped += 32;
        }
        return skipped;
    }

    private static uint LeadingZeros(uint word)
    {
        if (word == 0) return 32;
        uint count = 0;
        if ((word & 0xFFFF0000) == 0) { count += 16; word <<= 16; }
        if ((word & 0xFF000000) == 0) { count += 8; word <<= 8; }
        if ((word & 0xF0000000) == 0) { count += 4; word <<= 4; }
        if ((word & 0xC0000000) == 0) { count += 2; word <<= 2; }
        if ((word & 0x80000000) == 0) count++;
        return count;
    }

    private static bool IsFree<T>(ref T p, ref Work w, uint block, out bool free)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        free = false;
        if (!BitmapPosition(ref p, ref w, block, out var map, out var offset, out var mask) ||
            !Read(ref p, ref w, map, BitmapId, w.Bitmap)) return false;
        free = (p.ReadUInt32(w.Bitmap, offset) & mask) != 0 && !OverwriteReserved(ref p, ref w, block);
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
        if (!BitmapPosition(ref p, ref w, block, out var map, out var offset, out var mask)) return false;
        // On the first edit, mark the allocation in scratch before reserving
        // its undo sectors. Otherwise the reservation could select this block.
        var firstEdit = w.Buffered.IsNotNull &&
            AmigaSmartFileSystemWriteBuffer.Find(ref p, w.Buffered, w.BlockBytes, map).IsNull;
        var bitmap = firstEdit ? (Read(ref p, ref w, map, BitmapId, w.Bitmap) ? w.Bitmap : APTR.Null) :
            Edit(ref p, ref w, map, BitmapId, w.Bitmap);
        if (bitmap.IsNull) return false;
        var old = p.ReadUInt32(bitmap, offset);
        if (((old & mask) != 0) == free) return Fail(ref w, DOS.Error.DiskNotValidated);
        ReplaceWord(ref p, bitmap, offset, free ? old | mask : old & ~mask);
        if (!PutSealed(ref p, ref w, map, bitmap)) return false;
        w.FreeDelta += free ? 1 : -1;
        return true;
    }

    private static uint AllocateData<T>(ref T p, ref Work w, uint preferred)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var low = w.ReservedBlocks;
        var high = w.BlockCount - 1;
        if (preferred >= low && preferred < high &&
            IsFree(ref p, ref w, preferred, out var free) && free)
        {
            if (!Mark(ref p, ref w, preferred, false)) return 0;
            w.AllocationCursor = preferred + 1 < high ? preferred + 1 : low;
            return preferred;
        }
        var start = preferred >= low && preferred < high ? preferred :
            w.AllocationCursor >= low && w.AllocationCursor < high ? w.AllocationCursor : low;
        var cachedMap = uint.MaxValue;
        for (var scanned = 0u; scanned < high - low; scanned++)
        {
            var block = scanned < high - start ? start + scanned : low + (scanned - (high - start));
            if (!BitmapPosition(ref w, block, out var map, out var offset, out var mask)) return 0;
            if (map != cachedMap)
            {
                if (!Read(ref p, ref w, map, BitmapId, w.Bitmap)) return 0;
                cachedMap = map;
            }
            var available = p.ReadUInt32(w.Bitmap, offset);
            if ((available & mask) == 0)
            {
                var skip = UsedSpan(ref p, w.Bitmap, offset, mask, w.BlockBytes);
                if (skip > high - block - 1) skip = high - block - 1;
                if (skip > high - low - scanned - 1) skip = high - low - scanned - 1;
                scanned += skip;
                continue;
            }
            if (!IsFree(ref p, ref w, block, out free)) return 0;
            if (free)
            {
                if (!Mark(ref p, ref w, block, false)) return 0;
                w.AllocationCursor = block + 1 < high ? block + 1 : low;
                return block;
            }
        }
        if (HasOverwriteReservations(ref p, ref w))
        { ReleaseOverwriteReservation(ref p, ref w); return AllocateData(ref p, ref w, preferred); }
        Fail(ref w, DOS.Error.DiskFull); return 0;
    }

    private static uint AllocateAdmin<T>(ref T p, ref Work w)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var container = w.AdminContainer;
        var tail = 0u; var emptyContainer = 0u; var emptyOffset = 0;
        for (var guard = 0u; container != 0 && guard < w.BlockCount; guard++)
        {
            if (!Read(ref p, ref w, container, AdminId, w.Meta)) return 0;
            for (var at = 24; at + 8 <= w.BlockBytes; at += 8)
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
                    if (allocated >= w.BlockCount - 1) { Fail(ref w, DOS.Error.DiskNotValidated); return 0; }
                    p.WriteUInt32(w.Meta, at + 4, used | mask);
                    return Put(ref p, ref w, container, w.Meta) ? allocated : 0;
                }
            }
            tail = container;
            container = p.ReadUInt32(w.Meta, 12);
        }
        if (container != 0) { Fail(ref w, DOS.Error.DiskNotValidated); return 0; }
        if (w.PreserveBitmap) { Fail(ref w, DOS.Error.DiskFull); return 0; }
        if (w.BlockCount < 34) { Fail(ref w, DOS.Error.DiskFull); return 0; }
        uint span = 0;
        for (var candidate = w.ReservedBlocks; candidate <= w.BlockCount - 33; candidate++)
        {
            var found = true;
            for (var i = 0u; i < 32; i++)
            {
                if (!IsFree(ref p, ref w, candidate + i, out var free)) return 0;
                if (!free) { found = false; candidate += i; break; }
            }
            if (found) { span = candidate; break; }
        }
        if (span == 0 && HasOverwriteReservations(ref p, ref w))
        { ReleaseOverwriteReservation(ref p, ref w); return AllocateAdmin(ref p, ref w); }
        if (span == 0) { Fail(ref w, DOS.Error.DiskFull); return 0; }
        // Reserve the complete span before its first bitmap undo allocation.
        w.AdminReservation = span;
        for (var i = 0u; i < 32; i++)
            if (!Mark(ref p, ref w, span + i, false)) { w.AdminReservation = 0; return 0; }
        w.AdminReservation = 0;
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
        p.Clear(w.Meta, w.BlockBytes);
        p.WriteUInt32(w.Meta, 0, AdminId); p.WriteUInt32(w.Meta, 8, span);
        p.WriteUInt32(w.Meta, 16, tail); p.WriteUInt8(w.Meta, 20, 2); // log2(4 bitmap bytes)
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
            if (!w.CaseSensitive)
            {
                var asciiLower = value >= 97 && value <= 122;
                var latinLower = value >= 224 && value <= 246 || value >= 248 && value <= 254;
                if (asciiLower || latinLower) value = (byte)(value - 32);
            }
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
        var volumePrefix = false;
        for (var i = 0u; i < r.Length; i++)
        {
            var separator = p.ReadUInt8(r.Name, (int)i);
            if (separator is (byte)'/' or (byte)':')
            {
                segment = i + 1;
                volumePrefix = separator == (byte)':';
            }
        }
        if (segment != 0)
        {
            if (volumePrefix) r.Directory = AmigaSmartFileSystemCore.RootNode;
            else
            {
                var parent = AmigaSmartFileSystemCore.Resolve(ref p, r.Directory, r.Name, segment, w.Meta, out var error);
                if (parent == 0) return Fail(ref w, error);
                r.Directory = parent;
            }
            r.Name = APTR.FromPointer(r.Name.Raw + segment); r.Length -= segment;
        }
        if (r.Length == 0 || r.Length > 100) return Fail(ref w, DOS.Error.InvalidComponentName);
        for (var i = 0u; i < r.Length; i++)
        {
            var value = p.ReadUInt8(r.Name, (int)i);
            if (value < 32 || value == ':' || value == '/') return Fail(ref w, DOS.Error.InvalidComponentName);
        }
        var parentView = AmigaSmartFileSystemCore.ObjectViewFromRoots(ref p, r.Directory, w.RootContainer,
            w.NodeRoot, w.BlockCount, w.BlockBytes, w.Meta, out var parentAt);
        if (parentView.IsNull)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var directory = (p.ReadUInt8(parentView, parentAt + 24) & 128) != 0;
        return directory || Fail(ref w, DOS.Error.ObjectWrongType);
    }

    private static bool ContainerEnd<T>(ref T p, ref Work w, APTR buffer, out int end)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        end = 24;
        var limit = (int)w.BlockBytes;
        while (end + 27 <= limit)
        {
            if (p.ReadUInt32(buffer, end + 4) == 0) return true;
            var nameEnd = end + 25;
            while (nameEnd < limit && p.ReadUInt8(buffer, nameEnd) != 0) nameEnd++;
            if (nameEnd == limit) break;
            var commentEnd = nameEnd + 1;
            while (commentEnd < limit && p.ReadUInt8(buffer, commentEnd) != 0) commentEnd++;
            if (commentEnd == limit) break;
            var next = (commentEnd + 2) & ~1;
            if (next <= end) break;
            end = next;
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool FindFreeNodeIn<T>(ref T p, ref Work w, uint block, uint depth, bool grow,
        out uint node, out uint leaf, out int slot)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        node = leaf = 0; slot = 0;
        if (depth >= 8) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!Read(ref p, ref w, block, NodeId, w.Meta)) return false;
        var first = p.ReadUInt32(w.Meta, 12);
        var stride = p.ReadUInt32(w.Meta, 16);
        if (stride == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (stride == 1)
        {
            for (var at = 20; at + 10 <= w.BlockBytes; at += 10)
            {
                var candidate = first + (uint)((at - 20) / 10);
                if (candidate == 0 || p.ReadUInt32(w.Meta, at) != 0) continue;
                node = candidate; leaf = block; slot = at; return true;
            }
            return false;
        }
        var shift = 0;
        while ((1u << shift) < w.BlockBytes) shift++;
        shift -= 5;
        var indexCapacity = (w.BlockBytes - 20) / 4;
        for (var index = 0; index < indexCapacity; index++)
        {
            if (!Read(ref p, ref w, block, NodeId, w.Meta)) return false;
            var pointer = p.ReadUInt32(w.Meta, 20 + (int)index * 4);
            if (pointer == 0)
            {
                if (!grow) continue;
                var childFirst = (ulong)first + (uint)index * (ulong)stride;
                if (childFirst > uint.MaxValue) break;
                var leafCapacity = (w.BlockBytes - 20) / 10;
                var childStride = stride == leafCapacity ? 1u : stride / indexCapacity;
                if (childStride == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
                var child = AllocateAdmin(ref p, ref w);
                if (child == 0) return false;
                if (child > uint.MaxValue >> shift) return Fail(ref w, DOS.Error.DiskFull);
                if (!InitializeNodeBranch(ref p, ref w, child, (uint)childFirst, childStride, depth + 1) ||
                    !Read(ref p, ref w, block, NodeId, w.Meta)) return false;
                pointer = child << shift;
                p.WriteUInt32(w.Meta, 20 + (int)index * 4, pointer);
                if (!Put(ref p, ref w, block, w.Meta)) return false;
            }
            if (FindFreeNodeIn(ref p, ref w, pointer >> shift, depth + 1, grow, out node, out leaf, out slot)) return true;
            if (w.Error != DOS.Error.None) return false;
        }
        return false;
    }

    private static bool FindFreeNode<T>(ref T p, ref Work w, out uint node, out uint leaf, out int slot)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (FindFreeNodeIn(ref p, ref w, w.NodeRoot, 0, false, out node, out leaf, out slot)) return true;
        if (w.Error != DOS.Error.None) return false;
        return Fail(ref w, DOS.Error.DiskFull);
    }

    private static bool InitializeNodeBranch<T>(ref T p, ref Work w, uint block, uint first, uint stride, uint depth)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (depth >= 8 || stride == 0) return Fail(ref w, DOS.Error.DiskFull);
        p.Clear(w.Meta, w.BlockBytes);
        p.WriteUInt32(w.Meta, 0, NodeId); p.WriteUInt32(w.Meta, 8, block);
        p.WriteUInt32(w.Meta, 12, first); p.WriteUInt32(w.Meta, 16, stride);
        if (!Put(ref p, ref w, block, w.Meta)) return false;
        if (stride == 1) return true;
        var leafCapacity = (w.BlockBytes - 20) / 10;
        var indexCapacity = (w.BlockBytes - 20) / 4;
        var childStride = stride == leafCapacity ? 1u : stride / indexCapacity;
        if (childStride == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        var child = AllocateAdmin(ref p, ref w);
        if (child == 0 || !InitializeNodeBranch(ref p, ref w, child, first, childStride, depth + 1)) return false;
        if (!Read(ref p, ref w, block, NodeId, w.Meta)) return false;
        var shift = 0;
        while ((1u << shift) < w.BlockBytes) shift++;
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
        var leafCapacity = (w.BlockBytes - 20) / 10;
        var indexCapacity = (w.BlockBytes - 20) / 4;
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
        while ((1u << shift) < w.BlockBytes) shift++;
        shift -= 5;
        if (saved > uint.MaxValue >> shift || branch > uint.MaxValue >> shift)
            return Fail(ref w, DOS.Error.DiskFull);
        p.Clear(w.Meta, w.BlockBytes);
        p.WriteUInt32(w.Meta, 0, NodeId); p.WriteUInt32(w.Meta, 8, w.NodeRoot);
        p.WriteUInt32(w.Meta, 12, first); p.WriteUInt32(w.Meta, 16, newStride);
        p.WriteUInt32(w.Meta, 20, (saved << shift) | 1);
        p.WriteUInt32(w.Meta, 24, branch << shift);
        return Put(ref p, ref w, w.NodeRoot, w.Meta);
    }

    private static bool InsertObject<T>(ref T p, ref Work w, ref Request r, bool directory, uint node,
        APTR preserved, int preservedAt, int commentAt, uint commentLength,
        out uint container, out uint hashTable, out int recordAt, bool recycledName = false)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        container = hashTable = 0; recordAt = 0;
        if (!Object(ref p, ref w, r.Directory, w.Meta, out var parentAt))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        hashTable = p.ReadUInt32(w.Meta, parentAt + 12);
        var first = p.ReadUInt32(w.Meta, parentAt + 16);
        var entryBytes = ((int)r.Length + (int)commentLength + 27 + 1) & ~1;
        var at = 24;
        if (first == 0)
        {
            first = AllocateAdmin(ref p, ref w);
            if (first == 0) return false;
            if (!Object(ref p, ref w, r.Directory, w.Meta, out parentAt))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            p.WriteUInt32(w.Meta, parentAt + 16, first);
            if (!Put(ref p, ref w, p.ReadUInt32(w.Meta, 8), w.Meta)) return false;
            p.Clear(w.Meta, w.BlockBytes);
            p.WriteUInt32(w.Meta, 0, ObjectId); p.WriteUInt32(w.Meta, 8, first);
            p.WriteUInt32(w.Meta, 12, r.Directory);
            container = first;
        }
        else
        {
            var current = first;
            for (var guard = 0u; guard < w.BlockCount; guard++)
            {
                if (!Read(ref p, ref w, current, ObjectId, w.Meta) ||
                    p.ReadUInt32(w.Meta, 12) != r.Directory || !ContainerEnd(ref p, ref w, w.Meta, out var end)) return false;
                if (end + entryBytes + 27 <= w.BlockBytes) { container = current; at = end; break; }
                var next = p.ReadUInt32(w.Meta, 16);
                if (next != 0) { current = next; continue; }
                var allocated = AllocateAdmin(ref p, ref w);
                if (allocated == 0 || !Read(ref p, ref w, current, ObjectId, w.Meta)) return false;
                p.WriteUInt32(w.Meta, 16, allocated);
                if (!Put(ref p, ref w, current, w.Meta)) return false;
                p.Clear(w.Meta, w.BlockBytes);
                p.WriteUInt32(w.Meta, 0, ObjectId); p.WriteUInt32(w.Meta, 8, allocated);
                p.WriteUInt32(w.Meta, 12, r.Directory); p.WriteUInt32(w.Meta, 20, current);
                container = allocated; break;
            }
            if (container == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        if (at + entryBytes > w.BlockBytes) return Fail(ref w, DOS.Error.DiskFull);
        for (var i = 0; i < entryBytes; i++) p.WriteUInt8(w.Meta, at + i, 0);
        if (preserved.IsNotNull)
            for (var i = 0; i < 25; i++) p.WriteUInt8(w.Meta, at + i, p.ReadUInt8(preserved, preservedAt + i));
        else p.WriteUInt32(w.Meta, at + 8, 15);
        p.WriteUInt32(w.Meta, at + 4, node);
        if (preserved.IsNull) p.WriteUInt8(w.Meta, at + 24, directory ? (byte)128 : (byte)0);
        for (var i = 0u; i < r.Length; i++) p.WriteUInt8(w.Meta, at + 25 + (int)i,
            recycledName ? RecycledNameByte(ref p, preserved, preservedAt + 25, i, r.Length, r.Position) : p.ReadUInt8(r.Name, (int)i));
        for (var i = 0u; i < commentLength; i++)
            p.WriteUInt8(w.Meta, at + 26 + (int)r.Length + (int)i, p.ReadUInt8(preserved, commentAt + (int)i));
        recordAt = at;
        return Put(ref p, ref w, container, w.Meta);
    }

    private static bool CreateOperation<T>(ref T p, ref Work w, ref Request r, bool directory)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!Prepare(ref p, ref w) || !Parent(ref p, ref w, ref r)) return false;
        if (r.Directory == RecycledNode)
        {
            if (!RecyclingEnabled(ref p, ref w, out var enabled)) return false;
            if (enabled) return Fail(ref w, DOS.Error.ObjectInUse);
        }
        var existing = AmigaSmartFileSystemCore.Resolve(ref p, r.Directory, r.Name, r.Length, w.Meta, out var error);
        if (existing != 0) return Fail(ref w, DOS.Error.ObjectExists);
        if (error != DOS.Error.ObjectNotFound) return Fail(ref w, error);
        var foundNode = FindFreeNode(ref p, ref w, out var node, out var leaf, out var slot);
        if (!foundNode && w.Error != DOS.Error.DiskFull) return false;
        if (!foundNode) w.Error = DOS.Error.None;
        if (!Begin(ref p, ref w)) return false;
        if (!foundNode && !FindFreeNodeIn(ref p, ref w, w.NodeRoot, 0, true, out node, out leaf, out slot))
        {
            if (w.Error != DOS.Error.None || !GrowNodeRoot(ref p, ref w) ||
                !FindFreeNode(ref p, ref w, out node, out leaf, out slot)) return false;
        }
        if (!InsertObject(ref p, ref w, ref r, directory, node, APTR.Null, 0, 0, 0,
            out var container, out var hashTable, out var recordAt)) return false;
        var hash = Hash(ref p, ref w, r.Name, r.Length);
        var oldHead = 0u;
        var hashOffset = 0;
        if (hashTable != 0)
        {
            var hashView = View(ref p, ref w, hashTable, HashId, w.Meta);
            if (hashView.IsNull) return false;
            hashOffset = 16 + (hash % (int)((w.BlockBytes - 16) / 4)) * 4;
            oldHead = p.ReadUInt32(hashView, hashOffset);
        }
        var nodeImage = Edit(ref p, ref w, leaf, NodeId, w.Meta);
        if (nodeImage.IsNull) return false;
        ReplaceWord(ref p, nodeImage, slot, container); ReplaceWord(ref p, nodeImage, slot + 4, oldHead);
        ReplaceHalf(ref p, nodeImage, slot + 8, hash);
        if (!PutSealed(ref p, ref w, leaf, nodeImage)) return false;
        if (hashTable != 0)
        {
            var hashImage = Edit(ref p, ref w, hashTable, HashId, w.Meta);
            if (hashImage.IsNull) return false;
            ReplaceWord(ref p, hashImage, hashOffset, node);
            if (!PutSealed(ref p, ref w, hashTable, hashImage)) return false;
        }
        if (!Finish(ref p, ref w)) return false;
        if (w.Buffered.IsNotNull)
        {
            // The next Write can reuse the record just inserted. All other
            // mutations invalidate this hint before they can move records.
            p.WriteUInt32(w.Buffered, BufferHeader.CachedNode, node); p.WriteUInt32(w.Buffered, BufferHeader.CachedContainer, container);
            p.WriteUInt32(w.Buffered, BufferHeader.CachedRecordOffset, (uint)recordAt);
        }
        r.Node = node; return true;
    }

    private static bool FileBlock<T>(ref T p, ref Work w, uint first, uint index, out uint diskBlock,
        out uint lastKey, out uint lastSize, out uint lastStart, out uint lastLeaf, out int lastOffset)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        diskBlock = lastKey = lastSize = lastStart = 0;
        lastLeaf = 0; lastOffset = 0;
        var key = first; var start = 0u;
        for (var guard = 0u; key != 0 && guard < w.BlockCount; guard++)
        {
            var view = AmigaSmartFileSystemCore.ExtentRecordView(ref p, w.ExtentRoot, key, w.Meta,
                w.BlockCount, w.BlockBytes, out lastLeaf, out lastOffset);
            if (view.IsNull)
                return Fail(ref w, DOS.Error.DiskNotValidated);
            var count = p.ReadUInt16(view, lastOffset + 12);
            var next = p.ReadUInt32(view, lastOffset + 4);
            if (count == 0) return Fail(ref w, DOS.Error.DiskNotValidated);
            lastKey = key; lastSize = count; lastStart = start;
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
        for (var depth = 0u; depth < w.BlockCount; depth++)
        {
            if (!Read(ref p, ref w, leaf, TreeId, w.Meta)) return false;
            var count = p.ReadUInt16(w.Meta, 12); var isLeaf = p.ReadUInt8(w.Meta, 14) != 0;
            var stride = p.ReadUInt8(w.Meta, 15);
            if ((!isLeaf && count == 0) || stride < (isLeaf ? 14 : 8) ||
                16u + (uint)count * stride > w.BlockBytes)
                return Fail(ref w, DOS.Error.DiskNotValidated);
            if (isLeaf) return true;
            var selected = 16;
            for (var i = 1; i < count && p.ReadUInt32(w.Meta, 16 + i * stride) <= key; i++)
                selected = 16 + i * stride;
            leaf = p.ReadUInt32(w.Meta, selected + 4);
            if (leaf == 0 || leaf >= w.BlockCount) return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static void InsertBnode<T>(ref T p, APTR buffer, int count, int stride, uint key,
        uint value, uint previous, bool isLeaf) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var index = 0;
        while (index < count && p.ReadUInt32(buffer, 16 + index * stride) < key) index++;
        var offset = 16 + index * stride;
        var at = 16 + count * stride;
        if ((stride & 1) == 0 && (buffer.Raw & 1) == 0)
        {
            // The source and destination overlap. Move backwards, with aligned
            // longwords/words, so insertion cannot overwrite an unread record.
            while (at - offset >= 4)
            { at -= 4; p.WriteUInt32(buffer, at + stride, p.ReadUInt32(buffer, at)); }
            if (at != offset) { at -= 2; p.WriteUInt16(buffer, at + stride, p.ReadUInt16(buffer, at)); }
        }
        else while (at != offset) { at--; p.WriteUInt8(buffer, at + stride, p.ReadUInt8(buffer, at)); }
        p.Clear(APTR.FromPointer(buffer.Raw + (uint)offset), (uint)stride);
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
            16u + (uint)count * stride > w.BlockBytes)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!isLeaf)
        {
            if (key < p.ReadUInt32(w.Meta, 16))
            {
                p.WriteUInt32(w.Meta, 16, key);
                if (!Put(ref p, ref w, block, w.Meta)) return false;
            }
            var selected = 16;
            for (var i = 1; i < count && p.ReadUInt32(w.Meta, 16 + i * stride) <= key; i++)
                selected = 16 + i * stride;
            var child = p.ReadUInt32(w.Meta, selected + 4);
            if (child == 0 || child >= w.BlockCount ||
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
        if (16u + (uint)(count + 1) * stride <= w.BlockBytes)
        {
            InsertBnode(ref p, w.Meta, count, stride, key, value, previous, isLeaf);
            return Put(ref p, ref w, block, w.Meta);
        }
        var right = AllocateAdmin(ref p, ref w);
        if (right == 0 || !Read(ref p, ref w, block, TreeId, w.Meta)) return false;
        p.Clear(w.Data, w.BlockBytes);
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
        p.Clear(w.Meta, w.BlockBytes);
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
        if (previous == 0 || (previous & 0x80000000) != 0) return true;
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

    private static bool GrowExtent<T>(ref T p, ref Work w, uint key, uint newBlock, uint additionalBlocks = 1)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var original = AmigaSmartFileSystemCore.ExtentRecordView(ref p, w.ExtentRoot, key, w.Meta,
            w.BlockCount, w.BlockBytes, out var leaf, out var at);
        if (original.IsNull) return Fail(ref w, DOS.Error.DiskNotValidated);
        var blocks = p.ReadUInt16(original, at + 12);
        if (additionalBlocks == 0 || additionalBlocks > ushort.MaxValue - blocks ||
            key + blocks != newBlock) return Fail(ref w, DOS.Error.DiskNotValidated);
        return GrowExtentAt(ref p, ref w, leaf, at, blocks + additionalBlocks);
    }

    // Use a record location only within the operation that validated it, and
    // before any extent insertion/removal can move it. Edit still captures the
    // checkpoint image before changing the checked leaf.
    private static bool GrowExtentAt<T>(ref T p, ref Work w, uint leaf, int at, uint blocks)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var image = Edit(ref p, ref w, leaf, TreeId, w.Meta);
        if (image.IsNull) return false;
        ReplaceHalf(ref p, image, at + 12, (ushort)blocks);
        return PutSealed(ref p, ref w, leaf, image);
    }

    // A full-block append can reserve its contiguous run with one bitmap
    // update and extend its extent once. The bitmap and extent remain covered
    // by the same undo transaction as the ordinary block-by-block path.
    private static bool AppendContiguousRun<T>(ref T p, ref Work w, ref Request r,
        uint first, uint oldBlocks, out bool handled, out uint runStart)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!AppendPreferredRun(ref p, ref w, ref r, first, oldBlocks, out handled, out runStart)) return false;
        if (handled) return true;
        var blocks = r.Length / w.BlockBytes;
        if (blocks < 2 || blocks > 65536 / w.BlockBytes || w.JournalHead != 0) return true;
        // A blocked continuation is not a reason to allocate/update every sector
        // separately. Search for another complete run before the scalar fallback.
        if (!FindFreeRun(ref p, ref w, w.AllocationCursor, blocks, blocks, true, out var start, out var count)) return false;
        if (count == 0) return true;
        if (!FileBlock(ref p, ref w, first, oldBlocks, out var existing,
            out var lastKey, out var lastSize, out _, out _, out _)) return false;
        if (existing != 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!MarkDataRun(ref p, ref w, start, count)) return false;
        if (lastKey != 0 && lastKey + lastSize == start && count <= ushort.MaxValue - lastSize)
        {
            if (!GrowExtent(ref p, ref w, lastKey, start, count)) return false;
        }
        else if (!InsertExtent(ref p, ref w, start, lastKey) ||
            !GrowExtent(ref p, ref w, start, start + 1, count - 1)) return false;
        if ((r.Buffer.Raw & 3) == 0)
        {
            if (!WriteNewDataRun(ref p, ref w, start, count, r.Buffer)) return false;
        }
        else for (var block = 0u; block < count; block++)
        {
            p.Copy(APTR.FromPointer(r.Buffer.Raw + block * w.BlockBytes), w.Data, w.BlockBytes);
            if (!p.WriteBlock(start + block, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        w.AllocationCursor = start + count < w.BlockCount - 1 ? start + count : w.ReservedBlocks;
        runStart = start; handled = true;
        return true;
    }

    private static bool AppendPreferredRun<T>(ref T p, ref Work w, ref Request r,
        uint first, uint oldBlocks, out bool handled, out uint runStart)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        handled = false; runStart = 0;
        var blockBytes = w.BlockBytes;
        var blocks = r.Length / blockBytes;
        if (blocks < 2 || blocks > 65536 / blockBytes || w.JournalHead != 0) return true;
        if (!FileBlock(ref p, ref w, first, oldBlocks, out var existing,
            out var lastKey, out var lastSize, out _, out var lastLeaf, out var lastOffset)) return false;
        if (existing != 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        var low = w.ReservedBlocks;
        var high = w.BlockCount - 1;
        var start = lastKey == 0 ? w.AllocationCursor : lastKey + lastSize;
        if (start < low || start >= high || blocks > high - start ||
            (lastKey != 0 && blocks > ushort.MaxValue - lastSize)) return true;
        if (!BitmapPosition(ref p, ref w, start, out var map, out var firstOffset, out _)) return false;
        // Bitmap payloads contain complete words. The checked byte offset and
        // low five block bits also give the remaining span in this map.
        if (blocks > (blockBytes - (uint)firstOffset) * 8 - (start & 31)) return true;
        APTR checkpointBitmap = APTR.Null;
        if (w.Buffered.IsNotNull)
        {
            // Check the run against reservations once, rather than searching
            // the entire dirty list for every sector in a contiguous append.
            var count = AmigaSmartFileSystemWriteBuffer.Count(ref p, w.Buffered);
            var slot = AmigaSmartFileSystemWriteBuffer.First(w.Buffered);
            for (var i = 0u; i < count; i++, slot = AmigaSmartFileSystemWriteBuffer.Next(slot, blockBytes))
            {
                var key = p.ReadUInt32(slot, BufferEntry.Block); var undo = p.ReadUInt32(slot, BufferEntry.UndoImage); var link = p.ReadUInt32(slot, BufferEntry.UndoEntry);
                if ((key >= start && key - start < blocks) || (undo >= start && undo - start < blocks) ||
                    (link >= start && link - start < blocks)) return true;
                if (key == map) checkpointBitmap = AmigaSmartFileSystemWriteBuffer.Checkpoint(slot, blockBytes);
            }
        }
        var bitmap = View(ref p, ref w, map, BitmapId, w.Bitmap);
        if (bitmap.IsNull) return false;
        var offset = firstOffset; var withinWord = start & 31;
        var remainingBits = blocks;
        while (remainingBits != 0)
        {
            var bits = 32u - withinWord;
            if (bits > remainingBits) bits = remainingBits;
            var runMask = (uint.MaxValue >> (int)withinWord) & (uint.MaxValue << (int)(32 - withinWord - bits));
            if ((p.ReadUInt32(bitmap, offset) & runMask) != runMask) return true;
            if (checkpointBitmap.IsNotNull && (p.ReadUInt32(checkpointBitmap, offset) & runMask) != runMask) return true;
            remainingBits -= bits; offset += 4; withinWord = 0;
        }
        if (HasOverwriteReservations(ref p, ref w))
            for (var i = 0u; i < blocks; i++)
                if (OverwriteReserved(ref p, ref w, start + i)) return true;
        offset = firstOffset; withinWord = start & 31;
        // A newly staged bitmap reserves its undo sectors in Put, after all
        // bits in this run have been cleared. Existing entries already own theirs.
        if (w.RedoStaging || checkpointBitmap.IsNotNull) bitmap = Edit(ref p, ref w, map, BitmapId, w.Bitmap);
        else if (bitmap != w.Bitmap)
        {
            // Cache views are read-only and expire at the next block access.
            // Keep the old image intact until Put captures its checkpoint.
            p.Copy(bitmap, w.Bitmap, blockBytes); bitmap = w.Bitmap;
        }
        if (bitmap.IsNull) return false;
        remainingBits = blocks;
        while (remainingBits != 0)
        {
            var bits = 32u - withinWord;
            if (bits > remainingBits) bits = remainingBits;
            var runMask = (uint.MaxValue >> (int)withinWord) & (uint.MaxValue << (int)(32 - withinWord - bits));
            ReplaceWord(ref p, bitmap, offset, p.ReadUInt32(bitmap, offset) & ~runMask);
            remainingBits -= bits; offset += 4; withinWord = 0;
        }
        if (!PutSealed(ref p, ref w, map, bitmap)) return false;
        w.FreeDelta -= (int)blocks;
        if (lastKey == 0)
        {
            if (!InsertExtent(ref p, ref w, start, 0) ||
                (blocks > 1 && !GrowExtent(ref p, ref w, start, start + 1, blocks - 1))) return false;
        }
        else if (!GrowExtentAt(ref p, ref w, lastLeaf, lastOffset, lastSize + blocks)) return false;
        if ((r.Buffer.Raw & 3) == 0)
        {
            if (!WriteNewDataRun(ref p, ref w, start, blocks, r.Buffer)) return false;
        }
        else for (var block = 0u; block < blocks; block++)
        {
            var source = APTR.FromPointer(r.Buffer.Raw + block * blockBytes);
            // Native sector I/O copies longwords, so only pass it a suitably
            // aligned caller buffer. Other buffers retain the byte-copy path.
            if (((source.Raw | blockBytes) & 3) != 0)
            {
                p.Copy(source, w.Data, blockBytes);
                source = w.Data;
            }
            if (!p.WriteBlock(start + block, source))
                return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        w.AllocationCursor = start + blocks < high ? start + blocks : low;
        runStart = start; handled = true;
        return true;
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
        if (stride < 8 || 16u + (uint)count * stride > w.BlockBytes)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var selected = 0;
        for (var i = 1; i < count && p.ReadUInt32(w.Meta, 16 + i * stride) <= key; i++) selected = i;
        var child = p.ReadUInt32(w.Meta, 16 + selected * stride + 4);
        if (child == 0 || child >= w.BlockCount ||
            !PruneExtent(ref p, ref w, child, key, depth + 1, out var childEmpty)) return false;
        if (!childEmpty) return true;
        if (!Read(ref p, ref w, block, TreeId, w.Meta)) return false;
        var end = 16 + count * stride;
        var at = 16 + selected * stride;
        var lowerBound = p.ReadUInt32(w.Meta, 16);
        for (var offset = at; offset < end - stride; offset++)
            p.WriteUInt8(w.Meta, offset, p.ReadUInt8(w.Meta, offset + stride));
        for (var offset = end - stride; offset < end; offset++) p.WriteUInt8(w.Meta, offset, 0);
        count--;
        if (selected == 0 && count != 0) p.WriteUInt32(w.Meta, 16, lowerBound);
        if (count == 0 && depth == 0)
        {
            p.WriteUInt8(w.Meta, 14, 1); p.WriteUInt8(w.Meta, 15, 14);
        }
        p.WriteUInt16(w.Meta, 12, (ushort)count);
        if (!Put(ref p, ref w, block, w.Meta) || !FreeAdmin(ref p, ref w, child)) return false;
        empty = count == 0;
        return true;
    }

    private static bool RemoveExtent<T>(ref T p, ref Work w, uint key, out uint next, bool freeData = true)
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
            if (blocks == 0 || key > w.BlockCount - blocks) return Fail(ref w, DOS.Error.DiskNotValidated);
            var end = 16 + count * stride;
            for (var offset = at; offset < end - stride; offset++)
                p.WriteUInt8(w.Meta, offset, p.ReadUInt8(w.Meta, offset + stride));
            for (var offset = end - stride; offset < end; offset++) p.WriteUInt8(w.Meta, offset, 0);
            p.WriteUInt16(w.Meta, 12, (ushort)(count - 1));
            if (!Put(ref p, ref w, leaf, w.Meta)) return false;
            if (count == 1 && leaf != w.ExtentRoot &&
                !PruneExtent(ref p, ref w, w.ExtentRoot, key, 0, out _)) return false;
            if (previous != 0 && (previous & 0x80000000) == 0)
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
            if (freeData) for (var block = key; block < key + blocks; block++) if (!Mark(ref p, ref w, block, true)) return false;
            return true;
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool FreeFile<T>(ref T p, ref Work w, uint first)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        for (var guard = 0u; first != 0 && guard < w.BlockCount; guard++)
        {
            if (!RemoveExtent(ref p, ref w, first, out var next)) return false;
            first = next;
        }
        return first == 0 || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool TruncateOperation<T>(ref T p, ref Work w, uint node)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        ReleaseOverwriteReservation(ref p, ref w, node);
        if (!Prepare(ref p, ref w) || !Object(ref p, ref w, node, w.Meta, out var at))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if ((p.ReadUInt8(w.Meta, at + 24) & (128 | 64)) != 0) return Fail(ref w, DOS.Error.ObjectWrongType);
        if (((p.ReadUInt32(w.Meta, at + 8) ^ 15) & 4) != 0) return Fail(ref w, DOS.Error.WriteProtected);
        var first = p.ReadUInt32(w.Meta, at + 12);
        if (first == 0 && p.ReadUInt32(w.Meta, at + 16) == 0) return true;
        if (!RecycledSizeChange(ref p, ref w, p.ReadUInt32(w.Meta, 12), p.ReadUInt32(w.Meta, at + 16), 0)) return false;
        if (!Begin(ref p, ref w) || !FreeFile(ref p, ref w, first) ||
            !Object(ref p, ref w, node, w.Meta, out at)) return false;
        p.WriteUInt32(w.Meta, at + 12, 0); p.WriteUInt32(w.Meta, at + 16, 0);
        return Put(ref p, ref w, p.ReadUInt32(w.Meta, 8), w.Meta) && Finish(ref p, ref w);
    }

    private static bool NodeSlot<T>(ref T p, ref Work w, uint node, APTR buffer, out uint leaf, out int slot)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        leaf = w.NodeRoot; slot = 0;
        var shift = 0;
        while ((1u << shift) < w.BlockBytes) shift++;
        shift -= 5;
        for (var depth = 0u; depth < w.BlockCount; depth++)
        {
            if (!Read(ref p, ref w, leaf, NodeId, buffer)) return false;
            var first = p.ReadUInt32(buffer, 12); var stride = p.ReadUInt32(buffer, 16);
            if (stride == 0 || node < first) return Fail(ref w, DOS.Error.DiskNotValidated);
            var index = (node - first) / stride;
            if (stride == 1)
            {
                if (index >= (w.BlockBytes - 20) / 10) return Fail(ref w, DOS.Error.DiskNotValidated);
                slot = 20 + (int)index * 10;
                return p.ReadUInt32(buffer, slot) != 0 || Fail(ref w, DOS.Error.DiskNotValidated);
            }
            if (index >= (w.BlockBytes - 20) / 4) return Fail(ref w, DOS.Error.DiskNotValidated);
            leaf = p.ReadUInt32(buffer, 20 + (int)index * 4) >> shift;
        }
        return Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool RemoveHash<T>(ref T p, ref Work w, uint parent, uint node, ushort hash)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!NodeSlot(ref p, ref w, node, w.Data, out _, out var nodeAt)) return false;
        var successor = p.ReadUInt32(w.Data, nodeAt + 4);
        if (!Object(ref p, ref w, parent, w.Meta, out var at))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        var table = p.ReadUInt32(w.Meta, at + 12);
        if (table == 0) return true;
        if (!Read(ref p, ref w, table, HashId, w.Meta)) return false;
        var hashAt = 16 + (hash % (int)((w.BlockBytes - 16) / 4)) * 4;
        var current = p.ReadUInt32(w.Meta, hashAt);
        if (current == node)
        {
            p.WriteUInt32(w.Meta, hashAt, successor);
            return Put(ref p, ref w, table, w.Meta);
        }
        for (var guard = 0u; current != 0 && guard < w.BlockCount; guard++)
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
        for (var guard = 0u; container != 0 && guard < w.BlockCount; guard++)
        {
            if (!Read(ref p, ref w, container, AdminId, w.Meta)) return false;
            for (var at = 24; at + 8 <= w.BlockBytes; at += 8)
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
        var image = Edit(ref p, ref w, container, ObjectId, w.Meta);
        if (image.IsNull || p.ReadUInt32(image, at + 4) != node)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (!ContainerEnd(ref p, ref w, image, out var endAt)) return false;
        var comment = at + 25;
        while (comment < endAt && p.ReadUInt8(image, comment) != 0) comment++;
        comment++;
        while (comment < endAt && p.ReadUInt8(image, comment) != 0) comment++;
        var after = (comment + 2) & ~1;
        if (after > endAt) return Fail(ref w, DOS.Error.DiskNotValidated);
        var count = after - at;
        for (var offset = at; offset < endAt - count; offset++)
            p.WriteUInt8(image, offset, p.ReadUInt8(image, offset + count));
        for (var offset = endAt - count; offset < endAt; offset++) p.WriteUInt8(image, offset, 0);
        return Put(ref p, ref w, container, image);
    }

    private static bool RemoveOperation<T>(ref T p, ref Work w, uint node)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        ReleaseOverwriteReservation(ref p, ref w, node);
        if (node == AmigaSmartFileSystemCore.RootNode) return Fail(ref w, DOS.Error.ObjectInUse);
        if (!Prepare(ref p, ref w) || !Object(ref p, ref w, node, w.Meta, out var at))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (((p.ReadUInt32(w.Meta, at + 8) ^ 15) & 1) != 0) return Fail(ref w, DOS.Error.DeleteProtected);
        var bits = p.ReadUInt8(w.Meta, at + 24);
        if ((bits & 2) != 0) return Fail(ref w, DOS.Error.ObjectInUse);
        var first = p.ReadUInt32(w.Meta, at + ((bits & 128) != 0 ? 16 : 12));
        var directoryHash = (bits & 128) != 0 ? p.ReadUInt32(w.Meta, at + 12) : 0u;
        var objectContainer = p.ReadUInt32(w.Meta, 8);
        var parent = p.ReadUInt32(w.Meta, 12);
        var size = p.ReadUInt32(w.Meta, at + 16);
        var nameLength = 0u;
        while (at + 25 + nameLength < w.BlockBytes && p.ReadUInt8(w.Meta, at + 25 + (int)nameLength) != 0)
            nameLength++;
        var hash = Hash(ref p, ref w, APTR.FromPointer(w.Meta.Raw + (uint)at + 25), nameLength);
        if ((bits & 128) != 0)
        {
            var block = first;
            for (var guard = 0u; block != 0 && guard < w.BlockCount; guard++)
            {
                if (!Read(ref p, ref w, block, ObjectId, w.Data) || !ContainerEnd(ref p, ref w, w.Data, out var end)) return false;
                if (end != 24) return Fail(ref w, DOS.Error.DirectoryNotEmpty);
                block = p.ReadUInt32(w.Data, 16);
            }
            if (block != 0) return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        else if ((bits & 64) != 0) return Fail(ref w, DOS.Error.ObjectWrongType);
        else
        {
            if (!RecyclingEnabled(ref p, ref w, out var enabled)) return false;
            if (enabled && parent != RecycledNode) return RecycleFile(ref p, ref w, node, nameLength);
            if (enabled)
            {
                w.RecycledFilesDelta--;
                w.RecycledBlocksDelta -= (int)FileBlocks(size, w.BlockBytes);
            }
        }
        // FreeFile descends and prunes the extent tree, including internal
        // roots on populated volumes. Tree height does not restrict deletion.
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
        var nodeImage = Edit(ref p, ref w, leafBlock, NodeId, w.Data);
        if (nodeImage.IsNull) return false;
        ReplaceWord(ref p, nodeImage, nodeSlot, 0); ReplaceWord(ref p, nodeImage, nodeSlot + 4, 0);
        ReplaceHalf(ref p, nodeImage, nodeSlot + 8, 0);
        return PutSealed(ref p, ref w, leafBlock, nodeImage) &&
            ExciseObject(ref p, ref w, objectContainer, at, node) && Finish(ref p, ref w);
    }

    private static bool RenameOperation<T>(ref T p, ref Work w, ref Request r, bool recycling = false)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (r.Node == AmigaSmartFileSystemCore.RootNode) return Fail(ref w, DOS.Error.ObjectInUse);
        if (!Prepare(ref p, ref w)) return false;
        if (!recycling)
        {
            if (!Parent(ref p, ref w, ref r)) return false;
            if (r.Directory == RecycledNode)
            {
                if (!RecyclingEnabled(ref p, ref w, out var enabled)) return false;
                if (enabled) return Fail(ref w, DOS.Error.ObjectInUse);
            }
            var existing = AmigaSmartFileSystemCore.Resolve(ref p, r.Directory, r.Name, r.Length, w.Meta, out var error);
            if (existing != 0 && existing != r.Node) return Fail(ref w, DOS.Error.ObjectExists);
            if (existing == 0 && error != DOS.Error.ObjectNotFound) return Fail(ref w, error);
        }
        if (!Object(ref p, ref w, r.Node, w.Meta, out var oldAt))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if (((p.ReadUInt32(w.Meta, oldAt + 8) ^ 15) & 1) != 0) return Fail(ref w, DOS.Error.DeleteProtected);
        var oldContainer = p.ReadUInt32(w.Meta, 8);
        var oldParent = p.ReadUInt32(w.Meta, 12);
        var oldSize = p.ReadUInt32(w.Meta, oldAt + 16);
        var directory = (p.ReadUInt8(w.Meta, oldAt + 24) & 128) != 0;
        var oldNameLength = 0u;
        while (oldAt + 25 + oldNameLength < w.BlockBytes &&
            p.ReadUInt8(w.Meta, oldAt + 25 + (int)oldNameLength) != 0) oldNameLength++;
        var commentAt = oldAt + 26 + (int)oldNameLength;
        var commentLength = 0u;
        while (commentAt + commentLength < w.BlockBytes &&
            p.ReadUInt8(w.Meta, commentAt + (int)commentLength) != 0) commentLength++;
        if (commentAt + commentLength >= w.BlockBytes)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        p.Copy(w.Meta, w.Bitmap, w.BlockBytes);
        w.PreserveBitmap = true;
        var oldHash = Hash(ref p, ref w, APTR.FromPointer(w.Bitmap.Raw + (uint)oldAt + 25), oldNameLength);
        if (recycling)
        {
            w.RecycledFilesDelta++;
            w.RecycledBlocksDelta += (int)FileBlocks(oldSize, w.BlockBytes);
        }
        else if (oldParent == RecycledNode)
        {
            if (!RecyclingEnabled(ref p, ref w, out var enabled)) return false;
            if (enabled)
            {
                w.RecycledFilesDelta--;
                w.RecycledBlocksDelta -= (int)FileBlocks(oldSize, w.BlockBytes);
            }
        }
        if (directory)
        {
            var ancestor = r.Directory;
            for (var guard = 0u; ancestor != AmigaSmartFileSystemCore.RootNode && guard < w.BlockCount; guard++)
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
            out var newContainer, out var hashTable, out _, recycledName: recycling)) return false;
        var newHash = recycling ? RecycledHash(ref p, ref w, ref r, oldAt + 25) : Hash(ref p, ref w, r.Name, r.Length);
        var hashAt = 0; var oldHead = 0u;
        if (hashTable != 0)
        {
            if (!Read(ref p, ref w, hashTable, HashId, w.Meta)) return false;
            hashAt = 16 + (newHash % (int)((w.BlockBytes - 16) / 4)) * 4;
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

    private static bool MetadataOperation<T>(ref T p, ref Work w, ref Request r, DosPacketAction action)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (action != DosPacketAction.SetProtection && action != DosPacketAction.SetDate &&
            action != DosPacketAction.SetComment) return Fail(ref w, DOS.Error.ActionNotKnown);
        if (action == DosPacketAction.SetComment && r.Length > 79) return Fail(ref w, DOS.Error.CommentTooBig);
        if (action == DosPacketAction.SetDate && !p.IsMapped(r.Buffer, 12)) return Fail(ref w, DOS.Error.BadNumber);
        if (action == DosPacketAction.SetComment && !p.IsMapped(r.Buffer, r.Length)) return Fail(ref w, DOS.Error.BadNumber);
        if (!Prepare(ref p, ref w)) return false;
        var original = AmigaSmartFileSystemCore.ObjectViewFromRoots(ref p, r.Node, w.RootContainer,
            w.NodeRoot, w.BlockCount, w.BlockBytes, w.Meta, out var at);
        if (original.IsNull) return Fail(ref w, DOS.Error.DiskNotValidated);
        var container = p.ReadUInt32(original, 8);
        if (action == DosPacketAction.SetProtection)
        {
            if (!Begin(ref p, ref w)) return false;
            var image = Edit(ref p, ref w, container, ObjectId, w.Meta);
            if (image.IsNull) return false;
            ReplaceWord(ref p, image, at + 8, r.Position ^ 15);
            return PutSealed(ref p, ref w, container, image) && Finish(ref p, ref w);
        }
        if (action == DosPacketAction.SetDate)
        {
            var days = p.ReadUInt32(r.Buffer, 0); var minutes = p.ReadUInt32(r.Buffer, 4);
            var ticks = p.ReadUInt32(r.Buffer, 8);
            if (minutes >= 1440 || ticks >= 3000 || days > (uint.MaxValue - minutes * 60 - ticks / 50) / 86400)
                return Fail(ref w, DOS.Error.BadNumber);
            var seconds = days * 86400 + minutes * 60 + ticks / 50;
            if (!Begin(ref p, ref w)) return false;
            var image = Edit(ref p, ref w, container, ObjectId, w.Meta);
            if (image.IsNull) return false;
            ReplaceWord(ref p, image, at + 20, seconds);
            return PutSealed(ref p, ref w, container, image) && Finish(ref p, ref w);
        }
        if (original != w.Meta) p.Copy(original, w.Meta, w.BlockBytes);
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
        if (end + shift + 27 > w.BlockBytes) return Fail(ref w, DOS.Error.DiskFull);
        if (!Begin(ref p, ref w) || !Object(ref p, ref w, r.Node, w.Meta, out at) ||
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

    private static bool WriteOperation<T>(ref T p, ref Work w, ref Request r, out uint actual)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        ReleaseOverwriteReservation(ref p, ref w, r.Node);
        actual = 0;
        if (r.Length > int.MaxValue || !p.IsMapped(r.Buffer, r.Length)) return Fail(ref w, DOS.Error.BadNumber);
        if (!Prepare(ref p, ref w)) return false;
        APTR original;
        int at;
        var hint = w.TrstObjectHint.IsNotNull ? w.TrstObjectHint :
            w.Buffered.IsNotNull ? APTR.FromPointer(w.Buffered.Raw + BufferHeader.CachedNode) : APTR.Null;
        if (hint.IsNotNull && p.ReadUInt32(hint) == r.Node && r.Node != 0)
        {
            at = (int)p.ReadUInt32(hint, 8);
            var container = p.ReadUInt32(hint, 4);
            original = container < w.BlockCount ? p.ReadMetadataView(container, ObjectId, w.Meta) : APTR.Null;
            if (original.IsNotNull && (at < 24 || (uint)at + 27 > w.BlockBytes ||
                p.ReadUInt32(original, at + 4) != r.Node)) original = APTR.Null;
        }
        else original = AmigaSmartFileSystemCore.ObjectViewFromRoots(ref p, r.Node, w.RootContainer,
            w.NodeRoot, w.BlockCount, w.BlockBytes, w.Meta, out at);
        if (original.IsNull)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if ((p.ReadUInt8(original, at + 24) & (128 | 64)) != 0) return Fail(ref w, DOS.Error.ObjectWrongType);
        if (((p.ReadUInt32(original, at + 8) ^ 15) & 4) != 0) return Fail(ref w, DOS.Error.WriteProtected);
        var size = p.ReadUInt32(original, at + 16); var first = p.ReadUInt32(original, at + 12);
        var oldFirst = first;
        var objectBlock = p.ReadUInt32(original, 8);
        var parent = p.ReadUInt32(original, 12);
        if (hint.IsNotNull)
        {
            p.WriteUInt32(hint, 0, r.Node); p.WriteUInt32(hint, 4, objectBlock);
            p.WriteUInt32(hint, 8, (uint)at);
        }
        if (r.Position > size) return Fail(ref w, DOS.Error.SeekError);
        if (r.Length > uint.MaxValue - r.Position) return Fail(ref w, DOS.Error.ObjectTooLarge);
        if (r.Length == 0) return true;
        var blockBytes = w.BlockBytes;
        var end = r.Position + r.Length;
        if (!RecycledSizeChange(ref p, ref w, parent, size, end > size ? end : size)) return false;
        var oldBlocks = size / blockBytes + (size % blockBytes == 0 ? 0u : 1u);
        var newBlocks = end / blockBytes + (end % blockBytes == 0 ? 0u : 1u);
        if (newBlocks > oldBlocks)
        {
            var root = View(ref p, ref w, w.RootContainer, ObjectId, w.Meta);
            if (root.IsNull) return false;
            if (newBlocks - oldBlocks > p.ReadUInt32(root, (int)blockBytes - 28)) return Fail(ref w, DOS.Error.DiskFull);
        }
        if (!Begin(ref p, ref w)) return false;
        // Relocate a complete aligned overwrite while keeping the old mapping
        // reachable until this packet's TRFA publication. If no complete free
        // run exists, the ordinary full-data redo path remains available.
        if (w.RedoStaging && r.Length <= 65536 && r.Length % blockBytes == 0 &&
            r.Position % blockBytes == 0 && (r.Buffer.Raw & 3) == 0 && end <= size)
        {
            if (!FileBlock(ref p, ref w, first, r.Position / blockBytes, out var current,
                out var key, out _, out _, out var sourceLeaf, out var sourceAt) || current == 0) return FailUnlessSet(ref w);
            if (!RelocateOverwrite(ref p, ref w, ref first, r.Node, key, current - key,
                r.Length / blockBytes, r.Buffer, APTR.Null, 0, out var moved, sourceLeaf, sourceAt)) return false;
            if (moved)
            {
                actual = r.Length;
                if (w.TrstContinuation.IsNotNull && p.ReadUInt32(w.TrstContinuation) == r.Node)
                    p.WriteUInt32(w.TrstContinuation, 4, end);
            }
        }
        if (r.Position == size && size % blockBytes == 0 && r.Length % blockBytes == 0)
        {
            if (!AppendContiguousRun(ref p, ref w, ref r, first, oldBlocks,
                out var handled, out var runStart)) return false;
            if (handled)
            {
                if (first == 0) first = runStart;
                actual = r.Length;
            }
        }
        var position = r.Position;
        var searchKey = first;
        var searchStart = 0u;
        while (actual < r.Length)
        {
            var index = position / blockBytes;
            var within = position % blockBytes;
            var count = blockBytes - within;
            if (count > r.Length - actual) count = r.Length - actual;
            var source = APTR.FromPointer(r.Buffer.Raw + actual);
            var direct = within == 0 && count == blockBytes && (source.Raw & 3) == 0;
            if (!FileBlock(ref p, ref w, searchKey, index - searchStart,
                out var diskBlock, out var lastKey, out var lastSize, out var lastStart,
                out var lastLeaf, out var lastOffset)) return false;
            var absoluteStart = searchStart + lastStart;
            if (lastKey != 0) { searchKey = lastKey; searchStart = absoluteStart; }
            var newlyAllocated = diskBlock == 0;
            if (newlyAllocated)
            {
                var preferred = lastKey == 0 ? 0u : lastKey + lastSize;
                diskBlock = AllocateData(ref p, ref w, preferred);
                if (diskBlock == 0) return false;
                if (lastKey != 0 && diskBlock == preferred && lastSize < ushort.MaxValue)
                {
                    if (!GrowExtentAt(ref p, ref w, lastLeaf, lastOffset, lastSize + 1)) return false;
                }
                else
                {
                    if (!InsertExtent(ref p, ref w, diskBlock, lastKey)) return false;
                    searchKey = diskBlock; searchStart = index;
                }
                if (first == 0) first = diskBlock;
                if (!direct) p.Clear(w.Data, blockBytes);
            }
            else if (!direct && !p.ReadBlock(diskBlock, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            if (!direct)
            {
                p.Copy(source, APTR.FromPointer(w.Data.Raw + within), count);
                source = w.Data;
            }
            // The bitmap and extent changes are undo-logged. A block that was
            // free before this transaction has no old file data to restore.
            if (newlyAllocated)
            {
                if (!p.WriteBlock(diskBlock, source)) return Fail(ref w, DOS.Error.DiskNotValidated);
            }
            else if (!JournalWrite(ref p, ref w, diskBlock, source)) return false;
            actual += count; position += count;
        }
        // Extent/allocation updates do not relocate this object's record.
        if (w.RedoStaging && first == oldFirst && end <= size) return Finish(ref p, ref w);
        var objectImage = Edit(ref p, ref w, objectBlock, ObjectId, w.Meta);
        if (objectImage.IsNull) return false;
        if (first != p.ReadUInt32(objectImage, at + 12)) ReplaceWord(ref p, objectImage, at + 12, first);
        if (end > size) ReplaceWord(ref p, objectImage, at + 16, end);
        if (!PutSealed(ref p, ref w, objectBlock, objectImage)) return false;
        return Finish(ref p, ref w);
    }

    private static bool ResizeOperation<T>(ref T p, ref Work w, uint node, uint newSize)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        ReleaseOverwriteReservation(ref p, ref w, node);
        if (!Prepare(ref p, ref w) || !Object(ref p, ref w, node, w.Meta, out var at))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        if ((p.ReadUInt8(w.Meta, at + 24) & (128 | 64)) != 0) return Fail(ref w, DOS.Error.ObjectWrongType);
        if (((p.ReadUInt32(w.Meta, at + 8) ^ 15) & 4) != 0) return Fail(ref w, DOS.Error.WriteProtected);
        var oldSize = p.ReadUInt32(w.Meta, at + 16);
        if (newSize == oldSize) return true;
        var first = p.ReadUInt32(w.Meta, at + 12);
        var blockBytes = w.BlockBytes;
        if (!RecycledSizeChange(ref p, ref w, p.ReadUInt32(w.Meta, 12), oldSize, newSize)) return false;
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
            for (var guard = 0u; current != 0 && guard < w.BlockCount; guard++)
            {
                if (!AmigaSmartFileSystemCore.Extent(ref p, w.ExtentRoot, current, w.Meta, w.BlockCount, w.BlockBytes,
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
                if (!FileBlock(ref p, ref w, first, oldBlocks - 1, out var diskBlock, out _, out _, out _, out _, out _) ||
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
                if (!FileBlock(ref p, ref w, first, index, out var existing, out var lastKey, out var lastSize, out _, out _, out _) ||
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
                // Recovery restores the allocation metadata, so old contents
                // of a newly allocated block are immaterial.
                if (!p.WriteBlock(diskBlock, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            }
        }
        if (!Object(ref p, ref w, node, w.Meta, out at))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        p.WriteUInt32(w.Meta, at + 12, first); p.WriteUInt32(w.Meta, at + 16, newSize);
        return Put(ref p, ref w, p.ReadUInt32(w.Meta, 8), w.Meta) && Finish(ref p, ref w);
    }
}
