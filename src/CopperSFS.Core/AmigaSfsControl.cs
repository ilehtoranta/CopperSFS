namespace CopperSFS;

/// <summary>Versioned, handler-specific DOS control packet. All fields are ULONGs.</summary>
public static class AmigaSfsControl
{
    public const int PacketAction = 0x43534643; // CSFC; arg1=Magic, arg2=record, arg3=Bytes.
    public const uint Magic = 0x43534631, Version = 1, Bytes = 64;
    public const uint Info = 0, SetDataCache = 1, SetWriteMode = 2;
    public const uint Strict = 0, Buffered = 1;
    public static class Field
    {
        public const int Version = 0, Operation = 4, Value = 8, Reserved = 12;
        public const int RequestedDataBytes = 16, ActualDataBytes = 20, PoolBytes = 24;
        public const int WriteMode = 28, Pending = 32, BlockBytes = 36, MetadataBuffers = 40;
        public const int WriteBufferBytes = 44, PhysicalQueueBytes = 48, RecoveryPending = 52;
        public const int Identity = 56;
    }
}
