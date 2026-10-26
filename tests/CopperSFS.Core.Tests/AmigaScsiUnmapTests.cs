using System.Buffers.Binary;
using Amiga;
using CopperSFS;

namespace CopperSFS.Tests;

public sealed class AmigaScsiUnmapTests
{
    [Theory]
    [InlineData(512u, 512u, 0UL, 1u, 2u)]
    [InlineData(512u, 1024u, 0x100000000UL, 3u, 5u)]
    [InlineData(512u, 512u, 0x20000000000UL, 1u, 7u)]
    [InlineData(4096u, 512u, 512UL, 7u, 16u)]
    [InlineData(4096u, 4096u, 8192UL, 2u, 3u)]
    public void UnmapEncodesExactDeviceLbasAndPhysicalAlignment(uint sector, uint bytes, ulong start, uint block, uint count)
    {
        var p = new Scsi { SectorBytes = sector }; var control = APTR.FromPointer(512);
        AmigaVolumeGeometry.Create(10000, bytes, 1, out var g); g.StartByte = start;
        Assert.True(AmigaScsiUnmap.Query(ref p, g, control, out var limits));
        Assert.True(limits.Valid); Assert.Equal(limits.Alignment, block % limits.Granularity);
        Assert.True(AmigaScsiUnmap.Discard(ref p, g, control, block, count));
        var range = Assert.Single(p.Unmapped);
        Assert.Equal((start + (ulong)block * bytes) / sector, range.Lba);
        Assert.Equal((ulong)count * bytes / sector, range.Count);
        Assert.Equal(new byte[] { 0x12, 0x12, 0x9E, 0x42 }, p.Opcodes);
        Assert.Equal(AmigaScsiUnmap.ControlBytes, 256u);
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void EveryDiscoveryTransportStatusAndShortReplyFailureDisablesDiscard(int command)
    {
        AmigaVolumeGeometry.Create(128, 512, 1, out var g);
        foreach (var kind in new[] { 0, 1, 2, 3 })
        {
            var p = new Scsi { FailCommand = command, Failure = kind }; var control = APTR.FromPointer(512);
            Assert.False(AmigaScsiUnmap.Query(ref p, g, control, out _));
            Assert.False(AmigaScsiUnmap.Discard(ref p, g, control, 12, 1)); Assert.Empty(p.Unmapped);
        }
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)]
    public void UnsupportedOrMalformedCapabilitiesNeverIssueUnmap(int fault)
    {
        var p = new Scsi { CapFault = fault }; var control = APTR.FromPointer(512);
        AmigaVolumeGeometry.Create(128, 512, 1, out var g);
        Assert.False(AmigaScsiUnmap.Query(ref p, g, control, out _)); Assert.DoesNotContain((byte)0x42, p.Opcodes);
    }

    [Fact]
    public void PartitionLimitsStaleGeometryAndUnalignedRangesNeverReachDevice()
    {
        var p = new Scsi { SectorBytes = 4096 }; var control = APTR.FromPointer(512);
        AmigaVolumeGeometry.Create(128, 512, 1, out var g); g.StartByte = 512;
        Assert.True(AmigaScsiUnmap.Query(ref p, g, control, out _));
        Assert.False(AmigaScsiUnmap.Discard(ref p, g, control, 0, 8));
        Assert.False(AmigaScsiUnmap.Discard(ref p, g, control, 7, 1));
        Assert.False(AmigaScsiUnmap.Discard(ref p, g, control, 127, 8));
        Assert.False(AmigaScsiUnmap.Discard(ref p, g, control, uint.MaxValue, 8));
        g.StartByte += 4096;
        Assert.False(AmigaScsiUnmap.Discard(ref p, g, control, 7, 8)); Assert.Empty(p.Unmapped);
        g.StartByte = ulong.MaxValue - 100;
        Assert.False(AmigaScsiUnmap.Query(ref p, g, control, out _));
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(3)]
    public void DiscardChecksIoStatusScsiStatusCommandAndDataCompletion(int failure)
    {
        var p = new Scsi { FailCommand = 4, Failure = failure }; var control = APTR.FromPointer(512);
        AmigaVolumeGeometry.Create(128, 512, 1, out var g);
        Assert.True(AmigaScsiUnmap.Query(ref p, g, control, out _));
        Assert.False(AmigaScsiUnmap.Discard(ref p, g, control, 12, 1));
    }

    private struct Scsi : IAmigaScsiDirectPlatform
    {
        public readonly byte[] Ram = new byte[4096];
        public readonly List<byte> Opcodes = [];
        public readonly List<(ulong Lba, ulong Count)> Unmapped = [];
        public uint SectorBytes = 512;
        public int FailCommand, Failure, CapFault = -1;
        public Scsi() { }
        public bool ScsiDirect(APTR command)
        {
            var cdb = APTR.FromPointer(ReadUInt32(command, 12)); var data = APTR.FromPointer(ReadUInt32(command));
            var length = ReadUInt32(command, 4); var cmdLength = ReadUInt16(command, 16);
            var opcode = ReadUInt8(cdb); Opcodes.Add(opcode);
            WriteUInt16(command, 18, cmdLength); WriteUInt32(command, 8, length);
            Assert.Equal(command.Raw + 48, ReadUInt32(command, 22)); Assert.Equal(32, ReadUInt16(command, 26));
            Assert.Equal(opcode == 0x42 ? 2 : 3, ReadUInt8(command, 20));
            if (opcode == 0x12)
            {
                Assert.Equal(6, cmdLength); Assert.Equal(1, ReadUInt8(cdb, 1)); Assert.Equal(64u, length);
                var page = ReadUInt8(cdb, 2); WriteUInt8(data, 1, CapFault == 0 ? (byte)0 : page);
                WriteUInt16(data, 2, (ushort)(page == 0xB2 ? 4 : 60));
                if (page == 0xB2) { WriteUInt8(data, 5, CapFault == 1 ? (byte)0 : (byte)128); WriteUInt32(command, 8, 8); }
                else { WriteUInt32(data, 20, CapFault == 2 ? 0u : 32768u); WriteUInt32(data, 24, CapFault == 3 ? 0u : 1u); }
            }
            else if (opcode == 0x9E)
            {
                Assert.Equal(16, cmdLength); Assert.Equal(0x10, ReadUInt8(cdb, 1)); Assert.Equal(32u, ReadUInt32(cdb, 10));
                WriteUInt32(data, 0, 0xFFFF); WriteUInt32(data, 4, uint.MaxValue);
                WriteUInt32(data, 8, CapFault == 4 ? 0u : SectorBytes);
            }
            else
            {
                Assert.Equal(0x42, opcode); Assert.Equal(10, cmdLength); Assert.Equal(24u, length);
                Assert.Equal(24, ReadUInt8(cdb, 8)); Assert.Equal(0, ReadUInt8(cdb, 1));
                Assert.Equal(22, ReadUInt16(data)); Assert.Equal(16, ReadUInt16(data, 2));
                Assert.Equal(0u, ReadUInt32(data, 4)); Assert.Equal(0u, ReadUInt32(data, 20));
                Unmapped.Add((((ulong)ReadUInt32(data, 8) << 32) | ReadUInt32(data, 12), ReadUInt32(data, 16)));
            }
            if (Opcodes.Count == FailCommand)
            {
                if (Failure == 0) return false;
                if (Failure == 1) WriteUInt8(command, 21, 2);
                if (Failure == 2) WriteUInt16(command, 18, (ushort)(cmdLength - 1));
                if (Failure == 3) WriteUInt32(command, 8, 0);
            }
            return true;
        }
        public byte ReadUInt8(APTR p, int offset = 0) => Ram[p.Raw + offset];
        public ushort ReadUInt16(APTR p, int offset = 0) => BinaryPrimitives.ReadUInt16BigEndian(Ram.AsSpan((int)p.Raw + offset));
        public uint ReadUInt32(APTR p, int offset = 0) => BinaryPrimitives.ReadUInt32BigEndian(Ram.AsSpan((int)p.Raw + offset));
        public void WriteUInt8(APTR p, int offset, byte value) => Ram[p.Raw + offset] = value;
        public void WriteUInt16(APTR p, int offset, ushort value) => BinaryPrimitives.WriteUInt16BigEndian(Ram.AsSpan((int)p.Raw + offset), value);
        public void WriteUInt32(APTR p, int offset, uint value) => BinaryPrimitives.WriteUInt32BigEndian(Ram.AsSpan((int)p.Raw + offset), value);
        public bool IsMapped(APTR p, uint bytes) => p.Raw <= Ram.Length && bytes <= Ram.Length - p.Raw;
        public void Clear(APTR p, uint bytes) => Ram.AsSpan((int)p.Raw, (int)bytes).Clear();
        public void Copy(APTR source, APTR target, uint bytes) => Ram.AsSpan((int)source.Raw, (int)bytes).CopyTo(Ram.AsSpan((int)target.Raw));
    }
}
