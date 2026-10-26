using Amiga;
using CopperSharp.Compiler;

namespace CopperSFS.Tools;

/// <summary>Explicit DOS packet client; never accesses the raw device itself.</summary>
public static class TrimCommand
{
    [M68kEntryPoint]
    public static int Main(int length, CONST_STRPTR text)
    {
        var library = Exec.OpenLibraryRaw("dos.library", 37);
        if (library.IsNull) return DOS.RETURN_FAIL;
        DOS.DOSLibraryBase = library;
        var memory = Exec.AllocMem(64, Exec.MemoryFlags.Public | Exec.MemoryFlags.Clear);
        if (memory == 0) { Exec.CloseLibrary(library); DOS.DOSLibraryBase = APTR.Null; return DOS.RETURN_FAIL; }
        var values = APTR.FromPointer(memory);
        var args = DOS.AllocDosObject((uint)DosObjectType.RdArgs, APTR.Null);
        var result = DOS.RETURN_ERROR;
        if (args.IsNotNull)
        {
            APTR.WriteUInt32(args, DosLayout.CSource.Buffer, text.Raw);
            APTR.WriteUInt32(args, DosLayout.CSource.Length, (uint)length);
            APTR.WriteUInt32(args, DosLayout.RDArgs.Flags, (uint)DosRdArgsFlags.NoPrompt);
            var parsed = DOS.ReadArgs("VOLUME/A,CHECK/S,DRYRUN/S", values, args);
            if (parsed.IsNotNull)
            {
                result = Run(values);
                DOS.FreeArgs(parsed);
            }
            else DOS.PutStr("Usage: CopperSFSTrim VOLUME: [CHECK | DRYRUN]\n");
            DOS.FreeDosObject((uint)DosObjectType.RdArgs, args);
        }
        Exec.FreeMem(memory, 64); Exec.CloseLibrary(library); DOS.DOSLibraryBase = APTR.Null;
        return result;
    }

    private static int Run(APTR values)
    {
        var check = APTR.ReadUInt32(values, 4) != 0;
        var dry = APTR.ReadUInt32(values, 8) != 0;
        if (check && dry) { DOS.PutStr("Choose CHECK or DRYRUN.\n"); return DOS.RETURN_ERROR; }
        var name = APTR.FromPointer(APTR.ReadUInt32(values, 0));
        var at = 0;
        while (APTR.ReadUInt8(name, at) != 0 && APTR.ReadUInt8(name, at) != (byte)':') at++;
        if (at == 0 || APTR.ReadUInt8(name, at) != (byte)':' || APTR.ReadUInt8(name, at + 1) != 0)
        { DOS.PutStr("Supply a volume or device name ending in ':', without a path.\n"); return DOS.RETURN_ERROR; }
        var proc = DOS.GetDeviceProc(CString.FromPointer(name.Raw), APTR.Null);
        if (proc.IsNull) { DOS.PrintFault(DOS.IoErr(), "CopperSFSTrim"); return DOS.RETURN_FAIL; }
        var record = APTR.FromPointer(values.Raw + 16);
        APTR.WriteUInt32(record, 0, AmigaSmartFileSystemTrimPacket.Size);
        APTR.WriteUInt32(record, 4, AmigaSmartFileSystemTrimPacket.Version);
        APTR.WriteUInt32(record, 8, check ? AmigaSmartFileSystemTrimPacket.QueryOnly : dry ? AmigaSmartFileSystemTrimPacket.DryRun : 0);
        var port = APTR.FromPointer(APTR.ReadUInt32(proc, DosLayout.DevProc.Port));
        var success = DOS.DoPkt(port, AmigaSmartFileSystemTrimPacket.Action, unchecked((int)record.Raw), 0, 0, 0, 0) != 0;
        var error = DOS.IoErr();
        DOS.FreeDeviceProc(proc);
        if (!success)
        {
            if (error == DOS.Error.ActionNotKnown)
                DOS.PutStr("Manual discard is unsupported by this handler or device.\n");
            else DOS.PrintFault(error, "CopperSFSTrim");
            return DOS.RETURN_FAIL;
        }
        if (check) DOS.PutStr("Manual SCSI UNMAP is supported. Automatic trimming is disabled.\n");
        else
        {
            if (dry) DOS.PutStr("Checkpoint completed; proposed discard only (DRYRUN).\n");
            else DOS.PutStr("Manual discard completed.\n");
            DOS.VPrintf("%lu filesystem blocks in %lu ranges.\n", APTR.FromPointer(record.Raw + 12));
        }
        return DOS.RETURN_OK;
    }
}
