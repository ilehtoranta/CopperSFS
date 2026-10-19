using Amiga;
using CopperStart.Exec;

namespace CopperStart.Dos;

/// <summary>Standard AmigaDOS ACTION_STARTUP entry for an L: filesystem segment.</summary>
public static class NativeSfsStandaloneHandler
{
    public static uint Entry()
    {
        // DOS LVOs use CopperSharp's writable _DOSLibraryBase slot in a HUNK.
        // The filesystem is started by DOS, but a new segment does not inherit
        // the caller's library-base register or the compiler's private slot.
        var dosBase = global::Amiga.Exec.OpenLibraryRaw(
            CString.FromPointer(CString.ToUInt32("dos.library")), 37);
        if (dosBase.IsNull) return 20;
        global::Amiga.DOS.DOSLibraryBase = dosBase;
        var task = global::Amiga.Exec.FindTask(CString.FromPointer(0));
        var port = DosProcessCodec.MessagePortAddress(task);
        global::Amiga.Exec.WaitPort(port.Raw);
        var message = APTR.FromPointer(global::Amiga.Exec.GetMsg(port.Raw));
        if (message.IsNull)
        { global::Amiga.Exec.CloseLibrary(dosBase); return 20; }
        var packet = APTR.FromPointer(APTR.ReadUInt32(message, ExecLayout.Node.Name));
        if (packet.IsNull)
        { global::Amiga.Exec.CloseLibrary(dosBase); return 20; }
        var startup = APTR.FromPointer(APTR.ReadUInt32(packet, 24) << 2); // dp_Arg2, FileSysStartupMsg BPTR
        var state = startup.IsNull ? APTR.Null : NativeAmigaFileSystem.CreateStartup(startup);
        if (state.IsNull)
        { Reply(message, packet, port, 0, DOS.Error.DiskNotValidated); global::Amiga.Exec.CloseLibrary(dosBase); return 20; }
        if (APTR.ReadUInt32(state, NativeAmigaFileSystem.DosTypeOffset) != AmigaSmartFileSystemCore.DosType)
        {
            NativeAmigaFileSystem.Destroy(state);
            Reply(message, packet, port, 0, DOS.Error.ObjectWrongType);
            global::Amiga.Exec.CloseLibrary(dosBase);
            return 20;
        }

        APTR.WriteUInt32(state, NativeAmigaFileSystem.PortOffset, port.Raw);
        var name = APTR.FromPointer(global::Amiga.Exec.AllocMem(108, global::Amiga.Exec.MemoryFlags.Public));
        if (name.IsNull)
        {
            NativeAmigaFileSystem.Destroy(state);
            Reply(message, packet, port, 0, DOS.Error.NoFreeStore);
            global::Amiga.Exec.CloseLibrary(dosBase);
            return 20;
        }
        NativeAmigaFileSystem.CopyVolumeName(state, name);
        var volume = global::Amiga.DOS.MakeDosEntry(CString.FromPointer(name.Raw), (int)DosListType.Volume);
        global::Amiga.Exec.FreeMem(name.Raw, 108);
        if (volume.IsNull)
        {
            NativeAmigaFileSystem.Destroy(state);
            Reply(message, packet, port, 0, DOS.Error.NoFreeStore);
            global::Amiga.Exec.CloseLibrary(dosBase);
            return 20;
        }
        APTR.WriteUInt32(volume, 8, port.Raw); // dol_Task
        NativeAmigaFileSystem.CopyVolumeDate(state, volume);
        APTR.WriteUInt32(volume, 32, APTR.ReadUInt32(state, NativeAmigaFileSystem.DosTypeOffset));
        APTR.WriteUInt32(state, NativeAmigaFileSystem.VolumeOffset, volume.Raw);
        var device = APTR.FromPointer(APTR.ReadUInt32(packet, 28) << 2); // dp_Arg3, DosList BPTR
        if (device.IsNotNull) APTR.WriteUInt32(device, 8, port.Raw);
        Reply(message, packet, port, -1, DOS.Error.None);
        // GetDeviceProc holds the DOS list lock until ACTION_STARTUP replies.
        if (global::Amiga.DOS.AddDosEntry(volume) == 0)
        {
            APTR.WriteUInt32(state, NativeAmigaFileSystem.VolumeOffset, 0);
            global::Amiga.DOS.FreeDosEntry(volume);
        }
        var result = NativeAmigaFileSystem.Run(state);
        global::Amiga.Exec.CloseLibrary(dosBase);
        return result;
    }

    private static void Reply(APTR message, APTR packet, APTR port, int result, DOS.Error error)
    {
        APTR.WriteUInt32(packet, 12, unchecked((uint)result));
        APTR.WriteUInt32(packet, 16, (uint)error);
        APTR.WriteUInt32(packet, 4, port.Raw);
        global::Amiga.Exec.ReplyMsg(message.Raw);
    }
}
