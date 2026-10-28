using Amiga;
namespace CopperSFS;

// Scalar native ABI adapters keep record layouts inside their defining module.
public static partial class AmigaSmartFileSystemReadOnlyCore
{
    public static bool DescribeInformation<T>(ref T p, uint node, APTR metadata,
        out ulong size, out bool directory, out uint parent) where T : struct, IAmigaFileSystemPlatform
    {
        var ok = Describe(ref p, node, metadata, out var entry);
        size = entry.Size; directory = entry.Directory; parent = entry.Parent; return ok;
    }
    public static bool Examine<T>(ref T p, uint node, APTR fib, APTR metadata) where T : struct, IAmigaFileSystemPlatform =>
        Describe(ref p, node, metadata, out var entry) && FillInfo(ref p, entry, fib);
    public static DOS.Error ExamineNext<T>(ref T p, uint directory, APTR fib, APTR metadata) where T : struct, IAmigaFileSystemPlatform
    {
        if (!p.IsMapped(fib, FileInfoBlock.SizeInBytes)) return DOS.Error.InvalidLock;
        var cursor = p.ReadUInt32(fib);
        var error = ExamineNext(ref p, directory, ref cursor, metadata, out var entry);
        return error != DOS.Error.None ? error : FillInfo(ref p, entry, fib) ? DOS.Error.None : DOS.Error.DiskNotValidated;
    }
    private static bool FillInfo<T>(ref T p, ReadEntry entry, APTR fib) where T : struct, IAmigaFileSystemPlatform
    {
        if (!p.IsMapped(fib, FileInfoBlock.SizeInBytes) || entry.NameLength > 107 || entry.CommentLength > 79) return false;
        p.Clear(fib, FileInfoBlock.SizeInBytes);
        var type = entry.Node == 1 ? 1u : entry.SoftLink ? 3u : entry.Directory ? 2u : 0xFFFFFFFD;
        var size = entry.Directory ? 0UL : entry.Size;
        var count = entry.Directory ? (entry.Node == 1 ? 0UL : 1UL) : size / p.Geometry.BlockBytes + (size % p.Geometry.BlockBytes == 0 ? 0UL : 1UL);
        p.WriteUInt32(fib, 0, entry.Node); p.WriteUInt32(fib, 4, type); p.WriteUInt32(fib, 120, type);
        p.WriteUInt8(fib, 8, (byte)entry.NameLength);
        for (uint i = 0; i < entry.NameLength; i++) p.WriteUInt8(fib, 9 + (int)i, p.ReadUInt8(entry.Name, (int)i));
        p.WriteUInt32(fib, 116, entry.Protection);
        p.WriteUInt32(fib, 124, size <= int.MaxValue ? (uint)size : 0);
        p.WriteUInt32(fib, 128, count <= uint.MaxValue ? (uint)count : uint.MaxValue);
        p.WriteUInt32(fib, 132, entry.ModifiedSeconds / 86400); p.WriteUInt32(fib, 136, entry.ModifiedSeconds % 86400 / 60);
        p.WriteUInt32(fib, 140, entry.ModifiedSeconds % 60 * 50);
        p.WriteUInt8(fib, 144, (byte)entry.CommentLength);
        for (uint i = 0; i < entry.CommentLength; i++) p.WriteUInt8(fib, 145 + (int)i, p.ReadUInt8(entry.Comment, (int)i));
        p.WriteUInt16(fib, 224, entry.OwnerUid); p.WriteUInt16(fib, 226, entry.OwnerGid);
        p.WriteUInt32(fib, FileInfoBlock.Size64Offset, (uint)(size >> 32)); p.WriteUInt32(fib, FileInfoBlock.Size64Offset + 4, (uint)size);
        p.WriteUInt32(fib, FileInfoBlock.NumBlocks64Offset, (uint)(count >> 32)); p.WriteUInt32(fib, FileInfoBlock.NumBlocks64Offset + 4, (uint)count);
        return true;
    }

}
