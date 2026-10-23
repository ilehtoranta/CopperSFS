using Amiga;

namespace CopperSFS;

/// <summary>
/// Optional caller-owned cache of clean data, below all transaction overlays.
/// Pages hold sixteen filesystem blocks, with a validity bit for each block.
/// Four-way sets give bounded lookup cost. Reused pages receive a second chance;
/// newly admitted pages remain probationary. No method performs device I/O.
/// </summary>
public static class AmigaFileSystemDataCache
{
    public const uint HeaderBytes = 32, EntryBytes = 16, BlocksPerPage = 16;
    public const uint MaximumPages = 256;
    public static class Header
    {
        public const int Pages = 0, BlockBytes = 4, Stride = 8, SetMask = 12;
        public const int Generation = 16, Populated = 20, PayloadBytes = 24, Victim = 28;
    }
    public static class Entry
    {
        public const int Page = 0, Valid = 4, Reused = 8, Generation = 12;
    }

    public static uint StorageBytes(uint bytes, uint pages) => HeaderBytes + pages * (EntryBytes + BlocksPerPage * bytes);

    public static bool Initialize<T>(ref T p, APTR cache, uint bytes, uint pages) where T : struct, IAmigaGuestMemory
    {
        if (cache.IsNull || bytes < 512 || bytes > 32768 || (bytes & (bytes - 1)) != 0 ||
            pages < 4 || pages > MaximumPages || (pages & (pages - 1)) != 0 ||
            !p.IsMapped(cache, StorageBytes(bytes, pages))) return false;
        p.Clear(cache, HeaderBytes);
        p.WriteUInt32(cache, Header.Pages, pages); p.WriteUInt32(cache, Header.BlockBytes, bytes);
        p.WriteUInt32(cache, Header.Stride, EntryBytes + BlocksPerPage * bytes);
        p.WriteUInt32(cache, Header.SetMask, pages / 4 - 1);
        p.WriteUInt32(cache, Header.PayloadBytes, pages * BlocksPerPage * bytes);
        // Generation zero means unused; payload memory need not be cleared.
        var entry = APTR.FromPointer(cache.Raw + HeaderBytes);
        for (var i = 0u; i < pages; i++, entry = APTR.FromPointer(entry.Raw + EntryBytes + BlocksPerPage * bytes))
            p.WriteUInt32(entry, Entry.Generation, 0);
        p.WriteUInt32(cache, Header.Generation, 1);
        return true;
    }

    public static void Invalidate<T>(ref T p, APTR cache) where T : struct, IAmigaGuestMemory
    {
        if (cache.IsNull || p.ReadUInt32(cache, Header.Populated) == 0) return;
        var generation = unchecked(p.ReadUInt32(cache, Header.Generation) + 1);
        if (generation == 0)
        {
            var entry = APTR.FromPointer(cache.Raw + HeaderBytes);
            var stride = p.ReadUInt32(cache, Header.Stride);
            for (var i = 0u; i < p.ReadUInt32(cache, Header.Pages); i++, entry = APTR.FromPointer(entry.Raw + stride))
                p.WriteUInt32(entry, Entry.Generation, 0);
            generation = 1;
        }
        p.WriteUInt32(cache, Header.Generation, generation);
        p.WriteUInt32(cache, Header.Populated, 0);
    }

    private static APTR Set<T>(ref T p, APTR cache, uint page) where T : struct, IAmigaGuestMemory
        => APTR.FromPointer(cache.Raw + HeaderBytes + ((page & p.ReadUInt32(cache, Header.SetMask)) * 4) * p.ReadUInt32(cache, Header.Stride));

    private static APTR Find<T>(ref T p, APTR cache, uint page) where T : struct, IAmigaGuestMemory
    {
        var entry = Set(ref p, cache, page); var stride = p.ReadUInt32(cache, Header.Stride);
        var generation = p.ReadUInt32(cache, Header.Generation);
        for (var i = 0; i < 4; i++, entry = APTR.FromPointer(entry.Raw + stride))
            if (p.ReadUInt32(entry, Entry.Generation) == generation && p.ReadUInt32(entry, Entry.Page) == page) return entry;
        return APTR.Null;
    }

    private static uint Mask(uint offset, uint count) => ((1u << (int)count) - 1) << (int)offset;

    /// <summary>All-or-nothing lookup: a miss leaves the destination untouched.
    /// The caller can then issue the original contiguous transfer, without
    /// splitting around cached islands or increasing its device request count.</summary>
    public static bool TryRead<T>(ref T p, APTR cache, uint block, uint count, APTR destination) where T : struct, IAmigaGuestMemory
    {
        if (cache.IsNull || count == 0 || p.ReadUInt32(cache, Header.Populated) == 0 ||
            count > p.ReadUInt32(cache, Header.Pages) * BlocksPerPage || block > uint.MaxValue - count) return false;
        // Check every page before changing any caller bytes.
        for (var done = 0u; done < count;)
        {
            var key = block + done; var offset = key & 15; var take = count - done;
            if (take > BlocksPerPage - offset) take = BlocksPerPage - offset;
            var entry = Find(ref p, cache, key >> 4); var mask = Mask(offset, take);
            if (entry.IsNull || (p.ReadUInt32(entry, Entry.Valid) & mask) != mask) return false;
            done += take;
        }
        var bytes = p.ReadUInt32(cache, Header.BlockBytes);
        for (var done = 0u; done < count;)
        {
            var key = block + done; var offset = key & 15; var take = count - done;
            if (take > BlocksPerPage - offset) take = BlocksPerPage - offset;
            var entry = Find(ref p, cache, key >> 4);
            p.Copy(APTR.FromPointer(entry.Raw + EntryBytes + offset * bytes), APTR.FromPointer(destination.Raw + done * bytes), take * bytes);
            p.WriteUInt32(entry, Entry.Reused, 1); done += take;
        }
        return true;
    }

    /// <summary>Admit only successfully read physical data with no intersecting
    /// pending writes. Never call this with an application-owned dirty image.</summary>
    public static void Store<T>(ref T p, APTR cache, uint block, uint count, APTR source) where T : struct, IAmigaGuestMemory
    {
        if (cache.IsNull || count == 0 || count > p.ReadUInt32(cache, Header.Pages) * BlocksPerPage || block > uint.MaxValue - count) return;
        var bytes = p.ReadUInt32(cache, Header.BlockBytes); var stride = p.ReadUInt32(cache, Header.Stride);
        var generation = p.ReadUInt32(cache, Header.Generation);
        for (var done = 0u; done < count;)
        {
            var key = block + done; var offset = key & 15; var take = count - done;
            if (take > BlocksPerPage - offset) take = BlocksPerPage - offset;
            var entry = Find(ref p, cache, key >> 4);
            if (entry.IsNull)
            {
                var first = Set(ref p, cache, key >> 4); var candidate = first;
                for (var i = 0; i < 4; i++, candidate = APTR.FromPointer(candidate.Raw + stride))
                    if (p.ReadUInt32(candidate, Entry.Generation) != generation) { entry = candidate; break; }
                if (entry.IsNull)
                {
                    var start = p.ReadUInt32(cache, Header.Victim) & 3;
                    p.WriteUInt32(cache, Header.Victim, start + 1);
                    for (var i = 0u; i < 4; i++)
                    {
                        candidate = APTR.FromPointer(first.Raw + ((start + i) & 3) * stride);
                        if (p.ReadUInt32(candidate, Entry.Reused) == 0) { entry = candidate; break; }
                        p.WriteUInt32(candidate, Entry.Reused, 0);
                    }
                    if (entry.IsNull) entry = APTR.FromPointer(first.Raw + start * stride);
                }
                p.WriteUInt32(entry, Entry.Generation, generation); p.WriteUInt32(entry, Entry.Page, key >> 4);
                p.WriteUInt32(entry, Entry.Valid, 0); p.WriteUInt32(entry, Entry.Reused, 0);
            }
            p.Copy(APTR.FromPointer(source.Raw + done * bytes), APTR.FromPointer(entry.Raw + EntryBytes + offset * bytes), take * bytes);
            p.WriteUInt32(entry, Entry.Valid, p.ReadUInt32(entry, Entry.Valid) | Mask(offset, take));
            done += take;
        }
        p.WriteUInt32(cache, Header.Populated, 1);
    }
}
