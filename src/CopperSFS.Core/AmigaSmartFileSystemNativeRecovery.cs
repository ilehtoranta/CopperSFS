using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    // Original Amiga SFS TRST records contain 68k-aligned OperationInformation
    // values. The block number starts at byte 2; data starts at byte 7.
    // Payloads use SFS LONGCOMPRESSION (fs.h/support.c).
    private struct NativeCursor
    {
        public uint Next, Offset, Visited;
    }

    private static bool StartNative<T>(ref T p, ref Work w, uint first, out NativeCursor cursor)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        cursor = default;
        if (!AmigaSmartFileSystemCore.Block(ref p, first, NativeRedoId, w.Journal))
            return Fail(ref w, DOS.Error.DiskNotValidated);
        cursor.Next = p.ReadUInt32(w.Journal, 12);
        cursor.Offset = 16;
        cursor.Visited = 1;
        return true;
    }

    private static bool NativeByte<T>(ref T p, ref Work w, ref NativeCursor cursor,
        out byte value, out bool end) where T : struct, IAmigaWritableFileSystemPlatform
    {
        value = 0; end = false;
        if (cursor.Offset == w.BlockBytes)
        {
            if (cursor.Next == 0) { end = true; return true; }
            if (cursor.Visited >= w.BlockCount ||
                !AmigaSmartFileSystemCore.Block(ref p, cursor.Next, NativeRedoId, w.Journal))
                return Fail(ref w, DOS.Error.DiskNotValidated);
            cursor.Next = p.ReadUInt32(w.Journal, 12);
            cursor.Offset = 16;
            cursor.Visited++;
        }
        value = p.ReadUInt8(w.Journal, (int)cursor.Offset++);
        return true;
    }

    private static bool NativeRequired<T>(ref T p, ref Work w, ref NativeCursor cursor,
        out byte value) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!NativeByte(ref p, ref w, ref cursor, out value, out var end)) return false;
        return !end || Fail(ref w, DOS.Error.DiskNotValidated);
    }

    private static bool NativePass<T>(ref T p, ref Work w, uint first, bool apply)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!StartNative(ref p, ref w, first, out var cursor)) return false;
        var words = w.BlockBytes / 4;
        // Every iteration consumes stream bytes. NativeByte bounds the chain
        // to BlockCount records, so no separate wide operation counter is needed.
        while (true)
        {
            if (!NativeByte(ref p, ref w, ref cursor, out var high, out var end)) return false;
            if (end) return true;
            if (!NativeRequired(ref p, ref w, ref cursor, out var low)) return false;
            var length = (uint)(high << 8 | low);
            if (length == 0) return true;
            if (length > w.BlockBytes + w.BlockBytes / 4 + 16)
                return Fail(ref w, DOS.Error.DiskNotValidated);
            uint target = 0;
            for (var i = 0; i < 4; i++)
            {
                if (!NativeRequired(ref p, ref w, ref cursor, out var part)) return false;
                target = (target << 8) | part;
            }
            if (!NativeRequired(ref p, ref w, ref cursor, out var bits)) return false;
            if (target < w.ReservedBlocks || target >= w.BlockCount - 1 ||
                (bits & ~1) != 0) return Fail(ref w, DOS.Error.DiskNotValidated);
            if (apply)
            {
                if ((bits & 1) != 0) p.Clear(w.Data, w.BlockBytes);
                else if (!p.ReadBlock(target, w.Data)) return Fail(ref w, DOS.Error.DiskNotValidated);
            }
            uint consumed = 0, word = 0;
            while (consumed < length)
            {
                if (!NativeRequired(ref p, ref w, ref cursor, out var code)) return false;
                consumed++;
                var count = (uint)(code & 63) + 1;
                if (count > words - word) return Fail(ref w, DOS.Error.DiskNotValidated);
                switch (code >> 6)
                {
                    case 0:
                    case 2:
                        if (apply)
                        {
                            var fill = (byte)(code >> 6 == 0 ? 0 : 255);
                            for (var i = 0u; i < count * 4; i++)
                                p.WriteUInt8(w.Data, (int)(word * 4 + i), fill);
                        }
                        break;
                    case 3:
                        if (count * 4 > length - consumed)
                            return Fail(ref w, DOS.Error.DiskNotValidated);
                        for (var i = 0u; i < count * 4; i++)
                        {
                            if (!NativeRequired(ref p, ref w, ref cursor, out var value)) return false;
                            if (apply) p.WriteUInt8(w.Data, (int)(word * 4 + i), value);
                        }
                        consumed += count * 4;
                        break;
                }
                word += count;
            }
            if (word != words) return Fail(ref w, DOS.Error.DiskNotValidated);
            var padding = (length & 1) == 0 ? 1 : 0;
            for (var i = 0; i < padding; i++)
                if (!NativeRequired(ref p, ref w, ref cursor, out _)) return false;
            if (apply && !p.WriteBlock(target, w.Data))
                return Fail(ref w, DOS.Error.DiskNotValidated);
        }
    }

    private static bool ReplayNative<T>(ref T p, ref Work w, uint marker, uint first)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (!NativePass(ref p, ref w, first, false)) return false;
        if (!NativePass(ref p, ref w, first, true) || !p.Flush())
            return Fail(ref w, DOS.Error.DiskNotValidated);
        p.Clear(w.Meta, w.BlockBytes);
        p.WriteUInt32(w.Meta, 0, TransactionOk); p.WriteUInt32(w.Meta, 8, marker);
        Seal(ref p, w.Meta, w.BlockBytes);
        return (p.WriteBlock(marker, w.Meta) && p.Flush()) || Fail(ref w, DOS.Error.DiskNotValidated);
    }
}
