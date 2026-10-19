# Experimental AmigaOS handler

The `CopperSFS` prerelease package contains a compiled MC68000 Amiga HUNK for
installation as `L:CopperSFS`. Handler source is not included in this repository.
This is an **experimental preview**: use a disposable SFS volume, keep an
independent backup, and do not make it your only boot or data volume. It does
not format disks or install itself in the RDB.

## Requirements

- AmigaOS 2.04 or newer and a separate boot volume containing the usual DOS
  commands. The `DEVS:DOSDrivers` mount-file layout below is for AmigaOS 2.1+;
  on 2.04, use an equivalent entry in `DEVS:MountList`.
- An existing classic `SFS\0` partition and its **exact** device, unit,
  cylinder range, track geometry, block size, transfer limit and DMA mask.
- A writable block device that honors completed writes and flushes. A
  partition extending beyond 4 GiB needs working TD64 or NSD 64-bit I/O.

## Install on a test volume

1. Check the downloaded ZIP against the attached `.zip.sha256` file. After
   extraction, `SHA256SUMS.txt` lists the checksums of the files inside. Copy
   the `CopperSFS` executable to `L:CopperSFS` on your boot volume.
2. Copy `SFS0.mount.example` to `DEVS:DOSDrivers/SFS0`, then replace every
   `CHANGE_ME` field with the values of the **existing test partition**. Check
   `SizeBlock`, `SectorsPerBlock`, `Reserved`, `MaxTransfer` and `Mask` against
   its current mount entry or RDB. Do not use the example unchanged.
3. Run `C:Mount SFS0:` and try `C:List SFS0:`. On a disposable image, test a
   small `C:Copy` to the volume, read it back, then unmount or reboot and
   verify the bytes again. Keep the boot volume separate from SFS0:.

`FileSystem = L:CopperSFS` chooses the handler. `DosType = 0x53465300`
identifies classic SFS. `Forceload = 1` forces the file to load even if a
matching filesystem is already present in `FileSystem.resource`. The
[AmigaOS DOS reference](https://developer.amigaos3.net/sites/default/files/downloads/2024-10/Amiga_ROM_Kernel_Reference_Manual_DOS.pdf)
describes these mount keywords and `FileSysStartupMsg`; the
[AmigaDOS directory guide](https://wiki.amigaos.net/wiki/AmigaOS_Manual:_AmigaDOS_Additional_Amiga_Directories)
explains `DEVS:DOSDrivers` and `DEVS:MountList`.

The handler uses classic 32-bit AmigaDOS file sizes and seek positions. If a
write is interrupted, mount the volume with CopperSFS on a writable device
to let its `CSUN` undo journal recover before trying another SFS handler.
Recovery needs an intact journal and working flush barriers. If recovery
fails, preserve the image and stop writing to it.

Native Kickstart 3.1 tests in an A500 emulator exercised startup, reads,
writes, fresh-boot remount, two concurrent writer tasks with a reader, and
recovery from one captured interrupted transaction. These tests used a
synthetic floppy image and the CopperSFS core for host-side consistency checks.
Physical Amigas, other ROMs and devices, independently formatted media and
additional power-cut points still need qualification. Please report the OS
version, device, partition geometry and exact steps with a reproducible
disposable image; do not upload licensed ROMs or private data.
