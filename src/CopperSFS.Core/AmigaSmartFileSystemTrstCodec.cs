using Amiga;

namespace CopperSFS;

/// <summary>Allocation-free planning of original SFS LONGCOMPRESSION records.</summary>
public static class AmigaSmartFileSystemTrstCodec
{
    /// <summary>
    /// Classifies each old/final longword once. Writes one code per run to plan
    /// (at most blockBytes / 4 bytes) and returns the run count and payload size.
    /// Inputs and plan must be mapped, disjoint buffers; blockBytes is a supported
    /// SFS block size. Literal bytes remain in finalImage until journal emission.
    /// </summary>
    public static uint Plan<T>(ref T p, APTR oldImage, APTR finalImage, uint blockBytes,
        APTR plan, out uint length) where T : struct, IAmigaGuestMemory
    {
        var oldAt = oldImage.Raw; var finalAt = finalImage.Raw;
        var remaining = blockBytes >> 2;
        uint runs = 0;
        length = 0;
        if (remaining == 0) return 0;
        var value = p.ReadUInt32(APTR.FromPointer(finalAt));
        var old = p.ReadUInt32(APTR.FromPointer(oldAt));
        while (remaining != 0)
        {
            var kind = value == old ? 1u : value == 0 ? 0u : value == uint.MaxValue ? 2u : 3u;
            var limit = remaining < 64 ? remaining : 64;
            var count = 1u;
            // Dispatch once per run. Most metadata words are unchanged; that
            // loop needs one comparison instead of reclassifying every word.
            // The first word of the following run stays in value/old, so each
            // input word is still read exactly once, including alternating runs.
            if (kind == 1)
            {
                // Four guarded pairs share one loop-control comparison.
                // Each input word is still read once, and the first mismatch
                // remains the carried first word of the following run.
                while (limit - count >= 4)
                {
                    {
                        oldAt += 4; finalAt += 4;
                        var nextValue = p.ReadUInt32(APTR.FromPointer(finalAt));
                        var nextOld = p.ReadUInt32(APTR.FromPointer(oldAt));
                        if (nextValue != nextOld) { value = nextValue; old = nextOld; goto UnchangedDone; }
                        count++;
                    }
                    {
                        oldAt += 4; finalAt += 4;
                        var nextValue = p.ReadUInt32(APTR.FromPointer(finalAt));
                        var nextOld = p.ReadUInt32(APTR.FromPointer(oldAt));
                        if (nextValue != nextOld) { value = nextValue; old = nextOld; goto UnchangedDone; }
                        count++;
                    }
                    {
                        oldAt += 4; finalAt += 4;
                        var nextValue = p.ReadUInt32(APTR.FromPointer(finalAt));
                        var nextOld = p.ReadUInt32(APTR.FromPointer(oldAt));
                        if (nextValue != nextOld) { value = nextValue; old = nextOld; goto UnchangedDone; }
                        count++;
                    }
                    {
                        oldAt += 4; finalAt += 4;
                        var nextValue = p.ReadUInt32(APTR.FromPointer(finalAt));
                        var nextOld = p.ReadUInt32(APTR.FromPointer(oldAt));
                        if (nextValue != nextOld) { value = nextValue; old = nextOld; goto UnchangedDone; }
                        count++;
                    }
                }
                while (count < limit)
                {
                    oldAt += 4; finalAt += 4;
                    var nextValue = p.ReadUInt32(APTR.FromPointer(finalAt));
                    var nextOld = p.ReadUInt32(APTR.FromPointer(oldAt));
                    if (nextValue != nextOld) { value = nextValue; old = nextOld; break; }
                    count++;
                }
            UnchangedDone:;
            }
            else if (kind == 0)
            {
                while (count < limit)
                {
                    oldAt += 4; finalAt += 4;
                    var nextValue = p.ReadUInt32(APTR.FromPointer(finalAt));
                    var nextOld = p.ReadUInt32(APTR.FromPointer(oldAt));
                    if (nextValue != 0 || nextOld == 0) { value = nextValue; old = nextOld; break; }
                    count++;
                }
            }
            else if (kind == 2)
            {
                while (count < limit)
                {
                    oldAt += 4; finalAt += 4;
                    var nextValue = p.ReadUInt32(APTR.FromPointer(finalAt));
                    var nextOld = p.ReadUInt32(APTR.FromPointer(oldAt));
                    if (nextValue != uint.MaxValue || nextOld == uint.MaxValue)
                    { value = nextValue; old = nextOld; break; }
                    count++;
                }
            }
            else
            {
                while (count < limit)
                {
                    oldAt += 4; finalAt += 4;
                    var nextValue = p.ReadUInt32(APTR.FromPointer(finalAt));
                    var nextOld = p.ReadUInt32(APTR.FromPointer(oldAt));
                    if (nextValue == nextOld || nextValue == 0 || nextValue == uint.MaxValue)
                    { value = nextValue; old = nextOld; break; }
                    count++;
                }
            }
            p.WriteUInt8(plan, (int)runs++, (byte)((kind << 6) | (count - 1)));
            length += kind == 3 ? 1 + count * 4 : 1;
            remaining -= count;
            if (remaining != 0 && count == limit)
            {
                oldAt += 4; finalAt += 4;
                value = p.ReadUInt32(APTR.FromPointer(finalAt));
                old = p.ReadUInt32(APTR.FromPointer(oldAt));
            }
        }
        return runs;
    }
}
