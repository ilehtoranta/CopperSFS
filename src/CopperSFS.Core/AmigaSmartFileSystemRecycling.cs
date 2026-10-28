using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    private const uint RecycledNode = 2;

    private static uint FileBlocks(uint size, uint bytes) => size / bytes + (size % bytes == 0 ? 0u : 1u);

    private static bool RecycledSizeChange<T>(ref T p, ref Work w, uint parent, uint before, uint after)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (parent != RecycledNode) return true;
        if (w.Recycling) w.RecycledBlocksDelta += (int)FileBlocks(after, w.BlockBytes) - (int)FileBlocks(before, w.BlockBytes);
        return true;
    }

    private static bool UpdateRecycledCounters<T>(ref T p, ref Work w, APTR root)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.RecycledFilesDelta == 0 && w.RecycledBlocksDelta == 0) return true;
        var blocksAt = (int)w.BlockBytes - 36;
        var filesAt = (int)w.BlockBytes - 32;
        var blocks = (long)p.ReadUInt32(root, blocksAt) + w.RecycledBlocksDelta;
        var files = (long)p.ReadUInt32(root, filesAt) + w.RecycledFilesDelta;
        if (blocks < 0 || blocks > w.BlockCount || files < 0 || files > uint.MaxValue)
            return Fail(ref w, DOS.Error.DiskNotValidated);
        ReplaceWord(ref p, root, blocksAt, (uint)blocks);
        ReplaceWord(ref p, root, filesAt, (uint)files);
        return true;
    }

    // SFS reserves four characters for $AAA..$PPP collision suffixes, even on
    // the first attempt. The name is generated from the preserved object image
    // so no fifth scratch block or native allocation is needed during a move.
    private static byte RecycledNameByte<T>(ref T p, APTR source, int nameAt, uint index,
        uint length, uint attempt) where T : struct, IAmigaFileSystemPlatform
    {
        var stem = attempt == 0 ? length : length - 4;
        if (index < stem) return p.ReadUInt8(source, nameAt + (int)index);
        var suffix = index - stem;
        return suffix == 0 ? (byte)'$' : (byte)('A' + (((attempt - 1) >> (int)((3 - suffix) * 4)) & 15));
    }

    private static void CopyRecycledName<T>(ref T p, APTR source, int nameAt, APTR target,
        uint length, uint attempt) where T : struct, IAmigaFileSystemPlatform
    {
        var stem = attempt == 0 ? length : length - 4;
        p.Copy(APTR.FromPointer(source.Raw + (uint)nameAt), target, stem);
        for (var i = stem; i < length; i++)
            p.WriteUInt8(target, (int)i, RecycledNameByte(ref p, source, nameAt, i, length, attempt));
    }

    private static ushort RecycledHash<T>(ref T p, ref Work w, ref Request r, int nameAt)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var hash = r.Length;
        for (var i = 0u; i < r.Length; i++)
        {
            var value = (uint)RecycledNameByte(ref p, w.Bitmap, nameAt, i, r.Length, r.Position);
            if (!w.CaseSensitive && ((value >= 97 && value <= 122) ||
                (value >= 224 && value <= 246) || (value >= 248 && value <= 254))) value -= 32;
            hash = unchecked(hash * 13 + value) & 65535;
        }
        return (ushort)hash;
    }

    private static bool RecycleFile<T>(ref T p, ref Work w, uint node, APTR source, int at, uint nameLength)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        p.Copy(source, w.Bitmap, w.BlockBytes);
        w.PreserveBitmap = true;
        var stem = nameLength > 96 ? 96u : nameLength;
        for (var attempt = 0u; attempt <= 4096; attempt++)
        {
            var length = stem + (attempt == 0 ? 0u : 4u);
            CopyRecycledName(ref p, w.Bitmap, at + 25, w.Data, length, attempt);
            var found = AmigaSmartFileSystemCore.ResolveFromRoots(ref p, RecycledNode, w.Data, length, w.Meta, w.RootContainer, w.NodeRoot,
                w.BlockCount, w.BlockBytes, w.CaseSensitive, out var error, out _, out _);
            if (found == 0)
            {
                if (error != DOS.Error.ObjectNotFound) return Fail(ref w, error);
                var request = new Request { Node = node, Directory = RecycledNode, Length = length, Position = attempt };
                return RenameOperation(ref p, ref w, ref request, recycling: true, recycledAt: at, recycledNameLength: nameLength);
            }
        }
        return Fail(ref w, DOS.Error.ObjectExists);
    }
}
