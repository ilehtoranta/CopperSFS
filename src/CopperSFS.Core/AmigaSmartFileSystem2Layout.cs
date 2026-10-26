using Amiga;

namespace CopperSFS;

// SFS2 mutation traversal uses the same verified layout as the public reader,
// but Object/Extent must also work while the transaction marker is dirty.
internal static class Sfs2Layout
{
    internal const uint RootNode = 1;
    private const AmigaSmartFileSystemFormat Format = AmigaSmartFileSystemFormat.Sfs2;
    internal static bool Root<T>(ref T p, APTR buffer) where T : struct, IAmigaFileSystemPlatform =>
        p.Geometry.BlockCount <= AmigaSmartFileSystem2WriteCore.MaxVolumeBlocks &&
        AmigaSmartFileSystemReadOnlyCore.Root(ref p, buffer, out var format) && format == Format;
    internal static bool Mount<T>(ref T p, APTR buffer) where T : struct, IAmigaFileSystemPlatform =>
        AmigaSmartFileSystemReadOnlyCore.Mount(ref p, buffer, out var format) && format == Format;
    internal static bool Block<T>(ref T p, uint block, uint id, APTR buffer) where T : struct, IAmigaFileSystemPlatform =>
        AmigaSmartFileSystemReadOnlyCore.Header(ref p, block, id, buffer, Format);
    internal static bool ValidChecksum<T>(ref T p, APTR buffer) where T : struct, IAmigaFileSystemPlatform =>
        AmigaSmartFileSystemReadOnlyCore.Checksum(ref p, buffer, Format);
    internal static bool Object<T>(ref T p, uint node, APTR buffer, out int at) where T : struct, IAmigaFileSystemPlatform
    {
        at = 0;
        if (!Root(ref p, buffer)) return false;
        var volume = new AmigaSmartFileSystemReadOnlyCore.Volume
        {
            Format = Format, Root = p.ReadUInt32(buffer, 104), NodeTree = p.ReadUInt32(buffer, 112),
            ExtentTree = p.ReadUInt32(buffer, 108)
        };
        return AmigaSmartFileSystemReadOnlyCore.Object(ref p, volume, node, buffer, out at);
    }
    internal static bool Extent<T>(ref T p, uint tree, uint key, APTR buffer, out uint count, out uint next)
        where T : struct, IAmigaFileSystemPlatform =>
        AmigaSmartFileSystemReadOnlyCore.Extent(ref p,
            new AmigaSmartFileSystemReadOnlyCore.Volume { Format = Format, ExtentTree = tree }, key, buffer, out count, out next, out _);
    internal static uint Resolve<T>(ref T p, uint directory, APTR name, uint length, APTR metadata, out DOS.Error error)
        where T : struct, IAmigaFileSystemPlatform =>
        AmigaSmartFileSystemReadOnlyCore.Resolve(ref p, directory, name, length, metadata, out error);
    internal static bool Describe<T>(ref T p, uint node, APTR buffer, out ulong size, out bool directory, out uint parent)
        where T : struct, IAmigaFileSystemPlatform
    {
        var ok = AmigaSmartFileSystemReadOnlyCore.Describe(ref p, node, buffer, out var entry);
        size = entry.Size; directory = entry.Directory; parent = entry.Parent; return ok;
    }
}

public static partial class AmigaSmartFileSystem2WriteCore
{
    private static ulong FileSize<T>(ref T p, APTR buffer, int at) where T : struct, IAmigaFileSystemPlatform =>
        ((ulong)p.ReadUInt32(buffer, at + 16) << 16) | p.ReadUInt16(buffer, at + 20);
    private static void SetFileSize<T>(ref T p, APTR buffer, int at, ulong size) where T : struct, IAmigaFileSystemPlatform
    {
        p.WriteUInt32(buffer, at + 16, (uint)(size >> 16)); p.WriteUInt16(buffer, at + 20, (ushort)size);
    }
}
