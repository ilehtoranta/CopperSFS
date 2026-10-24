using Amiga;

namespace CopperSFS.Tools;

/// <summary>Native AmigaDOS CLI. All allocations are through Exec, never the CLR.</summary>
public static class CopperSFSCtrl
{
    public static int Entry()
    {
        var dos = global::Amiga.Exec.OpenLibraryRaw(CString.FromLiteral("dos.library"), 37);
        if (dos.IsNull) return 20;
        DOS.DOSLibraryBase = dos;
        var memory = APTR.FromPointer(global::Amiga.Exec.AllocMem(256,
            global::Amiga.Exec.MemoryFlags.Public | global::Amiga.Exec.MemoryFlags.Clear));
        if (memory.IsNull) { global::Amiga.Exec.CloseLibrary(dos); return 20; }
        var args = DOS.ReadArgs(CString.FromLiteral("VOLUME/A,INFO/S,DATACACHE/K,WRITEMODE/K,FLUSH/S"), memory, APTR.Null);
        var result = 20;
        if (args.IsNull) DOS.PrintFault(DOS.IoErr(), CString.FromLiteral("CopperSFSCtrl"));
        else
        {
            result = Run(memory);
            DOS.FreeArgs(args);
        }
        global::Amiga.Exec.FreeMem(memory.Raw, 256);
        global::Amiga.Exec.CloseLibrary(dos);
        return result;
    }

    private static int Run(APTR memory)
    {
        var selections = 0;
        for (var offset = 4; offset <= 16; offset += 4) if (APTR.ReadUInt32(memory, offset) != 0) selections++;
        var operation = AmigaSfsControl.Info;
        var value = 0u;
        if (selections > 1) return Usage();
        var cache = APTR.FromPointer(APTR.ReadUInt32(memory, 8));
        var mode = APTR.FromPointer(APTR.ReadUInt32(memory, 12));
        if (cache.IsNotNull)
        {
            operation = AmigaSfsControl.SetDataCache;
            if (!Capacity(cache, out value)) return Usage();
        }
        if (mode.IsNotNull)
        {
            operation = AmigaSfsControl.SetWriteMode;
            if (Equal(mode, CString.FromLiteral("STRICT"))) value = AmigaSfsControl.Strict;
            else if (Equal(mode, CString.FromLiteral("BUFFERED"))) value = AmigaSfsControl.Buffered;
            else return Usage();
        }
        var target = DOS.GetDeviceProc(CString.FromPointer(APTR.ReadUInt32(memory, 0)), APTR.Null);
        if (target.IsNull) { DOS.PrintFault(DOS.IoErr(), CString.FromLiteral("CopperSFSCtrl")); return 20; }
        var port = APTR.FromPointer(APTR.ReadUInt32(target, DosLayout.DevProc.Port));
        var info = APTR.FromPointer(memory.Raw + 64);
        APTR.WriteUInt32(info, AmigaSfsControl.Field.Version, AmigaSfsControl.Version);
        var okay = Request(port, info, AmigaSfsControl.Info, 0) &&
            APTR.ReadUInt32(info, AmigaSfsControl.Field.Identity) == AmigaSfsControl.Magic;
        if (!okay)
        {
            DOS.PutStr(CString.FromLiteral("CopperSFSCtrl: target is not a compatible CopperSFS handler.\n"));
            DOS.FreeDeviceProc(target); return 20;
        }
        if (APTR.ReadUInt32(memory, 16) != 0)
        {
            okay = DOS.DoPkt0(port, (int)DosPacketAction.Flush) != 0;
            if (okay) okay = Request(port, info, AmigaSfsControl.Info, 0);
        }
        else if (operation != AmigaSfsControl.Info) okay = Request(port, info, operation, value);
        if (okay) Show(info, APTR.FromPointer(memory.Raw + 128));
        else DOS.PrintFault(DOS.IoErr(), CString.FromLiteral("CopperSFSCtrl"));
        DOS.FreeDeviceProc(target);
        return okay ? 0 : 20;
    }

    private static bool Request(APTR port, APTR info, uint operation, uint value)
    {
        APTR.WriteUInt32(info, AmigaSfsControl.Field.Operation, operation);
        APTR.WriteUInt32(info, AmigaSfsControl.Field.Value, value);
        return DOS.DoPkt3(port, AmigaSfsControl.PacketAction, (int)AmigaSfsControl.Magic,
            (int)info.Raw, (int)AmigaSfsControl.Bytes) != 0;
    }

    private static int Usage()
    {
        DOS.PutStr(CString.FromLiteral("Choose INFO, DATACACHE <bytes|K|M|OFF>, WRITEMODE <STRICT|BUFFERED>, or FLUSH.\n"));
        return 20;
    }

    private static bool Equal(APTR input, CString literal)
    {
        var text = APTR.FromPointer(CString.ToUInt32(literal));
        for (var i = 0; ; i++)
        {
            var a = APTR.ReadUInt8(input, i); var b = APTR.ReadUInt8(text, i);
            if (a >= 97 && a <= 122) a -= 32;
            if (a != b) return false;
            if (a == 0) return true;
        }
    }

    private static bool Capacity(APTR text, out uint value)
    {
        value = 0;
        if (Equal(text, CString.FromLiteral("OFF"))) return true;
        var index = 0; var digit = APTR.ReadUInt8(text, index);
        if (digit < 48 || digit > 57) return false;
        while (digit >= 48 && digit <= 57)
        {
            if (value > (uint.MaxValue - (uint)(digit - 48)) / 10) return false;
            value = value * 10 + (uint)(digit - 48); digit = APTR.ReadUInt8(text, ++index);
        }
        if (digit == 0) return true;
        var multiplier = digit == 75 || digit == 107 ? 1024u : digit == 77 || digit == 109 ? 1048576u : 0;
        if (multiplier == 0 || APTR.ReadUInt8(text, index + 1) != 0 || value > uint.MaxValue / multiplier) return false;
        value *= multiplier; return true;
    }

    private static void Number(CString label, uint value, APTR buffer)
    {
        DOS.PutStr(label);
        var at = 15;
        do { APTR.WriteUInt8(buffer, --at, (byte)(48 + value % 10)); value /= 10; } while (value != 0);
        DOS.WriteChars(APTR.FromPointer(buffer.Raw + (uint)at), (uint)(15 - at));
        DOS.PutStr(CString.FromLiteral("\n"));
    }

    private static void Show(APTR info, APTR buffer)
    {
        DOS.PutStr(CString.FromLiteral("CopperSFS configuration (bytes):\n"));
        Number(CString.FromLiteral("Requested data cache: "), APTR.ReadUInt32(info, AmigaSfsControl.Field.RequestedDataBytes), buffer);
        Number(CString.FromLiteral("Actual data cache: "), APTR.ReadUInt32(info, AmigaSfsControl.Field.ActualDataBytes), buffer);
        Number(CString.FromLiteral("Data cache allocation: "), APTR.ReadUInt32(info, AmigaSfsControl.Field.PoolBytes), buffer);
        Number(CString.FromLiteral("Metadata buffers: "), APTR.ReadUInt32(info, AmigaSfsControl.Field.MetadataBuffers), buffer);
        Number(CString.FromLiteral("Write buffer allocation: "), APTR.ReadUInt32(info, AmigaSfsControl.Field.WriteBufferBytes), buffer);
        Number(CString.FromLiteral("Physical queue allocation: "), APTR.ReadUInt32(info, AmigaSfsControl.Field.PhysicalQueueBytes), buffer);
        DOS.PutStr(APTR.ReadUInt32(info, AmigaSfsControl.Field.WriteMode) == AmigaSfsControl.Strict ?
            CString.FromLiteral("Write mode: strict\n") : CString.FromLiteral("Write mode: buffered\n"));
        Number(CString.FromLiteral("Pending writes: "), APTR.ReadUInt32(info, AmigaSfsControl.Field.Pending), buffer);
        Number(CString.FromLiteral("Recovery pending: "), APTR.ReadUInt32(info, AmigaSfsControl.Field.RecoveryPending), buffer);
    }
}
