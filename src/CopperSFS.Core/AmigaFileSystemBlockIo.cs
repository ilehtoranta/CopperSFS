using Amiga;

namespace CopperSFS;

/// <summary>Sector-at-a-time fallback for platforms without bulk file-data I/O.</summary>
public static class AmigaFileSystemBlockIo
{
    public static bool ReadMetadata<T>(ref T p, uint block, uint id, APTR destination)
        where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = p.Geometry;
        return block < geometry.BlockCount && p.ReadBlock(block, destination) &&
            ValidateMetadata(ref p, block, id, destination, geometry.BlockBytes);
    }

    public static bool ValidateMetadata<T>(ref T p, uint block, uint id, APTR data, uint bytes)
        where T : struct, IAmigaFileSystemPlatform =>
        p.ReadUInt32(data) == id && p.ReadUInt32(data, 8) == block &&
        AmigaSmartFileSystemCore.BlockSum(ref p, data, bytes) == uint.MaxValue;

    public static uint Read<T>(ref T p, uint block, uint count, APTR destination)
        where T : struct, IAmigaFileSystemPlatform
    {
        var geometry = p.Geometry;
        if (!Range(geometry, block, count, destination)) return 0;
        var completed = 0u;
        while (completed < count && p.ReadBlock(block + completed,
            APTR.FromPointer(destination.Raw + completed * geometry.BlockBytes))) completed++;
        return completed;
    }

    public static bool Write<T>(ref T p, uint block, uint count, APTR source)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var geometry = p.Geometry;
        if (block < geometry.ReservedBlocks || !Range(geometry, block, count, source)) return false;
        for (var index = 0u; index < count; index++)
            if (!p.WriteBlock(block + index, APTR.FromPointer(source.Raw + index * geometry.BlockBytes))) return false;
        return true;
    }

    private static bool Range(AmigaVolumeGeometry geometry, uint block, uint count, APTR buffer)
        => geometry.BlockBytes != 0 && block < geometry.BlockCount && count <= geometry.BlockCount - block &&
           count <= (uint.MaxValue - buffer.Raw) / geometry.BlockBytes;
}
