using Amiga;

namespace CopperStart.Dos;

/// <summary>Caller-owned block I/O and guest memory for the SFS core.</summary>
public interface IAmigaFileSystemPlatform : IAmigaGuestMemory
{
    AmigaVolumeGeometry Geometry { get; }
    bool ReadBlock(uint block, APTR destination);
}

public interface IAmigaWritableFileSystemPlatform : IAmigaFileSystemPlatform
{
    bool WriteProtected();
    bool WriteBlock(uint block, APTR source);
    bool Flush();
}
