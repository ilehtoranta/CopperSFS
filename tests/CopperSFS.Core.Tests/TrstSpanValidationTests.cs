using Amiga;

namespace CopperSFS.Tests;

public sealed partial class AmigaSmartFileSystemTests
{
    private sealed class SpanValidationState(Disk disk)
    {
        public Disk Disk = disk;
        public uint DeniedFirst, DeniedBytes;
        public bool Queried, EnforceProtection, WideRegion;
        public Action<SpanValidationState>? OnProtection;
        public bool Accessible(APTR address, uint bytes) => Disk.IsMapped(address, bytes) &&
            (DeniedBytes == 0 || (ulong)address.Raw + bytes <= DeniedFirst ||
             (ulong)DeniedFirst + DeniedBytes <= address.Raw);
        public void Check(APTR address, uint bytes) => Assert.True(Accessible(address, bytes),
            $"Read/write of expired private memory {address.Raw:X}+{bytes} after query={Queried}");
    }

    private struct SpanValidationPlatform(SpanValidationState state) : IAmigaWritableFileSystemPlatform
    {
        public AmigaVolumeGeometry Geometry => state.Disk.Geometry;
        public uint BlockCount => state.Disk.BlockCount;
        public uint BlockBytes => state.Disk.BlockBytes;
        public uint ReservedBlocks => state.Disk.ReservedBlocks;
        public APTR RedoOverlay { get => state.Disk.RedoOverlay; set => state.Disk.RedoOverlay = value; }
        public bool WriteProtected()
        { state.Queried = true; state.OnProtection?.Invoke(state); return state.Disk.Protected; }
        public bool SupportsUnpublishedDataWrites => state.EnforceProtection;
        public bool UnpublishedDataWritesEnforceProtection => state.EnforceProtection;
        public bool WriteUnpublishedDataBlocks(uint block, uint count, APTR source)
            => !state.Disk.Protected && state.Disk.WriteDataBlocks(block, count, source);
        public bool IsMapped(APTR address, uint bytes) => state.Accessible(address, bytes);
        public bool TryMappedRegion(APTR address, uint bytes, out uint lower, out uint upper)
        {
            lower = upper = address.Raw;
            if (address.Raw > uint.MaxValue - bytes || !state.Accessible(address, bytes)) return false;
            upper += bytes;
            if (state.WideRegion)
            {
                lower = 0; upper = (uint)state.Disk.Ram.Length;
                if (state.DeniedBytes != 0)
                {
                    if (address.Raw < state.DeniedFirst) upper = state.DeniedFirst;
                    else lower = state.DeniedFirst + state.DeniedBytes;
                }
            }
            return true;
        }
        public byte ReadUInt8(APTR address, int offset = 0)
        { state.Check(APTR.FromPointer(address.Raw + (uint)offset), 1); return state.Disk.ReadUInt8(address, offset); }
        public ushort ReadUInt16(APTR address, int offset = 0)
        { state.Check(APTR.FromPointer(address.Raw + (uint)offset), 2); return state.Disk.ReadUInt16(address, offset); }
        public uint ReadUInt32(APTR address, int offset = 0)
        { state.Check(APTR.FromPointer(address.Raw + (uint)offset), 4); return state.Disk.ReadUInt32(address, offset); }
        public void WriteUInt8(APTR address, int offset, byte value)
        { state.Check(APTR.FromPointer(address.Raw + (uint)offset), 1); state.Disk.WriteUInt8(address, offset, value); }
        public void WriteUInt16(APTR address, int offset, ushort value)
        { state.Check(APTR.FromPointer(address.Raw + (uint)offset), 2); state.Disk.WriteUInt16(address, offset, value); }
        public void WriteUInt32(APTR address, int offset, uint value)
        { state.Check(APTR.FromPointer(address.Raw + (uint)offset), 4); state.Disk.WriteUInt32(address, offset, value); }
        public void Clear(APTR address, uint bytes) { state.Check(address, bytes); state.Disk.Clear(address, bytes); }
        public void Copy(APTR source, APTR destination, uint bytes)
        { state.Check(source, bytes); state.Check(destination, bytes); state.Disk.Copy(source, destination, bytes); }
        public bool ReadBlock(uint block, APTR destination) => state.Disk.ReadBlock(block, destination);
        public bool ReadMetadataBlock(uint block, uint id, APTR destination) => state.Disk.ReadMetadataBlock(block, id, destination);
        public APTR ReadMetadataView(uint block, uint id, APTR scratch) => state.Disk.ReadMetadataView(block, id, scratch);
        public uint ReadDataBlocks(uint block, uint count, APTR destination) => state.Disk.ReadDataBlocks(block, count, destination);
        public bool WriteBlock(uint block, APTR source) => state.Disk.WriteBlock(block, source);
        public bool WriteDataBlocks(uint block, uint count, APTR source) => state.Disk.WriteDataBlocks(block, count, source);
        public bool Flush() => state.Disk.Flush();
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)] [InlineData(2, false)] [InlineData(2, true)] [InlineData(3, false)] [InlineData(3, true)] [InlineData(4, false)] [InlineData(4, true)] [InlineData(5, false)] [InlineData(5, true)]
    [InlineData(6, false)] [InlineData(6, true)]
    public void TrstSpanValidationRechecksPrivateStateAfterProtectionIo(int change, bool wideRegion)
    {
        var seed = ContinuationSeed(); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected); var active = disk.RedoOverlay;
        var payload = Enumerable.Repeat((byte)0xD4, 1024).ToArray();
        Assert.True(DeferredWrite(ref disk, 3072, payload, out var handled, out _)); Assert.True(handled);
        payload.CopyTo(expected, 3072);
        var before = disk.Ram.AsSpan((int)GroupAddress.Raw, (int)disk.ReadUInt32(GroupAddress, 8)).ToArray();
        var state = new SpanValidationState(disk) { WideRegion = wideRegion };
        state.OnProtection = s =>
        {
            if (change <= 1)
            { s.DeniedFirst = change == 0 ? GroupAddress.Raw : active.Raw; s.DeniedBytes = 4; }
            if (change == 2) s.Disk.WriteUInt32(GroupAddress, 8, s.Disk.ReadUInt32(GroupAddress, 8) + 4);
            if (change == 3) s.Disk.WriteUInt32(GroupAddress, 0, 0);
            if (change == 4) s.Disk.WriteUInt32(GroupAddress, 16, active.Raw + 4);
            if (change == 5) s.Disk.WriteUInt32(active, 20, s.Disk.ReadUInt32(active, 20) + 4);
            if (change == 6) { s.DeniedFirst = 4000; s.DeniedBytes = 1024; }
        };
        var platform = new SpanValidationPlatform(state); var writes = disk.Writes;
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = 3, Position = 4096, Length = 1024, Buffer = APTR.FromPointer(4000) };
        Assert.False(AmigaSmartFileSystemWriteCore.TryContinueTrstGroup(ref platform, GroupAddress,
            TrstWork().Meta, APTR.Null, ref request, out handled, out var error, true));
        Assert.True(state.Queried); Assert.False(handled); Assert.Equal(change == 6 ? DOS.Error.BadNumber : DOS.Error.DiskNotValidated, error);
        Assert.Equal(writes, state.Disk.Writes); Assert.Equal(active, state.Disk.RedoOverlay);
        before.CopyTo(state.Disk.Ram, (int)GroupAddress.Raw);
        state.DeniedBytes = 0; state.OnProtection = null;
        Assert.Equal(expected, DeferredRead(ref state.Disk, expected.Length));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref state.Disk, ref work, GroupAddress));
        var remount = new Disk(state.Disk.Snapshot()); Assert.Equal(expected, DeferredRead(ref remount, expected.Length));
    }

    [Theory]
    [InlineData(0, false)] [InlineData(0, true)] [InlineData(1, false)] [InlineData(1, true)] [InlineData(2, false)] [InlineData(2, true)] [InlineData(3, false)] [InlineData(3, true)]
    [InlineData(4, false)] [InlineData(4, true)] [InlineData(5, false)] [InlineData(5, true)] [InlineData(6, false)] [InlineData(6, true)] [InlineData(7, false)] [InlineData(7, true)]
    [InlineData(8, false)] [InlineData(8, true)] [InlineData(9, false)] [InlineData(9, true)] [InlineData(10, false)] [InlineData(10, true)]
    public void TrstDeviceEnforcedEntryRevalidatesMemoryAndPrivateStateEveryCall(int change, bool wideRegion)
    {
        var seed = ContinuationSeed(); var expected = seed.Bytes.ToArray();
        var disk = StageTrstProof(seed.Image, expected);
        var payload = Enumerable.Repeat((byte)0xD4, 1024).ToArray();
        Assert.True(DeferredWrite(ref disk, 3072, payload, out var handled, out _)); Assert.True(handled);
        payload.CopyTo(expected, 3072);
        var state = new SpanValidationState(disk) { EnforceProtection = true, WideRegion = wideRegion };
        var platform = new SpanValidationPlatform(state);
        var request = new AmigaSmartFileSystemWriteCore.Request
            { Node = 3, Position = 4096, Length = 1024, Buffer = APTR.FromPointer(4000) };
        payload.CopyTo(state.Disk.Ram, 4000);
        Assert.True(AmigaSmartFileSystemWriteCore.TryContinueTrstGroup(ref platform, GroupAddress,
            TrstWork().Meta, APTR.Null, ref request, out handled, out _, true));
        Assert.True(handled); Assert.False(state.Queried); payload.CopyTo(expected, 4096);
        var active = state.Disk.RedoOverlay;
        var before = state.Disk.Ram.AsSpan((int)GroupAddress.Raw, (int)state.Disk.ReadUInt32(GroupAddress, 8)).ToArray();
        // The preceding successful call grants no mapping or validity lease to
        // the next call, even when there is no protection-status I/O.
        if (change <= 1)
        { state.DeniedFirst = change == 0 ? GroupAddress.Raw : active.Raw; state.DeniedBytes = 4; }
        if (change == 2) state.Disk.WriteUInt32(GroupAddress, 0, 0);
        if (change == 3) state.Disk.WriteUInt32(GroupAddress, 8, state.Disk.ReadUInt32(GroupAddress, 8) + 4);
        if (change == 4) state.Disk.WriteUInt32(GroupAddress, 16, active.Raw + 4);
        if (change == 5) state.Disk.WriteUInt32(active, 20, state.Disk.ReadUInt32(active, 20) + 4);
        if (change == 6) state.Disk.WriteUInt32(GroupAddress, 204, state.Disk.ReadUInt32(GroupAddress, 204) ^ 1);
        if (change == 7) state.Disk.WriteUInt32(GroupAddress, 216, state.Disk.ReadUInt32(GroupAddress, 216) ^ 1);
        if (change == 8) state.Disk.WriteUInt32(GroupAddress, 212, 0);
        if (change == 9) state.Disk.WriteUInt32(active, 16, uint.MaxValue);
        if (change == 10) state.Disk.WriteUInt32(active, 4, uint.MaxValue);
        request.Position = 5120;
        var writes = state.Disk.Writes;
        Assert.False(AmigaSmartFileSystemWriteCore.TryContinueTrstGroup(ref platform, GroupAddress,
            TrstWork().Meta, APTR.Null, ref request, out handled, out var error, true));
        Assert.False(handled); Assert.False(state.Queried);
        Assert.NotEqual(DOS.Error.None, error); Assert.Equal(writes, state.Disk.Writes);
        Assert.Equal(active, state.Disk.RedoOverlay);
        before.CopyTo(state.Disk.Ram, (int)GroupAddress.Raw); state.DeniedBytes = 0;
        Assert.Equal(expected, DeferredRead(ref state.Disk, expected.Length));
        var work = TrstWork(); Assert.True(AmigaSmartFileSystemWriteCore.FlushTrstGroup(ref state.Disk, ref work, GroupAddress));
        var remount = new Disk(state.Disk.Snapshot()); Assert.Equal(expected, DeferredRead(ref remount, expected.Length));
    }
}
