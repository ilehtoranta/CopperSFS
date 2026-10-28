using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    // Memory-only consumers of already verified, full bitmap images. The
    // header is not part of the allocation mask. Mounted block sizes are
    // multiples of four; no borrowed image survives a block/cache access here.
    private static void IntersectTrstBitmapWords<T>(ref T p, APTR output, APTR durable,
        APTR checkpoint, uint bytes) where T : struct, IAmigaGuestMemory
    {
        var target = output.Raw + 12; var original = durable.Raw + 12;
        var remaining = (bytes - 12) >> 2;
        if (checkpoint.IsNull)
        {
            while (remaining >= 4u)
            {
                var to = APTR.FromPointer(target); var from = APTR.FromPointer(original);
                p.WriteUInt32(to, 0, p.ReadUInt32(to) & p.ReadUInt32(from));
                p.WriteUInt32(to, 4, p.ReadUInt32(to, 4) & p.ReadUInt32(from, 4));
                p.WriteUInt32(to, 8, p.ReadUInt32(to, 8) & p.ReadUInt32(from, 8));
                p.WriteUInt32(to, 12, p.ReadUInt32(to, 12) & p.ReadUInt32(from, 12));
                target += 16; original += 16; remaining -= 4;
            }
            while (remaining != 0)
            {
                var to = APTR.FromPointer(target);
                p.WriteUInt32(to, 0, p.ReadUInt32(to) & p.ReadUInt32(APTR.FromPointer(original)));
                target += 4; original += 4; remaining--;
            }
            return;
        }
        var accepted = checkpoint.Raw + 12;
        while (remaining >= 4u)
        {
            var to = APTR.FromPointer(target); var from = APTR.FromPointer(original); var prior = APTR.FromPointer(accepted);
            p.WriteUInt32(to, 0, p.ReadUInt32(to) & p.ReadUInt32(from) & p.ReadUInt32(prior));
            p.WriteUInt32(to, 4, p.ReadUInt32(to, 4) & p.ReadUInt32(from, 4) & p.ReadUInt32(prior, 4));
            p.WriteUInt32(to, 8, p.ReadUInt32(to, 8) & p.ReadUInt32(from, 8) & p.ReadUInt32(prior, 8));
            p.WriteUInt32(to, 12, p.ReadUInt32(to, 12) & p.ReadUInt32(from, 12) & p.ReadUInt32(prior, 12));
            target += 16; original += 16; accepted += 16; remaining -= 4;
        }
        while (remaining != 0)
        {
            var to = APTR.FromPointer(target);
            p.WriteUInt32(to, 0, p.ReadUInt32(to) & p.ReadUInt32(APTR.FromPointer(original)) & p.ReadUInt32(APTR.FromPointer(accepted)));
            target += 4; original += 4; accepted += 4; remaining--;
        }
    }

    private static uint CountTrstBitmapFreeBits<T>(ref T p, APTR bitmap, uint bytes)
        where T : struct, IAmigaGuestMemory
    {
        var pointer = bitmap.Raw + 12; var remaining = (bytes - 12) >> 2;
        var free = 0u;
        while (remaining != 0)
        {
            var word = p.ReadUInt32(APTR.FromPointer(pointer));
            pointer += 4; remaining--;
            if (word == uint.MaxValue) { free += 32; continue; }
            if (word == 0) continue;
            word -= (word >> 1) & 0x55555555;
            word = (word & 0x33333333) + ((word >> 2) & 0x33333333);
            word = (word + (word >> 4)) & 0x0F0F0F0F;
            word += word >> 8; word += word >> 16;
            free += word & 63;
        }
        return free;
    }
}
