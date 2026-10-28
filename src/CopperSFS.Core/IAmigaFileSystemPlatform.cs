using Amiga;

namespace CopperSFS;

/// <summary>Caller-owned block I/O and guest memory for the SFS core.</summary>
public interface IAmigaFileSystemPlatform : IAmigaGuestMemory
{
    AmigaVolumeGeometry Geometry { get; }
    /// <summary>Stable mounted geometry scalars, identical to Geometry's fields.
    /// Native platforms can return them without constructing the device descriptor.</summary>
    uint BlockCount { get; }
    uint BlockBytes { get; }
    uint ReservedBlocks { get; }
    bool ReadBlock(uint block, APTR destination);
    /// <summary>Optional read-only image already retained by the platform, or
    /// null when unavailable. Performs no block I/O; bytes must match ReadBlock,
    /// including pending writes. No checksum/type validation is implied. Consume
    /// the image before any subsequent block I/O or cache access can expire it.</summary>
    APTR ReadRetainedBlock(uint block) => APTR.Null;
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
    /// <summary>Optional caller-owned TRST overlay. Platforms supporting ExecuteTrst
    /// route all block reads/writes through AmigaSmartFileSystemRedoArena while set.</summary>
    APTR RedoOverlay { get => APTR.Null; set { } }
    /// <summary>Validate a range and optionally describe a larger contiguous
    /// mapped region containing it. Bounds are exclusive at the end, cannot
    /// wrap, and expire at any platform I/O or external call. The default
    /// certifies only the requested range; callers check other ranges normally.</summary>
    bool TryMappedRegion(APTR address, uint bytes, out uint lower, out uint upper)
    {
        lower = upper = address.Raw;
        if (address.Raw > uint.MaxValue - bytes || !IsMapped(address, bytes)) return false;
        upper += bytes;
        return true;
    }
    bool WriteProtected();
    /// <summary>Accept an owned copy outside the logical transaction overlays.
    /// An optional physical queue must provide read-your-writes and drain before
    /// Flush; writes from different flush intervals must never be combined.</summary>
    bool WriteBlock(uint block, APTR source);
    /// <summary>Accept consecutive blocks using the physical queue contract of
    /// WriteBlock. Does not flush or stage metadata. The caller must durably
    /// publish undo before overwriting reachable data or metadata.</summary>
    bool WriteDataBlocks(uint block, uint count, APTR source);
    /// <summary>Whether this platform implements direct unpublished-data writes.</summary>
    bool SupportsUnpublishedDataWrites => false;

    /// <summary>The direct unpublished transfer enforces device write protection
    /// before reporting success. A failed transfer may change only unpublished
    /// payload bytes, never the accepted logical view. This permits an established
    /// deferred run to rely on the write result rather than a separate status I/O.
    /// Platforms with additional software protection must keep the default.</summary>
    bool UnpublishedDataWritesEnforceProtection => false;
    /// <summary>Write data proven unreachable in the durable filesystem, outside
    /// all logical overlays. May bypass an owned-copy queue; consumes the source
    /// before returning. Preserve prior overlapping physical writes and require
    /// a later Flush before publishing metadata that makes this data reachable.</summary>
    bool WriteUnpublishedDataBlocks(uint block, uint count, APTR source) => false;
    /// <summary>Optional immediate writes for caller-retained journal/home images.
    /// The source is consumed before return, prior overlapping queued writes are
    /// ordered first, and Flush remains the durability barrier. The caller must
    /// publish its journal before writing any reachable home image.</summary>
    bool SupportsRetainedBlockWrites => false;
    bool WriteRetainedBlock(uint block, APTR source) => false;
    /// <summary>Drain all accepted physical writes, then flush the device.
    /// Return false if either step fails; retain queued images on a failed drain.</summary>
    bool Flush();
}

/// <summary>Bounded range transfers used by the separately verified SFS2 engine.</summary>
public interface IAmigaBlockRangeFileSystemPlatform : IAmigaWritableFileSystemPlatform
{
    bool ReadBlocks(uint first, uint count, APTR destination);
    bool WriteBlocks(uint first, uint count, APTR source);
}
