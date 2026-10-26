using Amiga;

namespace CopperSFS;

/// <summary>HD_SCSICMD transport. Check io_Error here; this codec additionally
/// checks SCSI status, command completion and data completion. No ATA guesses.</summary>
public interface IAmigaScsiDirectPlatform : IAmigaGuestMemory
{
    bool ScsiDirect(APTR command);
}

/// <summary>SBC-3 UNMAP, one coalesced range per command. All storage is caller
/// owned, word aligned, and suitable for the device's DMA constraints. Read-only
/// discovery requires VPD B2 LBPU, B0 limits and READ CAPACITY(16). Geometry
/// offsets are device bytes, while the public ranges are partition blocks.</summary>
public static class AmigaScsiUnmap
{
    public const uint ControlBytes = 256, ScsiCommandBytes = 30;
    private const int Cdb = 32, Sense = 48, Data = 80;
    private const int Sector = 208, Maximum = 212, Cookie = 216,
        StartHigh = 220, StartLow = 224, Blocks = 228, Bytes = 232;
    private const uint Ready = 0x554E4D50;

    public static bool Query<T>(ref T p, AmigaVolumeGeometry geometry, APTR control,
        out AmigaFileSystemDiscardLimits limits) where T : struct, IAmigaScsiDirectPlatform
    {
        limits = default;
        if (control.IsNull || (control.Raw & 1) != 0 || !p.IsMapped(control, ControlBytes)) return false;
        p.Clear(control, ControlBytes);
        if (!Inquiry(ref p, control, 0xB2, 8) || (p.ReadUInt8(control, Data + 5) & 128) == 0 ||
            !Inquiry(ref p, control, 0xB0, 28)) return false;
        var maximum = p.ReadUInt32(control, Data + 20);
        if (maximum == 0 || p.ReadUInt32(control, Data + 24) == 0) return false;
        Prepare(ref p, control, 16, 32, true);
        p.WriteUInt8(control, Cdb, 0x9E); p.WriteUInt8(control, Cdb + 1, 0x10);
        p.WriteUInt32(control, Cdb + 10, 32);
        if (!Complete(ref p, control, 16, 12)) return false;
        var last = ((ulong)p.ReadUInt32(control, Data) << 32) | p.ReadUInt32(control, Data + 4);
        var sector = p.ReadUInt32(control, Data + 8);
        var bytes = geometry.BlockBytes;
        if (sector == 0 || (sector & (sector - 1)) != 0 || bytes == 0 ||
            (bytes & (bytes - 1)) != 0 || geometry.BlockCount == 0 ||
            geometry.StartByte > ulong.MaxValue - (ulong)geometry.BlockCount * bytes) return false;
        var end = geometry.StartByte + (ulong)geometry.BlockCount * bytes;
        if ((end - 1) / sector > last) return false;
        if (bytes >= sector)
        {
            if (geometry.StartByte % sector != 0) return false;
            limits.Granularity = 1;
            limits.MaximumBlocks = maximum / (bytes / sector);
        }
        else
        {
            if (geometry.StartByte % bytes != 0) return false;
            limits.Granularity = sector / bytes;
            limits.Alignment = (uint)((sector - geometry.StartByte % sector) % sector) / bytes;
            var blocks = (ulong)maximum * limits.Granularity;
            limits.MaximumBlocks = blocks > uint.MaxValue ? uint.MaxValue : (uint)blocks;
        }
        // Bound individual device command latency. VPD optimal granularity is a
        // hint, not a mandate.
        if (limits.MaximumBlocks > 4096 && limits.Granularity <= 4096) limits.MaximumBlocks = 4096;
        if (!limits.Valid) return false;
        p.WriteUInt32(control, Sector, sector); p.WriteUInt32(control, Maximum, maximum);
        p.WriteUInt32(control, StartHigh, (uint)(geometry.StartByte >> 32));
        p.WriteUInt32(control, StartLow, (uint)geometry.StartByte);
        p.WriteUInt32(control, Blocks, geometry.BlockCount); p.WriteUInt32(control, Bytes, geometry.BlockBytes);
        p.WriteUInt32(control, Cookie, Ready);
        return true;
    }

    public static bool Discard<T>(ref T p, AmigaVolumeGeometry geometry, APTR control,
        uint block, uint count) where T : struct, IAmigaScsiDirectPlatform
    {
        if (count == 0 || block >= geometry.BlockCount || count > geometry.BlockCount - block ||
            control.IsNull || !p.IsMapped(control, ControlBytes) || p.ReadUInt32(control, Cookie) != Ready ||
            p.ReadUInt32(control, StartHigh) != (uint)(geometry.StartByte >> 32) ||
            p.ReadUInt32(control, StartLow) != (uint)geometry.StartByte ||
            p.ReadUInt32(control, Blocks) != geometry.BlockCount || p.ReadUInt32(control, Bytes) != geometry.BlockBytes) return false;
        var sector = p.ReadUInt32(control, Sector);
        var offset = geometry.StartByte + (ulong)block * geometry.BlockBytes;
        var length = (ulong)count * geometry.BlockBytes;
        if (sector == 0 || offset < geometry.StartByte || offset > ulong.MaxValue - length ||
            offset % sector != 0 || length % sector != 0 ||
            length / sector > p.ReadUInt32(control, Maximum)) return false;
        Prepare(ref p, control, 10, 24, false);
        p.WriteUInt8(control, Cdb, 0x42); p.WriteUInt8(control, Cdb + 8, 24);
        p.Clear(APTR.FromPointer(control.Raw + Data), 24);
        p.WriteUInt16(control, Data, 22); p.WriteUInt16(control, Data + 2, 16);
        var lba = offset / sector;
        p.WriteUInt32(control, Data + 8, (uint)(lba >> 32));
        p.WriteUInt32(control, Data + 12, (uint)lba);
        p.WriteUInt32(control, Data + 16, (uint)(length / sector));
        return Complete(ref p, control, 10, 24);
    }

    private static bool Inquiry<T>(ref T p, APTR control, byte page, uint required)
        where T : struct, IAmigaScsiDirectPlatform
    {
        Prepare(ref p, control, 6, 64, true);
        p.WriteUInt8(control, Cdb, 0x12); p.WriteUInt8(control, Cdb + 1, 1);
        p.WriteUInt8(control, Cdb + 2, page); p.WriteUInt8(control, Cdb + 4, 64);
        return Complete(ref p, control, 6, required) && (p.ReadUInt8(control, Data) & 31) == 0 &&
            p.ReadUInt8(control, Data + 1) == page && p.ReadUInt16(control, Data + 2) >= required - 4;
    }

    private static void Prepare<T>(ref T p, APTR control, ushort cdbBytes, uint dataBytes, bool read)
        where T : struct, IAmigaGuestMemory
    {
        p.Clear(control, ScsiCommandBytes);
        p.Clear(APTR.FromPointer(control.Raw + Cdb), 16);
        p.Clear(APTR.FromPointer(control.Raw + Sense), 32);
        if (read) p.Clear(APTR.FromPointer(control.Raw + Data), 128);
        p.WriteUInt32(control, 0, control.Raw + Data); p.WriteUInt32(control, 4, dataBytes);
        p.WriteUInt32(control, 12, control.Raw + Cdb); p.WriteUInt16(control, 16, cdbBytes);
        p.WriteUInt8(control, 20, read ? (byte)3 : (byte)2);
        p.WriteUInt32(control, 22, control.Raw + Sense); p.WriteUInt16(control, 26, 32);
    }

    private static bool Complete<T>(ref T p, APTR control, ushort cdbBytes, uint required)
        where T : struct, IAmigaScsiDirectPlatform => p.ScsiDirect(control) &&
        p.ReadUInt8(control, 21) == 0 && p.ReadUInt16(control, 18) == cdbBytes &&
        p.ReadUInt32(control, 8) >= required && p.ReadUInt32(control, 8) <= p.ReadUInt32(control, 4);
}
