using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    public enum TrstOperation
    {
        Write, CreateFile, CreateDirectory, Remove, Rename, Resize, Truncate,
        SetProtection, SetComment, SetDate
    }

    /// <summary>
    /// Native SFS redo transaction in bounded caller-owned memory. Stages every
    /// changed reachable block, including file data on the ordinary path. Full
    /// aligned append/relocation runs may write originally free blocks directly;
    /// a flush makes their data durable before journal publication.
    /// Each distinct block uses BlockBytes + 8 bytes after the header and index.
    /// Buffer exhaustion fails before publication without changing reachable blocks.
    /// Request fields match the ordinary APIs; Resize takes its size in Position.
    /// After a failed commit, Recover is required; a published transaction rolls
    /// forward even if this call reports failure and zero actual bytes.
    /// The caller serializes all volume access until completion/recovery.
    /// </summary>
    public static bool ExecuteTrst<T>(ref T p, ref Work w, ref Request r,
        TrstOperation operation, APTR stagingBuffer, uint stagingBytes, out uint actual)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        actual = 0;
        var bytes = p.BlockBytes;
        w.Error = DOS.Error.None;
        // A native operation's compressed payload length is a UWORD. Bound the
        // worst case (one literal-run code per 64 longs) before doing any I/O.
        if (bytes < 512 || bytes > 32768 || operation < TrstOperation.Write || operation > TrstOperation.SetDate)
            return Fail(ref w, DOS.Error.BadNumber);
        if (stagingBuffer.IsNull || stagingBytes < bytes + 40 || !p.IsMapped(stagingBuffer, stagingBytes) ||
            w.Meta.IsNull || !p.IsMapped(w.Meta, bytes) || w.Data.IsNull || !p.IsMapped(w.Data, bytes) ||
            w.Bitmap.IsNull || !p.IsMapped(w.Bitmap, bytes) || w.Journal.IsNull || !p.IsMapped(w.Journal, bytes) ||
            Overlap(stagingBuffer, stagingBytes, r.Buffer, r.Length) ||
            Overlap(stagingBuffer, stagingBytes, r.Name, r.Length) ||
            Overlap(stagingBuffer, stagingBytes, w.Meta, bytes) ||
            Overlap(stagingBuffer, stagingBytes, w.Data, bytes) ||
            Overlap(stagingBuffer, stagingBytes, w.Bitmap, bytes) ||
            Overlap(stagingBuffer, stagingBytes, w.Journal, bytes) ||
            // SetDate reads twelve bytes regardless of Request.Length.
            (operation == TrstOperation.SetDate && Overlap(stagingBuffer, stagingBytes, r.Buffer, 12)))
            return Fail(ref w, DOS.Error.NoFreeStore);
        // Switching journal policy ends CSUN streams before staging allocations.
        ReleaseOverwriteReservation(ref p, ref w);
        // Legacy CSUN and TRST recovery must reach the real device, never the overlay.
        if (!Flush(ref p, ref w)) return false;
        var buffered = w.Buffered; var batch = w.Batch;
        w.Buffered = APTR.Null; w.Batch = APTR.Null;
        if (!Prepare(ref p, ref w))
        { w.Buffered = buffered; w.Batch = batch; return false; }
        if (!AmigaSmartFileSystemRedoArena.Initialize(ref p, stagingBuffer, stagingBytes, bytes))
        { w.Buffered = buffered; w.Batch = batch; return Fail(ref w, DOS.Error.NoFreeStore); }
        var staged = new RedoPlatform<T> { Inner = p, Arena = stagingBuffer,
            Buffer = APTR.FromPointer(p.ReadUInt32(stagingBuffer, 20)), Stride = bytes + AmigaSmartFileSystemRedoArena.SlotHeaderBytes };
        p.RedoOverlay = stagingBuffer;
        if (p.RedoOverlay != stagingBuffer)
        { w.Buffered = buffered; w.Batch = batch; return Fail(ref w, DOS.Error.ActionNotKnown); }
        w.RedoStaging = true;
        uint written = 0;
        var success = operation switch
        {
            TrstOperation.Write => Write(ref p, ref w, ref r, out written),
            TrstOperation.CreateFile => Create(ref p, ref w, ref r, false),
            TrstOperation.CreateDirectory => Create(ref p, ref w, ref r, true),
            TrstOperation.Remove => Remove(ref p, ref w, r.Node),
            TrstOperation.Rename => Rename(ref p, ref w, ref r),
            TrstOperation.Resize => Resize(ref p, ref w, r.Node, r.Position),
            TrstOperation.Truncate => Truncate(ref p, ref w, r.Node),
            TrstOperation.SetProtection => SetMetadata(ref p, ref w, ref r, DosPacketAction.SetProtection),
            TrstOperation.SetComment => SetMetadata(ref p, ref w, ref r, DosPacketAction.SetComment),
            TrstOperation.SetDate => SetMetadata(ref p, ref w, ref r, DosPacketAction.SetDate),
            _ => false
        };
        p.RedoOverlay = APTR.Null;
        staged.Inner = p;
        staged.Count = p.ReadUInt32(stagingBuffer, 0);
        staged.Full = p.ReadUInt32(stagingBuffer, 12) != 0;
        w.RedoStaging = false;
        w.Buffered = buffered; w.Batch = batch;
        if (staged.Full) return Fail(ref w, DOS.Error.NoFreeStore);
        if (!success || !CommitTrst(ref p, ref w, ref staged)) return false;
        actual = written;
        return true;
    }

    // A bounded, coalescing block overlay. Reads observe prior staged writes;
    // only explicitly verified unpublished data bypasses it while staging.
    private struct RedoPlatform<T>
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        internal T Inner;
        internal APTR Buffer, Arena;
        internal uint Stride, Count;
        internal bool Full;
        public AmigaVolumeGeometry Geometry => Inner.Geometry;
        public uint BlockBytes => Inner.BlockBytes;
        public uint BlockCount => Inner.BlockCount;
        public uint ReservedBlocks => Inner.ReservedBlocks;
        internal bool Find(uint block, out APTR image)
        {
            image = AmigaSmartFileSystemRedoArena.Find(ref Inner, Arena, block);
            return image.IsNotNull;
        }
    }

    private struct RedoStream
    {
        internal uint First, Block, Offset, Search, Bitmap;
    }

    private static bool TrstWriteBlock<T>(ref T p, uint block, APTR image)
        where T : struct, IAmigaWritableFileSystemPlatform => p.SupportsRetainedBlockWrites
            ? p.WriteRetainedBlock(block, image) : p.WriteBlock(block, image);

    // TrstSpace proves journal pages free in both the original and final
    // bitmaps. They are unpublished writes too, and need not displace hot
    // metadata or copy into the native LRU. Marker and home images still use
    // retained writes. The journal flush/publication order is unchanged.
    private static bool TrstWritePage<T>(ref T p, uint block, APTR image)
        where T : struct, IAmigaWritableFileSystemPlatform => p.SupportsUnpublishedDataWrites
            ? p.WriteUnpublishedDataBlocks(block, 1, image) : TrstWriteBlock(ref p, block, image);

    private static bool TrstSpace<T>(ref T p, ref Work w, ref RedoPlatform<T> staged,
        ref RedoStream stream, out uint block) where T : struct, IAmigaWritableFileSystemPlatform
    {
        block = 0;
        // Journal blocks must be free in BOTH the real and final bitmap, and
        // must not be staged targets, even when a target is subsequently freed.
        // Monotonic search also keeps journal blocks distinct without a heap list.
        while (stream.Search < p.BlockCount - 1)
        {
            var candidate = stream.Search++;
            if (OverwriteReserved(ref p, ref w, candidate)) continue;
            if (!BitmapPosition(ref p, ref w, candidate, out var map, out var offset, out var mask)) return false;
            // Only this allocator uses Meta during journal emission. Original
            // target images are read into Data; journal pages use Journal.
            if (stream.Bitmap != map)
            {
                if (!Read(ref p, ref w, map, BitmapId, w.Meta)) return false;
                // Meta is private search scratch. Intersect once per map so
                // UsedSpan skips both durable allocations and unpublished
                // allocations in the final view. The owned arena image stays
                // unchanged; no borrowed block view survives a block access.
                if (staged.Find(map, out var finalMap))
                    for (var at = 12; at < w.BlockBytes; at += 4)
                        p.WriteUInt32(w.Meta, at, p.ReadUInt32(w.Meta, at) & p.ReadUInt32(finalMap, at));
                stream.Bitmap = map;
            }
            if ((p.ReadUInt32(w.Meta, offset) & mask) == 0)
            {
                var skip = UsedSpan(ref p, w.Meta, offset, mask, w.BlockBytes);
                var remaining = p.BlockCount - 1 - stream.Search;
                stream.Search += skip < remaining ? skip : remaining;
                continue;
            }
            if (candidate == w.RootContainer + 2 || staged.Find(candidate, out _)) continue;
            block = candidate; return true;
        }
        return Fail(ref w, DOS.Error.DiskFull);
    }

    private static void TrstPage<T>(ref T p, ref Work w, ref RedoStream stream, uint block)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        p.Clear(w.Journal, p.BlockBytes);
        p.WriteUInt32(w.Journal, 0, NativeRedoId); p.WriteUInt32(w.Journal, 8, block);
        stream.Block = block; stream.Offset = 16;
    }

    private static bool TrstByte<T>(ref T p, ref Work w, ref RedoPlatform<T> staged,
        ref RedoStream stream, byte value) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (stream.Offset == p.BlockBytes)
        {
            // The first 512-byte page starts in scratch with the marker's
            // address. It may only publish there if the ENTIRE journal fits.
            // On overflow, relocate that still-unwritten page into free space
            // before linking/emitting any page. Encoding continues once, using
            // the same bounded scratch and the ordinary external TRFA path.
            if (stream.First == w.RootContainer + 2)
            {
                if (!TrstSpace(ref p, ref w, ref staged, ref stream, out var first)) return false;
                stream.First = stream.Block = first;
                p.WriteUInt32(w.Journal, 8, first);
            }
            if (!TrstSpace(ref p, ref w, ref staged, ref stream, out var next)) return false;
            p.WriteUInt32(w.Journal, 12, next); Seal(ref p, w.Journal, w.BlockBytes);
            if (!TrstWritePage(ref p, stream.Block, w.Journal)) return Fail(ref w, DOS.Error.DiskNotValidated);
            TrstPage(ref p, ref w, ref stream, next);
        }
        p.WriteUInt8(w.Journal, (int)stream.Offset++, value); return true;
    }

    // The run plan occupies at most one byte per source longword in the
    // existing Bitmap scratch. Journal allocation uses Meta, so page crossings
    // cannot clobber the plan. Old/final words are never classified a second time.
    private static bool TrstEmit<T>(ref T p, ref Work w, ref RedoPlatform<T> staged,
        ref RedoStream stream, APTR image, uint runs, uint length)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        // Plan already computed the complete payload length. When it fits,
        // emit into the bounded current page without a page-transition call
        // for every run. This path performs no I/O and changes no publication
        // barrier; spanning payloads retain the streaming path below.
        if (stream.Offset <= w.BlockBytes && length <= w.BlockBytes - stream.Offset)
        {
            var source = image.Raw;
            var destination = w.Journal.Raw + stream.Offset;
            for (var run = 0u; run < runs; run++)
            {
                var code = p.ReadUInt8(w.Bitmap, (int)run);
                var bytes = ((uint)(code & 63) + 1) * 4;
                p.WriteUInt8(APTR.FromPointer(destination++), 0, code);
                if ((code >> 6) == 3)
                {
                    p.Copy(APTR.FromPointer(source), APTR.FromPointer(destination), bytes);
                    destination += bytes;
                }
                source += bytes;
            }
            stream.Offset += length;
            return true;
        }
        var pointer = image.Raw;
        for (var run = 0u; run < runs; run++)
        {
            var code = p.ReadUInt8(w.Bitmap, (int)run);
            var bytes = ((uint)(code & 63) + 1) * 4;
            if (!TrstByte(ref p, ref w, ref staged, ref stream, code)) return false;
            if ((code >> 6) == 3 && !TrstLiteral(ref p, ref w, ref staged, ref stream,
                APTR.FromPointer(pointer), bytes)) return false;
            pointer += bytes;
        }
        return true;
    }

    // Copy bounded literal spans, including odd addresses and page crossings.
    private static bool TrstLiteral<T>(ref T p, ref Work w, ref RedoPlatform<T> staged,
        ref RedoStream stream, APTR source, uint bytes) where T : struct, IAmigaWritableFileSystemPlatform
    {
        var pointer = source.Raw;
        while (bytes != 0)
        {
            if (stream.Offset == w.BlockBytes)
            {
                if (!TrstByte(ref p, ref w, ref staged, ref stream, p.ReadUInt8(APTR.FromPointer(pointer)))) return false;
                pointer++; bytes--;
                continue;
            }
            var take = w.BlockBytes - stream.Offset;
            if (take > bytes) take = bytes;
            p.Copy(APTR.FromPointer(pointer), APTR.FromPointer(w.Journal.Raw + stream.Offset), take);
            stream.Offset += take; pointer += take; bytes -= take;
        }
        return true;
    }

    private static bool CommitTrst<T>(ref T p, ref Work w, ref RedoPlatform<T> staged)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (staged.Count == 0) return true;
        var stream = new RedoStream { Search = p.ReservedBlocks, Bitmap = uint.MaxValue };
        var marker = w.RootContainer + 2;
        var first = marker;
        // SFS 1.279 also publishes a one-sector TRST directly in root + 2.
        // Restrict this path to the qualified atomic 512-byte sector contract;
        // larger filesystem blocks keep the separate-page/TRFA protocol.
        if ((w.BlockBytes != 512 || p.Geometry.SectorBytes != 512) &&
            !TrstSpace(ref p, ref w, ref staged, ref stream, out first)) return false;
        stream.First = first; TrstPage(ref p, ref w, ref stream, first);
        var slot = staged.Buffer;
        for (var i = 0u; i < staged.Count; i++, slot = APTR.FromPointer(slot.Raw + staged.Stride))
        {
            var target = p.ReadUInt32(slot); var image = APTR.FromPointer(slot.Raw + AmigaSmartFileSystemRedoArena.SlotHeaderBytes);
            var original = p.ReadRetainedBlock(target);
            if (original.IsNull)
            {
                if (!p.ReadBlock(target, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
                original = w.Data;
            }
            var runs = AmigaSmartFileSystemTrstCodec.Plan(ref p, original, image, staged.Stride - AmigaSmartFileSystemRedoArena.SlotHeaderBytes, w.Bitmap, out var length);
            // Record starts are word-aligned, including across page boundaries.
            // Use the native fields directly when the whole header fits here.
            if (stream.Offset <= w.BlockBytes - 7)
            {
                var at = (int)stream.Offset;
                p.WriteUInt16(w.Journal, at, (ushort)length);
                p.WriteUInt32(w.Journal, at + 2, target); p.WriteUInt8(w.Journal, at + 6, 0);
                stream.Offset += 7;
            }
            else
            {
                if (!TrstByte(ref p, ref w, ref staged, ref stream, (byte)(length >> 8)) ||
                    !TrstByte(ref p, ref w, ref staged, ref stream, (byte)length)) return false;
                for (var shift = 24; shift >= 0; shift -= 8)
                    if (!TrstByte(ref p, ref w, ref staged, ref stream, (byte)(target >> shift))) return false;
                if (!TrstByte(ref p, ref w, ref staged, ref stream, 0)) return false;
            }
            if (!TrstEmit(ref p, ref w, ref staged, ref stream, image, runs, length)) return false;
            // Native OperationInformation uses 68k word alignment (7-byte header).
            if ((length & 1) == 0 && !TrstByte(ref p, ref w, ref staged, ref stream, 0)) return false;
        }
        // At an exact page boundary, end-of-chain terminates the stream. Otherwise
        // the zero-filled tail supplies the zero length (records are word aligned).
        Seal(ref p, w.Journal, (stream.Offset + 3) & ~3u);
        if (stream.First == marker)
        {
            // This write IS publication: all unpublished replacement data
            // must already be durable. Retain/cache the marker like TRFA;
            // it is not an unreferenced external journal page.
            if (!p.Flush() || !TrstWriteBlock(ref p, marker, w.Journal) || !p.Flush())
                return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        else
        {
            if (!TrstWritePage(ref p, stream.Block, w.Journal) || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
            p.Clear(w.Meta, p.BlockBytes);
            p.WriteUInt32(w.Meta, 0, TransactionFailed); p.WriteUInt32(w.Meta, 8, marker);
            p.WriteUInt32(w.Meta, 12, stream.First); Seal(ref p, w.Meta, 16);
            if (!TrstWriteBlock(ref p, marker, w.Meta) || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        }
        // The complete redo journal is durable. Retained final images are the
        // same images encoded above; healthy commits need not read/decode them
        // back from disk. Recovery still validates the entire journal first.
        slot = staged.Buffer;
        for (var i = 0u; i < staged.Count; i++, slot = APTR.FromPointer(slot.Raw + staged.Stride))
            if (!TrstWriteBlock(ref p, p.ReadUInt32(slot), APTR.FromPointer(slot.Raw + AmigaSmartFileSystemRedoArena.SlotHeaderBytes)))
                return Fail(ref w, DOS.Error.DiskNotValidated);
        return CompleteNative(ref p, ref w, marker);
    }
}


