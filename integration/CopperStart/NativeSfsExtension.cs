using Amiga;
using CopperSharp.Compiler;
using CopperSharp.Sdk.Amiga;

namespace CopperStart.Dos;

/// <summary>Private ABI between the fixed E0 extension ROM and the Kickstart filesystem handler.</summary>
public static class NativeSfsExtension
{
    // CSE0 identifies ABI version 1. The extension image is never a reset ROM;
    // its first longword is used as a signature instead of a stack pointer.
    public const uint Base = 0x00E0_0000, Bytes = 0x0004_0000, Magic = 0x4353_4530;
    private const int Operation = 0, Directory = 4, Node = 8, Position = 12,
        Length = 16, Name = 20, Buffer = 24, Argument = 28, Actual = 32, Error = 36;
    private const uint RecoverOperation = 1, CreateOperation = 2, TruncateOperation = 3,
        RemoveOperation = 4, RenameOperation = 5, MetadataOperation = 6,
        WriteOperation = 7, ResizeOperation = 8, RootOperation = 9, MountOperation = 10,
        ResolveOperation = 11, DescribeOperation = 12, ReadOperation = 13,
        ExamineOperation = 14, ExamineNextOperation = 15, CountUsedOperation = 16,
        PacketOperation = 17;

    [AmigaIndirectCall(M68kRegister.A3)]
    [return: M68kRegister(M68kRegister.D0)]
    private static extern uint Call([M68kRegister(M68kRegister.A3)] APTR entry,
        [M68kRegister(M68kRegister.A0)] APTR state,
        [M68kRegister(M68kRegister.A1)] APTR request);

    private static APTR Scratch(APTR state) => APTR.FromPointer(state.Raw + NativeAmigaFileSystem.ExtensionScratchOffset);

    public static bool Available()
    {
#if COPPERSTART_SFS_STANDALONE
        return true;
#else
        var header = APTR.FromPointer(Base);
        if (APTR.ReadUInt32(header, 0) != Magic) return false;
        var entry = APTR.ReadUInt32(header, 4);
        return (entry & 1) == 0 && entry >= Base + 8 && entry < Base + Bytes;
#endif
    }

    public static uint ClientServiceAddress()
    {
#if COPPERSTART_SFS_STANDALONE
        return 0;
#else
        if (!Available()) return 0;
        var entry = APTR.ReadUInt32(APTR.FromPointer(Base), 8);
        return (entry & 1) == 0 && entry >= Base + 12 && entry < Base + Bytes ? entry : 0;
#endif
    }

    private static bool Invoke(APTR state, uint operation, ref AmigaSmartFileSystemWriteCore.Work work,
        ref AmigaSmartFileSystemWriteCore.Request request, uint argument, out uint actual)
    {
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Directory, request.Directory);
        APTR.WriteUInt32(scratch, Node, request.Node);
        APTR.WriteUInt32(scratch, Position, request.Position);
        APTR.WriteUInt32(scratch, Length, request.Length);
        APTR.WriteUInt32(scratch, Name, request.Name.Raw);
        APTR.WriteUInt32(scratch, Buffer, request.Buffer.Raw);
        APTR.WriteUInt32(scratch, Argument, argument);
        var success = InvokeSimple(state, operation, out actual, out work.Error);
        request.Directory = APTR.ReadUInt32(scratch, Directory);
        request.Node = APTR.ReadUInt32(scratch, Node);
        request.Position = APTR.ReadUInt32(scratch, Position);
        request.Length = APTR.ReadUInt32(scratch, Length);
        return success;
    }

    private static bool InvokeSimple(APTR state, uint operation, out uint actual, out DOS.Error error)
    {
        actual = 0;
        error = DOS.Error.DiskNotValidated;
#if !COPPERSTART_SFS_STANDALONE
        if (!Available()) return false;
        var entry = APTR.ReadUInt32(APTR.FromPointer(Base), 4);
#endif
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Operation, operation);
        APTR.WriteUInt32(scratch, Actual, 0);
#if COPPERSTART_SFS_STANDALONE
        var success = Entry(state, scratch) != 0;
#else
        var success = Call(APTR.FromPointer(entry), state, scratch) != 0;
#endif
        actual = APTR.ReadUInt32(scratch, Actual);
        error = (DOS.Error)APTR.ReadUInt32(scratch, Error);
        return success;
    }

    public static bool Recover(APTR state, ref AmigaSmartFileSystemWriteCore.Work work)
        => InvokeSimple(state, RecoverOperation, out _, out work.Error);
    public static bool Create(APTR state, ref AmigaSmartFileSystemWriteCore.Work work,
        ref AmigaSmartFileSystemWriteCore.Request request, bool directory) =>
        Invoke(state, CreateOperation, ref work, ref request, directory ? 1u : 0u, out _);
    public static bool Truncate(APTR state, ref AmigaSmartFileSystemWriteCore.Work work, uint node)
    { var request = new AmigaSmartFileSystemWriteCore.Request { Node = node }; return Invoke(state, TruncateOperation, ref work, ref request, 0, out _); }
    public static bool Remove(APTR state, ref AmigaSmartFileSystemWriteCore.Work work, uint node)
    { var request = new AmigaSmartFileSystemWriteCore.Request { Node = node }; return Invoke(state, RemoveOperation, ref work, ref request, 0, out _); }
    public static bool Rename(APTR state, ref AmigaSmartFileSystemWriteCore.Work work,
        ref AmigaSmartFileSystemWriteCore.Request request) =>
        Invoke(state, RenameOperation, ref work, ref request, 0, out _);
    public static bool SetMetadata(APTR state, ref AmigaSmartFileSystemWriteCore.Work work,
        ref AmigaSmartFileSystemWriteCore.Request request, DosPacketAction action) =>
        Invoke(state, MetadataOperation, ref work, ref request, (uint)action, out _);
    public static bool Write(APTR state, ref AmigaSmartFileSystemWriteCore.Work work,
        ref AmigaSmartFileSystemWriteCore.Request request, out uint actual)
    {
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Node, request.Node);
        APTR.WriteUInt32(scratch, Position, request.Position);
        APTR.WriteUInt32(scratch, Buffer, request.Buffer.Raw);
        APTR.WriteUInt32(scratch, Length, request.Length);
        var success = InvokeSimple(state, WriteOperation, out actual, out work.Error);
        if (success) request.Position = APTR.ReadUInt32(scratch, Position);
        return success;
    }
    public static bool Resize(APTR state, ref AmigaSmartFileSystemWriteCore.Work work, uint node, uint bytes)
    {
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Node, node);
        APTR.WriteUInt32(scratch, Length, bytes);
        return InvokeSimple(state, ResizeOperation, out _, out work.Error);
    }

    public static bool Root(APTR state)
        => InvokeSimple(state, RootOperation, out _, out _);
    public static bool Mount(APTR state)
        => InvokeSimple(state, MountOperation, out _, out _);
    public static uint Resolve(APTR state, uint directory, APTR name, uint length, out DOS.Error error)
    {
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Directory, directory);
        APTR.WriteUInt32(scratch, Name, name.Raw);
        APTR.WriteUInt32(scratch, Length, length);
        return InvokeSimple(state, ResolveOperation, out _, out error)
            ? APTR.ReadUInt32(scratch, Node) : 0;
    }
    public static bool Describe(APTR state, uint node, out uint size, out bool directory, out uint parent)
    {
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Node, node);
        var success = InvokeSimple(state, DescribeOperation, out _, out _);
        size = APTR.ReadUInt32(scratch, Position);
        directory = APTR.ReadUInt32(scratch, Length) != 0;
        parent = APTR.ReadUInt32(scratch, Directory);
        return success;
    }
    public static int Read(APTR state, uint node, uint position, APTR destination, uint length, out DOS.Error error)
    {
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Node, node);
        APTR.WriteUInt32(scratch, Position, position);
        APTR.WriteUInt32(scratch, Buffer, destination.Raw);
        APTR.WriteUInt32(scratch, Length, length);
        return InvokeSimple(state, ReadOperation, out var actual, out error)
            ? unchecked((int)actual) : -1;
    }
    public static bool Examine(APTR state, uint node, APTR fib)
    {
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Node, node);
        APTR.WriteUInt32(scratch, Buffer, fib.Raw);
        return InvokeSimple(state, ExamineOperation, out _, out _);
    }
    public static DOS.Error ExamineNext(APTR state, uint directory, APTR fib)
    {
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Directory, directory);
        APTR.WriteUInt32(scratch, Buffer, fib.Raw);
        InvokeSimple(state, ExamineNextOperation, out _, out var error);
        return error;
    }
    public static bool CountUsed(APTR state, out uint used)
        => InvokeSimple(state, CountUsedOperation, out used, out _);
    public static int Packet(APTR state, APTR packet, DosPacketAction action, out DOS.Error error)
    {
        var scratch = Scratch(state);
        APTR.WriteUInt32(scratch, Buffer, packet.Raw);
        APTR.WriteUInt32(scratch, Argument, (uint)action);
        return InvokeSimple(state, PacketOperation, out var result, out error)
            ? unchecked((int)result) : 0;
    }

    /// <summary>Only the extension-ROM compiler root reaches the SFS mutation engine.</summary>
    public static uint ImageRoot() => 0;

    [M68kExport("copperstart.dos.native-client-service.extension")]
    [return: M68kRegister(M68kRegister.D0)]
    public static uint ClientServiceEntry([M68kRegister(M68kRegister.D0)] uint operation,
        [M68kRegister(M68kRegister.A0)] APTR state,
        [M68kRegister(M68kRegister.A1)] APTR packet) =>
        DosNativeClientService.Dispatch(operation, state, packet);

    [M68kExport("copperstart.sfs.extension.entry")]
    [return: M68kRegister(M68kRegister.D0)]
    public static uint Entry([M68kRegister(M68kRegister.A0)] APTR state,
        [M68kRegister(M68kRegister.A1)] APTR scratch)
    {
        var p = new NativeFileSystemPlatform(state);
        var work = NativeAmigaFileSystem.SmartWriteWork(state);
        var request = new AmigaSmartFileSystemWriteCore.Request
        {
            Directory = APTR.ReadUInt32(scratch, Directory), Node = APTR.ReadUInt32(scratch, Node),
            Position = APTR.ReadUInt32(scratch, Position), Length = APTR.ReadUInt32(scratch, Length),
            Name = APTR.FromPointer(APTR.ReadUInt32(scratch, Name)),
            Buffer = APTR.FromPointer(APTR.ReadUInt32(scratch, Buffer)),
        };
        var operation = APTR.ReadUInt32(scratch, Operation);
        var success = operation switch
        {
            RecoverOperation => AmigaSmartFileSystemWriteCore.Recover(ref p, ref work),
            CreateOperation => AmigaSmartFileSystemWriteCore.Create(ref p, ref work, ref request,
                APTR.ReadUInt32(scratch, Argument) != 0),
            TruncateOperation => AmigaSmartFileSystemWriteCore.Truncate(ref p, ref work, request.Node),
            RemoveOperation => AmigaSmartFileSystemWriteCore.Remove(ref p, ref work, request.Node),
            RenameOperation => AmigaSmartFileSystemWriteCore.Rename(ref p, ref work, ref request),
            MetadataOperation => AmigaSmartFileSystemWriteCore.SetMetadata(ref p, ref work, ref request,
                (DosPacketAction)APTR.ReadUInt32(scratch, Argument)),
            WriteOperation => AmigaSmartFileSystemWriteCore.Write(ref p, ref work, ref request, out var written)
                && StoreActual(scratch, written),
            ResizeOperation => AmigaSmartFileSystemWriteCore.Resize(ref p, ref work, request.Node, request.Length),
            RootOperation => AmigaSmartFileSystemCore.Root(ref p, NativeAmigaFileSystem.Metadata(state)),
            MountOperation => AmigaSmartFileSystemCore.Mount(ref p, NativeAmigaFileSystem.Metadata(state)),
            ResolveOperation => ResolveCore(ref p, state, ref work, ref request),
            DescribeOperation => DescribeCore(ref p, state, ref request),
            ReadOperation => ReadCore(ref p, state, ref work, ref request, scratch),
            ExamineOperation => AmigaSmartFileSystemCore.Examine(ref p, request.Node, request.Buffer,
                NativeAmigaFileSystem.Metadata(state)),
            ExamineNextOperation => ExamineNextCore(ref p, state, ref work, ref request),
            CountUsedOperation => CountUsedCore(ref p, state, scratch),
            PacketOperation => PacketCore(state, ref work, ref request, scratch),
            _ => false,
        };
        APTR.WriteUInt32(scratch, Directory, request.Directory);
        APTR.WriteUInt32(scratch, Node, request.Node);
        APTR.WriteUInt32(scratch, Position, request.Position);
        APTR.WriteUInt32(scratch, Length, request.Length);
        APTR.WriteUInt32(scratch, Error, (uint)work.Error);
        return success ? 1u : 0u;
    }

    private static bool StoreActual(APTR scratch, uint actual)
    { APTR.WriteUInt32(scratch, Actual, actual); return true; }

    private static bool ResolveCore(ref NativeFileSystemPlatform p, APTR state,
        ref AmigaSmartFileSystemWriteCore.Work work, ref AmigaSmartFileSystemWriteCore.Request request)
    {
        request.Node = AmigaSmartFileSystemCore.Resolve(ref p, request.Directory, request.Name,
            request.Length, NativeAmigaFileSystem.Metadata(state), out work.Error);
        return request.Node != 0;
    }
    private static bool DescribeCore(ref NativeFileSystemPlatform p, APTR state,
        ref AmigaSmartFileSystemWriteCore.Request request)
    {
        var success = AmigaSmartFileSystemCore.Describe(ref p, request.Node,
            NativeAmigaFileSystem.Metadata(state), out var size, out var directory, out var parent);
        request.Position = size; request.Length = directory ? 1u : 0u; request.Directory = parent;
        return success;
    }
    private static bool ReadCore(ref NativeFileSystemPlatform p, APTR state,
        ref AmigaSmartFileSystemWriteCore.Work work, ref AmigaSmartFileSystemWriteCore.Request request, APTR scratch)
    {
        var actual = AmigaSmartFileSystemCore.Read(ref p, request.Node, request.Position,
            request.Buffer, request.Length, NativeAmigaFileSystem.Metadata(state),
            NativeAmigaFileSystem.Data(state), out work.Error);
        APTR.WriteUInt32(scratch, Actual, unchecked((uint)actual));
        return actual >= 0;
    }
    private static bool ExamineNextCore(ref NativeFileSystemPlatform p, APTR state,
        ref AmigaSmartFileSystemWriteCore.Work work, ref AmigaSmartFileSystemWriteCore.Request request)
    {
        work.Error = AmigaSmartFileSystemCore.ExamineNext(ref p, request.Directory, request.Buffer,
            NativeAmigaFileSystem.Metadata(state), NativeAmigaFileSystem.Data(state));
        return work.Error == DOS.Error.None;
    }
    private static bool CountUsedCore(ref NativeFileSystemPlatform p, APTR state, APTR scratch)
    {
        var success = AmigaSmartFileSystemCore.CountUsed(ref p, NativeAmigaFileSystem.Metadata(state), out var used);
        APTR.WriteUInt32(scratch, Actual, used);
        return success;
    }
    private static bool PacketCore(APTR state, ref AmigaSmartFileSystemWriteCore.Work work,
        ref AmigaSmartFileSystemWriteCore.Request request, APTR scratch)
    {
        var result = NativeAmigaFileSystem.DispatchSmartMutation(state, request.Buffer,
            (DosPacketAction)APTR.ReadUInt32(scratch, Argument), out work.Error);
        APTR.WriteUInt32(scratch, Actual, unchecked((uint)result));
        return work.Error == DOS.Error.None;
    }
}
