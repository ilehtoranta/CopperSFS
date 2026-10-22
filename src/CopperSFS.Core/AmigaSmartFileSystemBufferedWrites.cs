using Amiga;

namespace CopperSFS;

public static partial class AmigaSmartFileSystemWriteCore
{
    private static APTR View<T>(ref T p, ref Work w, uint block, uint id, APTR scratch)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        var image = block < w.BlockCount ? p.ReadMetadataView(block, id, scratch) : APTR.Null;
        if (image.IsNull) Fail(ref w, DOS.Error.DiskNotValidated);
        return image;
    }

    private static APTR Edit<T>(ref T p, ref Work w, uint block, uint id, APTR scratch)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Buffered.IsNull || !w.JournalActive || w.BufferCommitting)
            return Read(ref p, ref w, block, id, scratch) ? scratch : APTR.Null;
        var slot = AmigaSmartFileSystemWriteBuffer.Find(ref p, w.Buffered, w.BlockBytes, block);
        if (slot.IsNull)
        {
            if (!Read(ref p, ref w, block, id, scratch) || !BufferWrite(ref p, ref w, block, scratch, id)) return APTR.Null;
            slot = AmigaSmartFileSystemWriteBuffer.Find(ref p, w.Buffered, w.BlockBytes, block);
        }
        var image = AmigaSmartFileSystemWriteBuffer.Image(slot);
        if (p.ReadUInt32(slot, 8) != id && !AmigaFileSystemBlockIo.ValidateMetadata(ref p, block, id, image, w.BlockBytes))
        { Fail(ref w, DOS.Error.DiskNotValidated); return APTR.Null; }
        // Capture the savepoint before handing out a mutable private image.
        // Every caller seals and publishes it before another metadata lookup.
        AmigaSmartFileSystemWriteBuffer.Touch(ref p, slot, w.BlockBytes);
        return image;
    }

    private static bool BufferWrite<T>(ref T p, ref Work w, uint target, APTR source, uint verifiedId)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        uint image = 0, entry = 0;
        var existing = AmigaSmartFileSystemWriteBuffer.Find(ref p, w.Buffered, w.BlockBytes, target);
        if (existing.IsNotNull && source == AmigaSmartFileSystemWriteBuffer.Image(existing) &&
            p.ReadUInt32(existing, 4) != 0)
        {
            // Edit already captured this operation's savepoint before exposing
            // the owned image. Publishing it only restores its verification;
            // there is no second copy or savepoint to capture.
            p.WriteUInt32(existing, 8, verifiedId);
            return true;
        }
        if (existing.IsNull)
        {
            if (AmigaSmartFileSystemWriteBuffer.Count(ref p, w.Buffered) == p.ReadUInt32(w.Buffered, 4))
            { w.BufferFull = true; return Fail(ref w, DOS.Error.NoFreeStore); }
            // Reserve rollback sectors before acknowledging a dirty block. The
            // allocator excludes these RAM reservations until the group commits.
            // Thus a full disk cannot strand previously acknowledged changes.
            image = JournalSpace(ref p, ref w, target, source, 0);
            if (image == 0) return false;
            entry = JournalSpace(ref p, ref w, target, source, image);
            if (entry == 0) return false;
        }
        return AmigaSmartFileSystemWriteBuffer.Store(ref p, w.Buffered, w.BlockBytes, target, source,
            image, entry, verifiedId, existing, out w.BufferFull) || Fail(ref w, w.BufferFull ? DOS.Error.NoFreeStore : DOS.Error.DiskNotValidated);
    }
    /// <summary>Recover physical storage without discarding acknowledged buffered changes.</summary>
    public static bool Recover<T>(ref T p, ref Work w) where T : struct, IAmigaWritableFileSystemPlatform
    {
        if (w.Buffered.IsNotNull)
        { p.WriteUInt32(w.Buffered, 16, 1); p.WriteUInt32(w.Buffered, 24, 0); p.WriteUInt32(w.Buffered, 68, 0); }
        var success = RecoverDisk(ref p, ref w);
        if (w.Buffered.IsNotNull)
        {
            p.WriteUInt32(w.Buffered, 16, 0);
            if (success) p.WriteUInt32(w.Buffered, 20, 0);
        }
        return success;
    }

    /// <summary>
    /// Make every previously successful operation durable. Undo records are
    /// flushed before publishing the marker; all home blocks are flushed before
    /// clearing it. Failed commits retain their RAM images for a later retry.
    /// </summary>
    public static bool Flush<T>(ref T p, ref Work w) where T : struct, IAmigaWritableFileSystemPlatform
    {
        w.Error = DOS.Error.None;
        if (w.Buffered.IsNull) return p.Flush() || Fail(ref w, DOS.Error.DiskNotValidated);
        if (p.ReadUInt32(w.Buffered, 20) != 0 && !Recover(ref p, ref w)) return false;
        var count = AmigaSmartFileSystemWriteBuffer.Count(ref p, w.Buffered);
        if (count == 0) return p.Flush() || Fail(ref w, DOS.Error.DiskNotValidated);
        if (!Prepare(ref p, ref w)) return false;
        w.BufferCommitting = true; w.JournalActive = true; w.JournalWrapped = false;
        if (w.JournalCursor < w.ReservedBlocks || w.JournalCursor >= w.BlockCount - 1)
            w.JournalCursor = w.ReservedBlocks;
        p.WriteUInt32(w.Buffered, 20, 1);
        var success = true;
        var slot = AmigaSmartFileSystemWriteBuffer.First(w.Buffered);
        for (var i = 0u; i < count && success; i++, slot = AmigaSmartFileSystemWriteBuffer.Next(slot, w.BlockBytes))
        {
            success = JournalWrite(ref p, ref w, p.ReadUInt32(slot), AmigaSmartFileSystemWriteBuffer.Image(slot));
        }
        if (success) success = Finish(ref p, ref w);
        w.BufferCommitting = false; w.JournalActive = false;
        if (success)
        {
            AmigaSmartFileSystemWriteBuffer.Discard(ref p, w.Buffered);
            p.WriteUInt32(w.Buffered, 20, 0);
        }
        return success;
    }

    private static bool Execute<T>(ref T p, ref Work w, ref Request r, uint operation, uint argument, out uint actual)
        where T : struct, IAmigaWritableFileSystemPlatform
    {
        actual = 0;
        var buffer = w.Buffered;
        var original = r;
        // Only consecutive writes preserve a record's location. Namespace,
        // size and metadata operations can compact or relocate containers.
        if (buffer.IsNotNull && operation != 5) p.WriteUInt32(buffer, 68, 0);
        if (buffer.IsNotNull && p.ReadUInt32(buffer, 20) != 0 && !Recover(ref p, ref w)) return false;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            w.BufferFull = false;
            if (w.Buffered.IsNotNull)
            {
                w.BlockBytes = p.ReadUInt32(buffer, 24) != 0 ? p.ReadUInt32(buffer, 60) : p.BlockBytes;
                AmigaSmartFileSystemWriteBuffer.Begin(ref p, buffer, w.BlockBytes);
            }
            var success = operation switch
            {
                0 => CreateOperation(ref p, ref w, ref r, argument != 0),
                1 => TruncateOperation(ref p, ref w, r.Node),
                2 => RemoveOperation(ref p, ref w, r.Node),
                3 => RenameOperation(ref p, ref w, ref r),
                4 => MetadataOperation(ref p, ref w, ref r, (DosPacketAction)argument),
                5 => WriteOperation(ref p, ref w, ref r, out actual),
                6 => ResizeOperation(ref p, ref w, r.Node, r.Length),
                _ => false,
            };
            if (w.Buffered.IsNotNull) AmigaSmartFileSystemWriteBuffer.End(ref p, buffer, w.BlockBytes, success);
            if (success || w.Buffered.IsNull || (!w.BufferFull &&
                (w.Error != DOS.Error.DiskFull || AmigaSmartFileSystemWriteBuffer.Count(ref p, buffer) == 0)))
            {
                w.Buffered = buffer;
                if (buffer.IsNotNull) p.WriteUInt32(buffer, 16, 0);
                if (!success) { r = original; actual = 0; }
                return success;
            }
            // A single operation larger than the bounded cache uses the strict
            // engine after committing earlier successful operations. Never
            // publish a partial operation merely to make room in RAM.
            if (!Flush(ref p, ref w)) { r = original; actual = 0; return false; }
            w.Buffered = APTR.Null;
            p.WriteUInt32(buffer, 16, 1);
            r = original; actual = 0;
        }
        return false;
    }

    public static bool Create<T>(ref T p, ref Work w, ref Request r, bool directory)
        where T : struct, IAmigaWritableFileSystemPlatform => Execute(ref p, ref w, ref r, 0, directory ? 1u : 0u, out _);
    public static bool Truncate<T>(ref T p, ref Work w, uint node) where T : struct, IAmigaWritableFileSystemPlatform
    { var r = new Request { Node = node }; return Execute(ref p, ref w, ref r, 1, 0, out _); }
    public static bool Remove<T>(ref T p, ref Work w, uint node) where T : struct, IAmigaWritableFileSystemPlatform
    { var r = new Request { Node = node }; return Execute(ref p, ref w, ref r, 2, 0, out _); }
    public static bool Rename<T>(ref T p, ref Work w, ref Request r)
        where T : struct, IAmigaWritableFileSystemPlatform => Execute(ref p, ref w, ref r, 3, 0, out _);
    public static bool SetMetadata<T>(ref T p, ref Work w, ref Request r, DosPacketAction action)
        where T : struct, IAmigaWritableFileSystemPlatform => Execute(ref p, ref w, ref r, 4, (uint)action, out _);
    public static bool Write<T>(ref T p, ref Work w, ref Request r, out uint actual)
        where T : struct, IAmigaWritableFileSystemPlatform => Execute(ref p, ref w, ref r, 5, 0, out actual);
    public static bool Resize<T>(ref T p, ref Work w, uint node, uint newSize) where T : struct, IAmigaWritableFileSystemPlatform
    { var r = new Request { Node = node, Length = newSize }; return Execute(ref p, ref w, ref r, 6, 0, out _); }
}
