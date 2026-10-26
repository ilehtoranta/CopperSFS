using Amiga;

namespace CopperSFS;

/// <summary>Partition-relative filesystem blocks. A range starts at Alignment
/// modulo Granularity and contains a multiple of Granularity blocks.</summary>
public struct AmigaFileSystemDiscardLimits
{
    public uint MaximumBlocks, Granularity, Alignment;
    public readonly bool Valid => Granularity != 0 && MaximumBlocks >= Granularity && Alignment < Granularity;
}

/// <summary>Optional synchronous discard, never called by ordinary mutation.
/// The caller owns control storage required by its backend. Discard may destroy
/// any part of a requested range even on failure. Completion must order before
/// subsequent writes; reads of discarded blocks need not return zero.</summary>
public interface IAmigaDiscardFileSystemPlatform : IAmigaWritableFileSystemPlatform
{
    bool QueryDiscard(APTR control, out AmigaFileSystemDiscardLimits limits);
    bool DiscardBlocks(APTR control, uint block, uint count);
}

/// <summary>Explicit CopperSFS DOS packet ABI; unrelated handlers reject it.
/// Arg1 is an APTR to a Size-byte record. All fields are big-endian longwords.
/// Result1 is DOS TRUE on success; Result2 is a DOS error on failure.</summary>
public static class AmigaSmartFileSystemTrimPacket
{
    public const int Action = 0x43535452; // CSTR, outside the standard DOS packet set.
    public const uint Version = 1, Size = 32, DryRun = 1, QueryOnly = 2;
    public const int SizeOffset = 0, VersionOffset = 4, FlagsOffset = 8,
        BlocksOffset = 12, RangesOffset = 16, MaximumOffset = 20,
        GranularityOffset = 24, AlignmentOffset = 28;
}
