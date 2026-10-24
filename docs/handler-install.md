# CopperSFS 1.0 installation and runtime settings

The experimental 1.0 package contains one **68020+ handler** and
`CopperSFSCtrl`. The handler defaults to buffered writes and a 256 KiB
clean-data cache. Use disposable existing SFS volumes, independent backups
and a separate boot volume. Native testing used Kickstart 3.1; physical
devices and other ROMs remain unqualified.

## Install or upgrade

Follow the complete [package installation instructions](../packaging/aminet-1.0/INSTALL.txt).
Verify the archive and extracted SHA-256 manifests. Finish work with the
old handler and complete a checked filesystem flush or clean inhibit before
replacing it. Recover an interrupted `CSUN` transaction with CopperSFS first.

Boot from another volume with the test volume unmounted, then install:

```text
Copy CopperSFS L:CopperSFS
Copy CopperSFSCtrl C:CopperSFSCtrl
```

Copy the supplied mount example to `DEVS:DOSDrivers/SFS0` on OS 2.1+, or
create an equivalent MountList entry on OS 2.04. Replace every `CHANGE_ME`
with the existing partition's exact device, unit, geometry and transfer
settings. Use `FileSystem = L:CopperSFS`, `DosType = 0x53465300` and
`Forceload = 1`. Do not copy test geometry. Mount, write/read a small test
file, complete a checked flush, reboot and verify its bytes again.

## Configure a mounted volume

```text
CopperSFSCtrl SFS0: INFO
CopperSFSCtrl SFS0: DATACACHE 128K
CopperSFSCtrl SFS0: DATACACHE 512K
CopperSFSCtrl SFS0: DATACACHE OFF
CopperSFSCtrl SFS0: WRITEMODE STRICT
CopperSFSCtrl SFS0: WRITEMODE BUFFERED
CopperSFSCtrl SFS0: FLUSH
```

Choose one operation per command; no operation means INFO. Exit status is
0 on success and 20 on failure. Options apply to that mounted volume only.
To restore preferences at boot, put the desired commands in `S:User-Startup`
after mounting and before applications use the volume. Check failures.

INFO reports requested and actual cache capacity, pool allocation, metadata
buffer count, write-buffer and phase-queue allocations, write mode, pending
writes and pending recovery. It does not report total process memory.
`Version L:CopperSFS FILE FULL` identifies the binary and startup defaults;
INFO shows current settings.

Cache replacement preserves pending writes and open file positions. It
requires the exact requested allocation; failure preserves the old pool.
Growth temporarily needs both pools. INFO and resizing do not issue disk
I/O once the volume is mounted. Resolving an unmounted target can mount it.
`ADDBUFFERS` continues to control the separate metadata cache.

## Memory and admission

Choose OFF/0 or a power-of-two page count from 4 through 256, with sixteen
filesystem blocks per page. For 512-byte blocks, accepted capacities are
32K, 64K, 128K, 256K, 512K, 1M and 2M. Bare bytes and K/M suffixes work.

| Data capacity, 512-byte blocks | Pool bytes |
| --- | ---: |
| Disabled | 0 |
| 128 KiB | 131,360 |
| 256 KiB | 262,688 |
| 512 KiB | 525,344 |
| 2 MiB | 2,101,280 |

The default loaded-and-mounted handler measured **585,480 bytes** in the
shared test profile, including code, process, stack and mount buffers.
Each volume owns its allocations; open files need additional memory.
Buffered mode uses a 50,256-byte logical write buffer and a 69,408-byte
physical phase queue at 512-byte blocks. Mount-time allocation failure
halves the default clean pool until one fits, eventually running uncached.
Explicit runtime changes instead preserve the old pool on failure.

An initial sequential pass does not populate this cache. A reread or seek
on the same handle enables admission, and a later pass can hit. Reopening
alone does not seed an empty pool. Reads of at least 8 KiB from files larger
than the actual pool bypass it. Writes, recovery and handoff invalidate
clean data. See [cache integration](data-cache.md).

## Durability and compatibility

Mode changes checkpoint before publishing the new mode. Failed changes
retain the old mode; enabling buffering also requires a write buffer and
timer. Selecting the current mode is a no-op. Use FLUSH for a checked
filesystem checkpoint. Merely closing files, waiting or calling
`dos.library/Flush(file)` does not confirm that checkpoint succeeded.

Buffered changes are immediately visible to readers. Checkpoints occur
five seconds after the first pending change, at buffer pressure, on
successful ACTION_FLUSH or before successful clean inhibit. Recent
unflushed changes may be lost after a crash. Strict mode commits each
successful mutation before replying. Neither mode makes several application
operations atomic. Both retain the same journal and ordered barriers.

The handler uses classic 32-bit DOS file sizes and seek positions. Device
access beyond 4 GiB requires TD64. NSD is not supported; native large-device
use remains unqualified. Read the [qualification boundary](../packaging/aminet-1.0/VALIDATION.txt),
[recovery contract](compatibility.md) and [control protocol](control-protocol.md).
