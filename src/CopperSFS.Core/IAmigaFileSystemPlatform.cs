using Amiga;

namespace CopperSFS;

/// <summary>Caller-owned block I/O and guest memory for the SFS core.</summary>
public interface IAmigaFileSystemPlatform : IAmigaGuestMemory
{
    AmigaVolumeGeometry Geometry { get; }
    bool ReadBlock(uint block, APTR destination);
    /// <summary>Read metadata and verify its type, own-block number and SFS checksum.
    /// A cache may retain this verification only while its private image is unchanged.</summary>
    bool ReadMetadataBlock(uint block, uint id, APTR destination);
    /// <summary>Verified read-only metadata, or null on failure. A platform may
    /// return scratch or a cache image. The view expires at the next block I/O;
    /// callers must never modify it or retain it across another block access.</summary>
    APTR ReadMetadataView(uint block, uint id, APTR scratch);
    /// <summary>Read consecutive file-data blocks, returning the completed prefix.
    /// Reads must observe staged writes. Metadata uses the verified metadata methods.</summary>
    uint ReadDataBlocks(uint block, uint count, APTR destination);
}

public interface IAmigaWritableFileSystemPlatform : IAmigaFileSystemPlatform
{
    bool WriteProtected();
    /// <summary>Accept an owned copy outside the logical transaction overlays.
    /// An optional physical queue must provide read-your-writes and drain before
    /// Flush; writes from different flush intervals must never be combined.</summary>
    bool WriteBlock(uint block, APTR source);
    /// <summary>Accept consecutive newly allocated data blocks using the same
    /// physical queue contract as WriteBlock. Does not flush, stage metadata,
    /// or replace the journal for existing data.</summary>
    bool WriteDataBlocks(uint block, uint count, APTR source);
    /// <summary>Drain all accepted physical writes, then flush the device.
    /// Return false if either step fails; retain queued images on a failed drain.</summary>
    bool Flush();
}
