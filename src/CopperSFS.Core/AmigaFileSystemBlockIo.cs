using Amiga;

namespace CopperSFS;

/// <summary>Sector-at-a-time fallback for platforms without bulk file-data I/O.</summary>
public static class AmigaFileSystemBlockIo
{
    public static bool ReadMetadata<T>(ref T p, uint block, uint id, APTR destination)
        where T : struct, IAmigaFileSystemPlatform
    {
        var bytes = p.BlockBytes;
        return block < p.BlockCount && p.ReadBlock(block, destination) &&
            ValidateMetadata(ref p, block, id, destination, bytes);
    }

    public static bool ValidateMetadata<T>(ref T p, uint block, uint id, APTR data, uint bytes)
        where T : struct, IAmigaGuestMemory =>
        p.ReadUInt32(data) == id && p.ReadUInt32(data, 8) == block &&
        AmigaSmartFileSystemCore.BlockSum(ref p, data, bytes) == uint.MaxValue;

    public static uint Read<T>(ref T p, uint block, uint count, APTR destination)
        where T : struct, IAmigaFileSystemPlatform
    {
        var bytes = p.BlockBytes;
        if (!Range(p.BlockCount, bytes, block, count, destination)) return 0;
        var completed = 0u;
        while (completed < count && p.ReadBlock(block + completed,
            APTR.FromPointer(destination.Raw + completed * bytes))) completed++;
        return completed;
    }

    public static bool Write<T>(ref T p, uint block, uint count, APTR source)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var bytes = p.BlockBytes;
        if (block < p.ReservedBlocks || !Range(p.BlockCount, bytes, block, count, source)) return false;
        for (var index = 0u; index < count; index++)
            if (!p.WriteBlock(block + index, APTR.FromPointer(source.Raw + index * bytes))) return false;
        return true;
    }

    private static bool Range(uint blockCount, uint bytes, uint block, uint count, APTR buffer)
        => bytes != 0 && block < blockCount && count <= blockCount - block &&
           count <= (uint.MaxValue - buffer.Raw) / bytes;
}
