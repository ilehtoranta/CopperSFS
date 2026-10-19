using Amiga;

namespace CopperStart.Dos;

/// <summary>Partition-relative filesystem blocks and the underlying device byte range.</summary>
public struct AmigaVolumeGeometry
{
    public uint BlockCount, BlockBytes, ReservedBlocks, SectorBytes;
    public ulong StartByte;
    public uint TrackBlocks, MaxTransfer, BufferMemoryType, Mask;
    public readonly uint RootBlock => (BlockCount - 1) / 2 + ReservedBlocks / 2 +
        ((BlockCount - 1) % 2 + ReservedBlocks % 2) / 2;
    public readonly uint TableEntries => BlockBytes / 4 - 56;
    public readonly uint BitmapBits => (BlockBytes - 4) * 8;
    public readonly int Tail => (int)BlockBytes - 512;

    public static bool Create(uint blocks, uint bytes, uint reserved, out AmigaVolumeGeometry geometry)
    {
        geometry = default;
        // Arithmetic/storage requirements of the format and caller-owned scratch buffers.
        if (bytes < 512 || (bytes & (bytes - 1)) != 0 || bytes > uint.MaxValue / 8 ||
            reserved == 0 || reserved >= uint.MaxValue - 1 || blocks <= reserved + 1) return false;
        geometry.SectorBytes = bytes; geometry.BlockCount = blocks; geometry.BlockBytes = bytes; geometry.ReservedBlocks = reserved;
        geometry.TrackBlocks = 1; geometry.MaxTransfer = 0x7FFFFFFF;
        geometry.BufferMemoryType = (uint)global::Amiga.Exec.MemoryFlags.Public;
        geometry.Mask = 0xFFFFFFFE;
        return true;
    }

    /// <summary>DosEnvec is a BCPL vector; SizeBlock is in longwords. Never read beyond de_TableSize.</summary>
    public static bool FromEnvironment<T>(ref T p, APTR environment, out AmigaVolumeGeometry geometry)
        where T : struct, IAmigaGuestMemory
    {
        geometry = default;
        if (environment.IsNull || p.ReadUInt32(environment) < 10) return false;
        var words = p.ReadUInt32(environment, 4);
        var surfaces = p.ReadUInt32(environment, 12);
        var sectorsPerBlock = p.ReadUInt32(environment, 16);
        var sectorsPerTrack = p.ReadUInt32(environment, 20);
        var low = p.ReadUInt32(environment, 36); var high = p.ReadUInt32(environment, 40);
        if (words > uint.MaxValue / 4 || sectorsPerBlock == 0 || surfaces == 0 ||
            sectorsPerTrack == 0 || high < low) return false;
        var sectorBytes = words * 4;
        if (sectorBytes == 0 || sectorsPerBlock > uint.MaxValue / sectorBytes) return false;
        var bytes = sectorBytes * sectorsPerBlock;
        var cylinder = (ulong)surfaces * sectorsPerTrack;
        var sectors = ((ulong)high - low + 1) * cylinder;
        if (sectors / cylinder != (ulong)high - low + 1 || sectors % sectorsPerBlock != 0 ||
            sectors / sectorsPerBlock > uint.MaxValue ||
            !Create((uint)(sectors / sectorsPerBlock), bytes, p.ReadUInt32(environment, 24), out geometry)) return false;
        var start = (ulong)low * cylinder;
        if (low != 0 && start / low != cylinder || start > ulong.MaxValue / sectorBytes ||
            sectors > ulong.MaxValue / sectorBytes || start * sectorBytes > ulong.MaxValue - sectors * sectorBytes) return false;
        geometry.SectorBytes = sectorBytes; geometry.StartByte = start * sectorBytes;
        geometry.TrackBlocks = sectorsPerTrack % sectorsPerBlock == 0 ? sectorsPerTrack / sectorsPerBlock : 1;
        var size = p.ReadUInt32(environment);
        if (size >= 12) geometry.BufferMemoryType = p.ReadUInt32(environment, 48) | (uint)global::Amiga.Exec.MemoryFlags.Public;
        if (size >= 13) geometry.MaxTransfer = p.ReadUInt32(environment, 52);
        if (size >= 14) geometry.Mask = p.ReadUInt32(environment, 56);
        return geometry.MaxTransfer >= sectorBytes;
    }

    public static bool FromDevice<T>(ref T p, APTR driveGeometry, out AmigaVolumeGeometry geometry)
        where T : struct, IAmigaGuestMemory
    {
        if (!Create(p.ReadUInt32(driveGeometry, 4), p.ReadUInt32(driveGeometry), 2, out geometry)) return false;
        geometry.TrackBlocks = p.ReadUInt32(driveGeometry, 20);
        if (geometry.TrackBlocks == 0) geometry.TrackBlocks = 1;
        geometry.BufferMemoryType = p.ReadUInt32(driveGeometry, 24) | (uint)global::Amiga.Exec.MemoryFlags.Public;
        return true;
    }

    public readonly bool DeviceRange(uint block, out ulong offset)
    {
        offset = StartByte + (ulong)block * BlockBytes;
        return block < BlockCount && offset >= StartByte && offset <= ulong.MaxValue - BlockBytes;
    }
}
