using Amiga;

namespace CopperSFS;

/// <summary>
/// Bounded caller-owned write-back storage. Platforms consult this before their
/// read cache. Each entry keeps the checkpoint image and a lazy operation savepoint;
/// failed operations cannot erase earlier acknowledged changes. Physical writes
/// and flushes must bypass this buffer. Commit uses the existing durable undo chain.
/// </summary>
public static class AmigaSmartFileSystemWriteBuffer
{
    public const uint MaximumCapacity = 32;
    private const uint Header = 336, EntryHeader = 24;
    private const uint HashOffset = 80;
    // Header: count, capacity, operation-start count, active, hidden, pending commit.
    public static uint StorageBytes(uint bytes, uint capacity) => Header + capacity * (EntryHeader + bytes * 3);

    public static bool Initialize<T>(ref T p, APTR buffer, uint bytes, uint capacity)
        where T : struct, IAmigaGuestMemory
    {
        if (buffer.IsNull || bytes < 512 || bytes > 32768 || (bytes & 3) != 0 ||
            capacity == 0 || capacity > MaximumCapacity || !p.IsMapped(buffer, StorageBytes(bytes, capacity))) return false;
        p.Clear(buffer, Header);
        p.WriteUInt32(buffer, 4, capacity);
        return true;
    }

    public static uint Count<T>(ref T p, APTR buffer) where T : struct, IAmigaGuestMemory
        => buffer.IsNull ? 0 : p.ReadUInt32(buffer);
    internal static APTR Slot(APTR buffer, uint bytes, uint index)
        => APTR.FromPointer(buffer.Raw + Header + index * (EntryHeader + bytes * 3));
    internal static APTR First(APTR buffer) => APTR.FromPointer(buffer.Raw + Header);
    internal static APTR Next(APTR slot, uint bytes) => APTR.FromPointer(slot.Raw + EntryHeader + bytes * 3);
    internal static APTR Image(APTR slot) => APTR.FromPointer(slot.Raw + EntryHeader);
    public static bool Intersects<T>(ref T p, APTR buffer, uint bytes, uint block, uint blocks)
        where T : struct, IAmigaGuestMemory
    {
        if (buffer.IsNull || p.ReadUInt32(buffer, 16) != 0) return false;
        var count = Count(ref p, buffer);
        var slot = First(buffer);
        for (var i = 0u; i < count; i++, slot = Next(slot, bytes))
        {
            var key = p.ReadUInt32(slot);
            if (key >= block && key - block < blocks) return true;
        }
        return false;
    }
    internal static APTR Checkpoint(APTR slot, uint bytes) => APTR.FromPointer(slot.Raw + EntryHeader + bytes);
    internal static APTR Find<T>(ref T p, APTR buffer, uint bytes, uint block) where T : struct, IAmigaGuestMemory
    {
        if (buffer.IsNull) return APTR.Null;
        var slot = APTR.FromPointer(p.ReadUInt32(buffer, (int)(HashOffset + (block & 63) * 4)));
        while (slot.IsNotNull)
        {
            if (p.ReadUInt32(slot) == block) return slot;
            slot = APTR.FromPointer(p.ReadUInt32(slot, 20));
        }
        return APTR.Null;
    }

    internal static void Discard<T>(ref T p, APTR buffer) where T : struct, IAmigaGuestMemory
    { p.WriteUInt32(buffer, 0, 0); p.Clear(APTR.FromPointer(buffer.Raw + HashOffset), 256); }

    public static bool TryRead<T>(ref T p, APTR buffer, uint bytes, uint block, APTR destination, uint metadataId,
        out bool found) where T : struct, IAmigaFileSystemPlatform
    {
        found = false;
        if (buffer.IsNull || p.ReadUInt32(buffer, 16) != 0) return true;
        // Most buckets contain one entry. Keep that hit in the caller to avoid
        // the native call/save/restore cost on every metadata access.
        var slot = APTR.FromPointer(p.ReadUInt32(buffer, (int)(HashOffset + (block & 63) * 4)));
        if (slot.IsNotNull && p.ReadUInt32(slot) != block) slot = Find(ref p, buffer, bytes, block);
        if (slot.IsNull) return true;
        found = true;
        var source = Image(slot);
        if (metadataId != 0 && p.ReadUInt32(slot, 8) != metadataId)
        {
            if (!AmigaFileSystemBlockIo.ValidateMetadata(ref p, block, metadataId, source, bytes)) return false;
            p.WriteUInt32(slot, 8, metadataId);
        }
        p.Copy(source, destination, bytes);
        return true;
    }

    public static APTR ReadView<T>(ref T p, APTR buffer, uint bytes, uint block, uint metadataId, out bool found)
        where T : struct, IAmigaFileSystemPlatform
    {
        found = false;
        if (buffer.IsNull || p.ReadUInt32(buffer, 16) != 0) return APTR.Null;
        var slot = APTR.FromPointer(p.ReadUInt32(buffer, (int)(HashOffset + (block & 63) * 4)));
        if (slot.IsNotNull && p.ReadUInt32(slot) != block) slot = Find(ref p, buffer, bytes, block);
        if (slot.IsNull) return APTR.Null;
        found = true;
        var source = Image(slot);
        if (p.ReadUInt32(slot, 8) != metadataId)
        {
            if (!AmigaFileSystemBlockIo.ValidateMetadata(ref p, block, metadataId, source, bytes)) return APTR.Null;
            p.WriteUInt32(slot, 8, metadataId);
        }
        return source;
    }

    internal static void Begin<T>(ref T p, APTR buffer, uint bytes) where T : struct, IAmigaGuestMemory
    {
        var count = Count(ref p, buffer);
        p.WriteUInt32(buffer, 8, count);
        p.WriteUInt32(buffer, 12, 1);
        var slot = First(buffer);
        for (var i = 0u; i < count; i++, slot = Next(slot, bytes)) p.WriteUInt32(slot, 4, 0);
    }

    internal static void Touch<T>(ref T p, APTR slot, uint bytes) where T : struct, IAmigaGuestMemory
    {
        if (p.ReadUInt32(slot, 4) == 0)
        {
            p.Copy(Image(slot), APTR.FromPointer(slot.Raw + EntryHeader + bytes * 2), bytes);
            p.WriteUInt32(slot, 4, 1);
        }
        p.WriteUInt32(slot, 8, 0);
    }

    internal static bool Store<T>(ref T p, APTR buffer, uint bytes, uint block, APTR source,
        uint undoImage, uint undoEntry, uint verifiedId, APTR existing, out bool full)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        full = false;
        var count = Count(ref p, buffer);
        var slot = existing;
        if (slot.IsNull)
        {
            if (count == p.ReadUInt32(buffer, 4)) { full = true; return false; }
            slot = Slot(buffer, bytes, count);
            if (!p.ReadBlock(block, Checkpoint(slot, bytes))) return false;
            p.WriteUInt32(slot, 0, block);
            p.WriteUInt32(slot, 4, 1);
            p.WriteUInt32(slot, 12, undoImage);
            p.WriteUInt32(slot, 16, undoEntry);
            var bucket = (int)(HashOffset + (block & 63) * 4);
            p.WriteUInt32(slot, 20, p.ReadUInt32(buffer, bucket));
            p.WriteUInt32(buffer, bucket, slot.Raw);
            p.WriteUInt32(buffer, 0, count + 1);
        }
        else Touch(ref p, slot, bytes);
        if (source != Image(slot)) p.Copy(source, Image(slot), bytes);
        p.WriteUInt32(slot, 8, verifiedId);
        return true;
    }

    internal static void End<T>(ref T p, APTR buffer, uint bytes, bool success) where T : struct, IAmigaGuestMemory
    {
        if (p.ReadUInt32(buffer, 12) == 0) return;
        if (!success)
        {
            var count = p.ReadUInt32(buffer, 8);
            var slot = First(buffer);
            for (var i = 0u; i < count; i++, slot = Next(slot, bytes))
            {
                if (p.ReadUInt32(slot, 4) != 0)
                {
                    p.Copy(APTR.FromPointer(slot.Raw + EntryHeader + bytes * 2), Image(slot), bytes);
                    p.WriteUInt32(slot, 8, 0);
                }
            }
            p.WriteUInt32(buffer, 0, count);
            p.Clear(APTR.FromPointer(buffer.Raw + HashOffset), 256);
            slot = First(buffer);
            for (var i = 0u; i < count; i++, slot = Next(slot, bytes))
            {
                var bucket = (int)(HashOffset + (p.ReadUInt32(slot) & 63) * 4);
                p.WriteUInt32(slot, 20, p.ReadUInt32(buffer, bucket));
                p.WriteUInt32(buffer, bucket, slot.Raw);
            }
        }
        p.WriteUInt32(buffer, 12, 0);
    }
}
