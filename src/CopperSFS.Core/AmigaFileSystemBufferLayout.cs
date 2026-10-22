namespace CopperSFS;

/// <summary>Byte offsets in the caller-owned physical write queue. These are
/// guest-memory layouts, not CLR structure layouts or on-disk formats.</summary>
public static class AmigaFileSystemWriteQueueLayout
{
    public const uint HeaderBytes = 288;
    public const uint EntryBytes = 16;
    public const int PointerBytes = 4;
    // Each entry also has a pointer in the sorted order and inverse owner arrays.
    public const uint IndexBytesPerEntry = EntryBytes + 2 * PointerBytes;
    public const int HashBucketMask = 63;
    public const uint HashBytes = 256;

    public static class Header
    {
        public const int Count = 0;
        public const int Capacity = 4;
        public const int BlockBytes = 8;
        public const int MinimumBlock = 12;
        public const int MaximumBlock = 16;
        public const int BulkBlocks = 20;
        public const int Order = 24;
        public const int Data = 28;
        public const int HashBuckets = 32;
    }

    public static class Entry
    {
        public const int Block = 0;
        public const int HashNext = 4;
        public const int Image = 8;
        public const int VerifiedId = 12;
    }
}

/// <summary>Byte offsets in the buffered writer's checkpoint/savepoint storage.
/// Images remain read-only outside the existing Edit/Store transaction paths.</summary>
public static class AmigaSmartFileSystemWriteBufferLayout
{
    public const uint HeaderBytes = 336;
    public const uint EntryHeaderBytes = 24;
    public const int HashBucketMask = 63;
    public const int PointerBytes = 4;
    public const uint HashBytes = 256;
    // Current, durable checkpoint, and operation savepoint images follow each entry.
    public const int ImagesPerEntry = 3;
    public const int SavepointImageIndex = 2;

    public static class Header
    {
        public const int Count = 0;
        public const int Capacity = 4;
        public const int OperationStartCount = 8;
        public const int OperationActive = 12;
        public const int Hidden = 16;
        public const int PendingCommit = 20;
        public const int GeometryValid = 24;
        // Offset 28 is reserved; do not compact the native adapter layout.
        public const int RootContainer = 32;
        public const int BitmapBase = 36;
        public const int AdminContainer = 40;
        public const int NodeRoot = 44;
        public const int ExtentRoot = 48;
        public const int CaseSensitive = 52;
        public const int BlockCount = 56;
        public const int BlockBytes = 60;
        public const int ReservedBlocks = 64;
        public const int CachedNode = 68;
        public const int CachedContainer = 72;
        public const int CachedRecordOffset = 76;
        public const uint HashBuckets = 80;
    }

    public static class Entry
    {
        public const int Block = 0;
        public const int Touched = 4;
        public const int VerifiedId = 8;
        public const int UndoImage = 12;
        public const int UndoEntry = 16;
        public const int HashNext = 20;
    }
}

/// <summary>Byte offsets in the strict writer's single-transaction staging area.</summary>
public static class AmigaSmartFileSystemTransactionBufferLayout
{
    public const uint HeaderBytes = 8;
    public const int EntryHeaderBytes = 4;

    public static class Header
    {
        public const int Count = 0;
        public const int Capacity = 4;
    }

    public static class Entry
    {
        public const int Block = 0;
        public const int Image = EntryHeaderBytes;
    }
}
