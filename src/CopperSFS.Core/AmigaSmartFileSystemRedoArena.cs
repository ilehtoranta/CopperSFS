using Amiga;

namespace CopperSFS;

/// <summary>
/// Bounded TRST overlay in caller memory. A 24-byte header and open-addressed
/// index precede the block/image slots. The private index can be cloned with
/// occupied slots; it is never persisted or allocated outside the supplied arena.
/// </summary>
public static class AmigaSmartFileSystemRedoArena
{
    public const uint HeaderBytes = 24;
    public const uint SlotHeaderBytes = 8;
    private const uint Indexed = 0x80000000;
    internal const uint Deferred = 0x40000000;

    // Indexed arenas use word 12 as 0/1 (normal/full), or the owning group
    // address while its Deferred format bit is set. Either marker detects a
    // damaged counterpart. Legacy SFS2 arenas keep their existing meaning.
    public static bool Materialize<T>(ref T p, APTR arena) where T : struct, IAmigaGuestMemory
    {
        var format = p.ReadUInt32(arena, 8);
        if ((format & Indexed) == 0) return (format & Deferred) == 0;
        var owner = p.ReadUInt32(arena, 12);
        return owner <= 1 && (format & Deferred) == 0 ||
            AmigaSmartFileSystemWriteCore.MaterializeTrstRun(ref p, arena, APTR.FromPointer(owner));
    }

    /// <summary>Exact owned storage for the indexed layout within a byte budget.
    /// The returned span is sufficient for Initialize and contains every slot.</summary>
    public static uint FitBytes(uint budget, uint blockBytes)
    {
        if (blockBytes < 512 || blockBytes > 32768 || budget < blockBytes + HeaderBytes + 16) return 0;
        var stride = blockBytes + SlotHeaderBytes;
        while (true)
        {
            var maximum = (budget - HeaderBytes) / stride;
            var buckets = 2u;
            while (buckets <= maximum || buckets - (buckets >> 2) < maximum) buckets <<= 1;
            var indexBytes = buckets * 4;
            var capacity = (budget - HeaderBytes - indexBytes) / stride;
            var fitted = HeaderBytes + indexBytes + capacity * stride;
            if (fitted == budget) return fitted;
            // Removing padding can cross an index-size threshold. Normalize
            // to a fixed point so Initialize retains the allocation extent.
            budget = fitted;
        }
    }

    /// <summary>Storage extent of an initialized, caller-owned indexed arena.
    /// Callers allocating FitBytes must release this same span; no on-disk or
    /// external input may be used as an allocation descriptor.</summary>
    public static uint StorageBytes<T>(ref T p, APTR arena) where T : struct, IAmigaGuestMemory
        => p.ReadUInt32(arena, 20) - arena.Raw +
            p.ReadUInt32(arena, 4) * ((p.ReadUInt32(arena, 8) & ~(Indexed | Deferred)) + SlotHeaderBytes);

    public static bool Initialize<T>(ref T p, APTR arena, uint arenaBytes, uint blockBytes)
        where T : struct, IAmigaGuestMemory
    {
        if (blockBytes < 512 || blockBytes > 32768 || arenaBytes < blockBytes + HeaderBytes + 16 ||
            arena.IsNull || !p.IsMapped(arena, arenaBytes)) return false;
        var stride = blockBytes + SlotHeaderBytes;
        var maximum = (arenaBytes - HeaderBytes) / stride;
        // At most 75% occupied, even before accounting for index storage. Thus
        // every probe terminates at an empty bucket, including a full arena.
        var buckets = 2u;
        while (buckets <= maximum || buckets - (buckets >> 2) < maximum) buckets <<= 1;
        var indexBytes = buckets * 4;
        var capacity = (arenaBytes - HeaderBytes - indexBytes) / stride;
        p.Clear(arena, HeaderBytes + indexBytes);
        p.WriteUInt32(arena, 4, capacity);
        // The high bit distinguishes this private layout from SFS2's existing
        // 16-byte overlay. Valid block sizes never use it. Keep both readers.
        p.WriteUInt32(arena, 8, blockBytes | Indexed);
        p.WriteUInt32(arena, 16, buckets - 1);
        p.WriteUInt32(arena, 20, arena.Raw + HeaderBytes + indexBytes);
        return true;
    }

    /// <summary>Clone a valid, owned indexed arena into a disjoint region of
    /// the same available size. Copy only the header, index and occupied slots;
    /// rebase owned index pointers without rehashing or copying each block.
    /// The source's images and verification state remain untouched.</summary>
    public static bool Clone<T>(ref T p, APTR source, APTR destination, uint arenaBytes)
        where T : struct, IAmigaGuestMemory
    {
        if (source.IsNull || destination.IsNull || arenaBytes < HeaderBytes + 16 + 512 ||
            !p.IsMapped(source, arenaBytes) || !p.IsMapped(destination, arenaBytes) ||
            (source.Raw <= destination.Raw ? destination.Raw - source.Raw < arenaBytes : source.Raw - destination.Raw < arenaBytes))
            return false;
        var format = p.ReadUInt32(source, 8);
        var bytes = format & ~(Indexed | Deferred);
        var mask = p.ReadUInt32(source, 16);
        if ((format & Indexed) == 0 || bytes < 512 || bytes > 32768 ||
            mask == 0 || mask == uint.MaxValue || (mask & (mask + 1)) != 0 ||
            mask + 1 > (arenaBytes - HeaderBytes) / 4) return false;
        var prefix = HeaderBytes + (mask + 1) * 4;
        var stride = bytes + SlotHeaderBytes;
        var capacity = p.ReadUInt32(source, 4);
        var count = p.ReadUInt32(source);
        if (capacity == 0 || capacity > mask || capacity > (mask + 1) - ((mask + 1) >> 2) ||
            capacity > (arenaBytes - prefix) / stride || count > capacity ||
            p.ReadUInt32(source, 20) != source.Raw + prefix) return false;
        if (((format & Deferred) != 0 || p.ReadUInt32(source, 12) > 1) && !Materialize(ref p, source)) return false;
        p.Copy(source, destination, prefix + count * stride);
        p.WriteUInt32(destination, 12, 0);
        p.WriteUInt32(destination, 20, destination.Raw + prefix);
        var bucket = APTR.FromPointer(destination.Raw + HeaderBytes);
        for (var i = 0u; i <= mask; i++, bucket = APTR.FromPointer(bucket.Raw + 4))
        {
            var slot = p.ReadUInt32(bucket);
            if (slot != 0) p.WriteUInt32(bucket, 0, unchecked(slot + destination.Raw - source.Raw));
        }
        return true;
    }

    private static APTR Probe<T>(ref T p, APTR arena, uint block) where T : struct, IAmigaGuestMemory
    {
        var mask = p.ReadUInt32(arena, 16);
        // Fold high block bits without a multiply/divide on MC68000. Sequential
        // data blocks spread across buckets; wide block numbers retain entropy.
        var bucket = (block ^ (block >> 9) ^ (block >> 18)) & mask;
        while (true)
        {
            var address = APTR.FromPointer(arena.Raw + HeaderBytes + bucket * 4);
            var slot = p.ReadUInt32(address);
            if (slot == 0 || p.ReadUInt32(APTR.FromPointer(slot)) == block) return address;
            bucket = (bucket + 1) & mask;
        }
    }

    /// <summary>Read or edit an owned metadata image with verification stored in
    /// its slot. A mutable view revokes verification before returning the image.
    /// Borrowed read views may not be modified without Store or an editable view.</summary>
    public static APTR MetadataView<T>(ref T p, APTR arena, uint block, uint id,
        bool editable, out bool found) where T : struct, IAmigaFileSystemPlatform
    {
        var format = p.ReadUInt32(arena, 8);
        if ((format & Deferred) != 0 || ((format & Indexed) != 0 && p.ReadUInt32(arena, 12) > 1))
        {
            if (!Materialize(ref p, arena)) { found = true; return APTR.Null; }
            format = p.ReadUInt32(arena, 8);
        }
        if ((format & Indexed) == 0)
        {
            var legacy = FindLinear(ref p, arena, block, format);
            found = legacy.IsNotNull;
            return found && AmigaFileSystemBlockIo.ValidateMetadata(ref p, block, id, legacy, format) ? legacy : APTR.Null;
        }
        var slot = APTR.FromPointer(p.ReadUInt32(Probe(ref p, arena, block)));
        found = slot.IsNotNull;
        if (!found) return APTR.Null;
        var image = APTR.FromPointer(slot.Raw + SlotHeaderBytes);
        if (id == 0 || p.ReadUInt32(slot, 4) != id)
        {
            if (!AmigaFileSystemBlockIo.ValidateMetadata(ref p, block, id, image, format & ~Indexed)) return APTR.Null;
            if (!editable) p.WriteUInt32(slot, 4, id);
        }
        if (editable) p.WriteUInt32(slot, 4, 0);
        return image;
    }

    public static APTR Find<T>(ref T p, APTR arena, uint block) where T : struct, IAmigaGuestMemory
        => TryFind(ref p, arena, block, out var image) ? image : APTR.Null;

    // Distinguish an absent block from failed materialization. Raw disk readers
    // must never fall through to the old disk image after a private-state error.
    public static bool TryFind<T>(ref T p, APTR arena, uint block, out APTR image) where T : struct, IAmigaGuestMemory
    {
        var format = p.ReadUInt32(arena, 8);
        if ((format & Deferred) != 0 || ((format & Indexed) != 0 && p.ReadUInt32(arena, 12) > 1))
        {
            if (!Materialize(ref p, arena)) { image = APTR.Null; return false; }
            format = p.ReadUInt32(arena, 8);
        }
        if ((format & Indexed) == 0) { image = FindLinear(ref p, arena, block, format); return true; }
        var slot = p.ReadUInt32(Probe(ref p, arena, block));
        image = slot == 0 ? APTR.Null : APTR.FromPointer(slot + SlotHeaderBytes);
        return true;
    }

    // A cached index is usable only for the same unchanged indexed arena.
    // Validate its bounds, target and verification state just as MetadataView
    // does after probing. The caller owns the index's lifetime certificate.
    internal static APTR OwnedMetadataAt<T>(ref T p, APTR arena, uint index, uint block, uint id)
        where T : struct, IAmigaGuestMemory
        => Materialize(ref p, arena) ? OwnedMetadataAtRaw(ref p, arena, index, block, id) : APTR.Null;

    // The materializer alone may inspect owned images without recursively
    // requesting materialization. Its certificate validates the arena first.
    internal static APTR OwnedMetadataAtRaw<T>(ref T p, APTR arena, uint index, uint block, uint id)
        where T : struct, IAmigaGuestMemory
    {
        var format = p.ReadUInt32(arena, 8) & ~Deferred;
        if ((format & Indexed) == 0 || index >= p.ReadUInt32(arena)) return APTR.Null;
        // A 512-byte block has a 520-byte slot. Avoid the native compiler's
        // general 32-bit multiply in the common continuation lookup.
        var offset = format == (Indexed | 512) ? (index << 9) + (index << 3) :
            index * ((format & ~Indexed) + SlotHeaderBytes);
        var slot = APTR.FromPointer(p.ReadUInt32(arena, 20) + offset);
        if (p.ReadUInt32(slot) != block) return APTR.Null;
        var image = APTR.FromPointer(slot.Raw + SlotHeaderBytes);
        if (id == 0 || p.ReadUInt32(slot, 4) != id)
        {
            if (!AmigaFileSystemBlockIo.ValidateMetadata(ref p, block, id, image, format & ~Indexed)) return APTR.Null;
            p.WriteUInt32(slot, 4, id);
        }
        return image;
    }

    public static bool Store<T>(ref T p, APTR arena, uint block, APTR source) where T : struct, IAmigaGuestMemory
        => StoreImage(ref p, arena, block, source, 0);

    // Only sealed metadata and immutable group copies may preserve verification.
    // Public/raw stores always revoke it, including a self-store.
    internal static bool StoreSealed<T>(ref T p, APTR arena, uint block, APTR source) where T : struct, IAmigaGuestMemory
        => StoreImage(ref p, arena, block, source, p.ReadUInt32(source, 8) == block ? p.ReadUInt32(source) : 0);

    // Only for an image already obtained from this indexed arena's MetadataView.
    // The caller keeps the arena and slot layout unchanged, revokes verification
    // before its bounded mutation, and restores it only after sealing the image.
    // No allocation, lookup, I/O or failure is possible in this publication step.
    internal static void SetOwnedVerification<T>(ref T p, APTR image, uint id) where T : struct, IAmigaGuestMemory
        => p.WriteUInt32(APTR.FromPointer(image.Raw - 4), 0, id);

    internal static bool StoreImage<T>(ref T p, APTR arena, uint block, APTR source, uint verifiedId) where T : struct, IAmigaGuestMemory
    {
        var format = p.ReadUInt32(arena, 8);
        if ((format & Deferred) != 0 || ((format & Indexed) != 0 && p.ReadUInt32(arena, 12) > 1))
        {
            if (!Materialize(ref p, arena)) return false;
            format = p.ReadUInt32(arena, 8);
        }
        if ((format & Indexed) == 0) return StoreLinear(ref p, arena, block, source, format);
        var bytes = format & ~Indexed;
        var bucket = Probe(ref p, arena, block);
        var slot = p.ReadUInt32(bucket);
        if (slot == 0)
        {
            var count = p.ReadUInt32(arena);
            if (count == p.ReadUInt32(arena, 4)) { p.WriteUInt32(arena, 12, 1); return false; }
            slot = p.ReadUInt32(arena, 20) + count * (bytes + SlotHeaderBytes);
            p.WriteUInt32(APTR.FromPointer(slot), 0, block);
            p.WriteUInt32(bucket, 0, slot); p.WriteUInt32(arena, 0, count + 1);
        }
        var image = APTR.FromPointer(slot + SlotHeaderBytes);
        p.WriteUInt32(APTR.FromPointer(slot), 4, 0);
        if (source != image) p.Copy(source, image, bytes);
        if (verifiedId != 0) p.WriteUInt32(APTR.FromPointer(slot), 4, verifiedId);
        return true;
    }

    // Compatibility for the separately qualified SFS2 overlay. Its initializer
    // and slot layout remain unchanged; classic TRST always uses the index.
    private static APTR FindLinear<T>(ref T p, APTR arena, uint block, uint bytes)
        where T : struct, IAmigaGuestMemory
    {
        var count = p.ReadUInt32(arena);
        var slot = APTR.FromPointer(arena.Raw + 16);
        for (var i = 0u; i < count; i++, slot = APTR.FromPointer(slot.Raw + bytes + 4))
            if (p.ReadUInt32(slot) == block) return APTR.FromPointer(slot.Raw + 4);
        return APTR.Null;
    }

    private static bool StoreLinear<T>(ref T p, APTR arena, uint block, APTR source, uint bytes)
        where T : struct, IAmigaGuestMemory
    {
        var image = FindLinear(ref p, arena, block, bytes);
        if (image.IsNull)
        {
            var count = p.ReadUInt32(arena);
            if (count == p.ReadUInt32(arena, 4)) { p.WriteUInt32(arena, 12, 1); return false; }
            var slot = APTR.FromPointer(arena.Raw + 16 + count * (bytes + 4));
            p.WriteUInt32(slot, 0, block); p.WriteUInt32(arena, 0, count + 1);
            image = APTR.FromPointer(slot.Raw + 4);
        }
        p.Copy(source, image, bytes); return true;
    }
}
