using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystem2WriteCore
{
    public enum TrstOperation
    {
        Write, CreateFile, CreateDirectory, Remove, Rename, Resize, Truncate,
        SetProtection, SetComment, SetDate
    }

    /// <summary>
    /// Opt-in native SFS redo transaction. Stages ALL changed blocks (including file
    /// data) in caller-owned memory. A 16-byte header precedes slots of BlockBytes + 4 bytes.
    /// Buffer exhaustion fails before publication without changing live blocks.
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
        var bytes = p.Geometry.BlockBytes;
        w.Error = DOS.Error.None;
        // A native operation's compressed payload length is a UWORD. Bound the
        // worst case (one literal-run code per 64 longs) before doing any I/O.
        if (bytes < 512 || bytes > 32768 || operation < TrstOperation.Write || operation > TrstOperation.SetDate)
            return Fail(ref w, DOS.Error.BadNumber);
        if (stagingBuffer.IsNull || stagingBytes < bytes + 20 || !p.IsMapped(stagingBuffer, stagingBytes) ||
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
        if (p.WriteProtected()) return Fail(ref w, DOS.Error.DiskWriteProtected);
        // Legacy CSUN and TRST recovery must reach the real device, never the overlay.
        if (!Recover(ref p, ref w)) return false;
        if (p.RedoOverlay.IsNotNull) return Fail(ref w, DOS.Error.NoFreeStore);
        var staged = new RedoPlatform<T> { Inner = p, Buffer = APTR.FromPointer(stagingBuffer.Raw + 16), Capacity = (stagingBytes - 16) / (bytes + 4) };
        p.WriteUInt32(stagingBuffer, 0, 0); p.WriteUInt32(stagingBuffer, 4, staged.Capacity);
        p.WriteUInt32(stagingBuffer, 8, bytes); p.WriteUInt32(stagingBuffer, 12, 0);
        p.RedoOverlay = stagingBuffer;
        if (p.RedoOverlay != stagingBuffer) return Fail(ref w, DOS.Error.ActionNotKnown);
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
        if (staged.Full) return Fail(ref w, DOS.Error.NoFreeStore);
        if (!success || !CommitTrst(ref p, ref w, ref staged)) return false;
        actual = written;
        return true;
    }

    // Native platforms own the overlay; this descriptor never becomes a
    // generic platform argument (the native compiler uses four-byte platforms).
    private struct RedoPlatform<T> where T : struct, IAmigaWritableFileSystemPlatform
    {
        internal T Inner;
        internal APTR Buffer;
        internal uint Capacity, Count;
        internal bool Full;
        internal APTR Slot(uint index) => APTR.FromPointer(Buffer.Raw + index * (Inner.BlockBytes + 4));
        internal APTR Image(uint index) => APTR.FromPointer(Slot(index).Raw + 4);
        internal bool Find(uint block, out APTR image)
        {
            for (var i = 0u; i < Count; i++)
                if (Inner.ReadUInt32(Slot(i)) == block) { image = Image(i); return true; }
            image = APTR.Null; return false;
        }
    }

    private struct RedoStream
    {
        internal uint First, Block, Offset, Search;
    }

    private static bool TrstSpace<T>(ref T p, ref Work w, ref RedoPlatform<T> staged,
        ref RedoStream stream, out uint block) where T : struct, IAmigaWritableFileSystemPlatform
    {
        block = 0;
        // Journal blocks must be free in BOTH the real and final bitmap, and
        // must not be staged targets, even when a target is subsequently freed.
        // Monotonic search also keeps journal blocks distinct without a heap list.
        while (stream.Search < p.Geometry.BlockCount - 1)
        {
            var candidate = stream.Search++;
            if (candidate == w.RootContainer + 2 || staged.Find(candidate, out _)) continue;
            if (!BitmapPosition(ref p, ref w, candidate, out var map, out var offset, out var mask) ||
                !Read(ref p, ref w, map, BitmapId, w.Meta)) return false;
            if ((p.ReadUInt32(w.Meta, offset) & mask) == 0) continue;
            if (staged.Find(map, out var finalMap))
            {
                if ((p.ReadUInt32(finalMap, offset) & mask) == 0) continue;
            }
            block = candidate; return true;
        }
        return Fail(ref w, DOS.Error.DiskFull);
    }

    private static void TrstPage<T>(ref T p, ref Work w, ref RedoStream stream, uint block)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        p.Clear(w.Journal, p.Geometry.BlockBytes);
        p.WriteUInt32(w.Journal, 0, NativeRedoId); p.WriteUInt32(w.Journal, 8, block);
        stream.Block = block; stream.Offset = 16;
    }

    private static bool TrstByte<T>(ref T p, ref Work w, ref RedoPlatform<T> staged,
        ref RedoStream stream, byte value) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (stream.Offset == p.Geometry.BlockBytes)
        {
            if (!TrstSpace(ref p, ref w, ref staged, ref stream, out var next)) return false;
            p.WriteUInt32(w.Journal, 12, next); Seal(ref p, w.Journal);
            if (!p.WriteBlock(stream.Block, w.Journal)) return Fail(ref w, DOS.Error.DiskNotValidated);
            TrstPage(ref p, ref w, ref stream, next);
        }
        p.WriteUInt8(w.Journal, (int)stream.Offset++, value); return true;
    }

    // LONGCOMPRESSION: unchanged, zero, all-ones, or literal runs of 1..64 longs.
    // The first pass measures; the second emits. Skips only cover old == final
    // words, so replay remains idempotent after a partially applied transaction.
    private static bool TrstEncode<T>(ref T p, ref Work w, ref RedoPlatform<T> staged,
        ref RedoStream stream, APTR image, bool emit, out uint length)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        length = 0;
        var words = p.Geometry.BlockBytes / 4;
        for (var at = 0u; at < words;)
        {
            var value = p.ReadUInt32(image, (int)(at * 4));
            var kind = value == p.ReadUInt32(w.Data, (int)(at * 4)) ? 1u : value == 0 ? 0u : value == uint.MaxValue ? 2u : 3u;
            var count = 1u;
            while (count < 64 && at + count < words)
            {
                value = p.ReadUInt32(image, (int)((at + count) * 4));
                var next = value == p.ReadUInt32(w.Data, (int)((at + count) * 4)) ? 1u : value == 0 ? 0u : value == uint.MaxValue ? 2u : 3u;
                if (next != kind) break;
                count++;
            }
            length++;
            if (emit && !TrstByte(ref p, ref w, ref staged, ref stream, (byte)((kind << 6) | (count - 1)))) return false;
            if (kind == 3)
            {
                length += count * 4;
                if (emit)
                    for (var i = 0u; i < count * 4; i++)
                        if (!TrstByte(ref p, ref w, ref staged, ref stream, p.ReadUInt8(image, (int)(at * 4 + i)))) return false;
            }
            at += count;
        }
        return true;
    }

    private static bool CommitTrst<T>(ref T p, ref Work w, ref RedoPlatform<T> staged)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (staged.Count == 0) return true;
        var stream = new RedoStream { Search = p.Geometry.ReservedBlocks };
        if (!TrstSpace(ref p, ref w, ref staged, ref stream, out var first)) return false;
        stream.First = first; TrstPage(ref p, ref w, ref stream, first);
        for (var i = 0u; i < staged.Count; i++)
        {
            var target = p.ReadUInt32(staged.Slot(i)); var image = staged.Image(i);
            if (!p.ReadBlock(target, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            if (!TrstEncode(ref p, ref w, ref staged, ref stream, image, false, out var length)) return false;
            if (!TrstByte(ref p, ref w, ref staged, ref stream, (byte)(length >> 8)) ||
                !TrstByte(ref p, ref w, ref staged, ref stream, (byte)length)) return false;
            for (var shift = 24; shift >= 0; shift -= 8)
                if (!TrstByte(ref p, ref w, ref staged, ref stream, (byte)(target >> shift))) return false;
            if (!TrstByte(ref p, ref w, ref staged, ref stream, 0) ||
                !TrstEncode(ref p, ref w, ref staged, ref stream, image, true, out _)) return false;
            // Native OperationInformation uses 68k word alignment (7-byte header).
            if ((length & 1) == 0 && !TrstByte(ref p, ref w, ref staged, ref stream, 0)) return false;
        }
        // At an exact page boundary, end-of-chain terminates the stream. Otherwise
        // the zero-filled tail supplies the zero length (records are word aligned).
        Seal(ref p, w.Journal);
        if (!p.WriteBlock(stream.Block, w.Journal) || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        p.Clear(w.Meta, p.Geometry.BlockBytes);
        var marker = w.RootContainer + 2;
        p.WriteUInt32(w.Meta, 0, TransactionFailed); p.WriteUInt32(w.Meta, 8, marker);
        p.WriteUInt32(w.Meta, 12, stream.First); Seal(ref p, w.Meta);
        if (!p.WriteBlock(marker, w.Meta) || !p.Flush()) return Fail(ref w, DOS.Error.DiskNotValidated);
        // Reuse the normal reader for application as well as recovery. Nothing
        // live is changed before the complete, recoverable journal is published.
        return ReplayNative(ref p, ref w, marker, stream.First);
    }
}
