# CopperSFS preview.3: installation and upgrade

The `v0.1.0-preview.3` packages contain relocatable Amiga HUNK filesystem
handlers for installation as `L:CopperSFS`. The default handler now uses
**buffered writes**: successful changes are visible immediately, but recent
unflushed changes can be lost after a crash. A strict variant is also included
in the 68020 package. Read the durability section before upgrading.

This remains an **experimental preview** for disposable SFS volumes with
independent backups. Keep a separate boot volume. The handler does not format
disks or install itself in the RDB, and its integration source is not included
in the public repository or binary package.

## Choose the package and handler

| Package | Executable | Intended use | Qualification |
| --- | --- | --- | --- |
| `CopperSFS-v0.1.0-preview.3-68020.zip` | `CopperSFS` | Buffered writes; requires a 68020 or newer CPU | Full Kickstart 3.1 A/B correctness, recovery and performance comparison |
| Same 68020 ZIP | `CopperSFS-strict` | Per-operation durability; requires a 68020 or newer CPU | Strict correctness, recovery and clean handoff; no performance claim |
| `CopperSFS-v0.1.0-preview.3-68000.zip` | `CopperSFS` | Buffered handler compiled for the 68000 instruction set | Mount and shared workload/remount on the 68020 profile only |

**Do not install either 68020 executable on a 68000 or 68010.** The 68000
package does not include a strict variant. Its full 68000 Kickstart boot and
physical 68000 hardware remain unqualified; the previous boot replay stopped
at an emulator assertion before filesystem startup. Do not apply the 68020
performance or exhaustive recovery results to that binary.

`BUILDINFO.txt` identifies the package, CPU, policy and exact handler hashes;
`RELEASE.json` records all three variants. The handlers have no `$VER:` string,
so use these hashes rather than AmigaDOS `Version` to identify this preview.

## Requirements

- AmigaOS 2.04 or newer and a separate boot volume containing the usual DOS
  commands. Native comparison testing used Kickstart 3.1; other ROM versions
  remain unqualified. `DEVS:DOSDrivers` below is for AmigaOS 2.1+; on 2.04,
  use an equivalent entry in `DEVS:MountList`.
- An existing classic `SFS\0` partition and its exact device, unit, cylinder
  range, track geometry, block size, transfer limit and DMA mask.
- A writable block device that honors flush barriers and atomic sector
  writes. A partition extending beyond 4 GiB additionally needs working
  TD64 or NSD 64-bit I/O; native large-device operation remains unqualified.

## Upgrade or change durability policy

1. Finish outstanding work with the currently mounted handler. Close all
   files and stop applications using the volume. Complete a filesystem flush
   or successful clean inhibit before replacing the handler or rebooting.
   See the durability section below; merely closing files is insufficient
   with buffered writes.
2. Preserve an independent backup. If the previous session crashed, recover
   its `CSUN` transaction with CopperSFS on a writable device first. If
   recovery fails, preserve the image and stop writing to it.
3. Boot from a separate volume with the test volume left unmounted. Keep a
   copy of the old handler. Replacing a file in `L:` does not replace a running
   handler; never mount the same partition simultaneously with both versions.
4. Select the executable for your CPU and desired policy, then install and
   mount it using the steps below. Switching to strict mode requires loading
   `CopperSFS-strict`; it is not a mount-file flag.

## Install on a test volume

1. Check the downloaded ZIP against its attached `.zip.sha256` file with a
   SHA-256 tool on your host system. After extraction, verify the files against
   `SHA256SUMS.txt`. These are SHA-256 manifests, not the AmigaDOS `Sum` format.
2. From inside the extracted package directory, choose one command:

   ```text
   C:Copy CopperSFS L:CopperSFS
   ```

   Or, for the strict 68020 variant:

   ```text
   C:Copy CopperSFS-strict L:CopperSFS
   ```

   Only the selected executable is installed under the name `L:CopperSFS`.
3. Copy `SFS0.mount.example` to `DEVS:DOSDrivers/SFS0`, then replace every
   `CHANGE_ME` value with the existing test partition's settings. Check
   `SizeBlock`, `SectorsPerBlock`, `Reserved`, `MaxTransfer` and `Mask` against
   its current mount entry or RDB. `SizeBlock` is in longwords: 512-byte blocks
   normally use 128. Do not use the example unchanged or copy the test-fixture
   geometry onto another partition.
4. Run `C:Mount SFS0:` and `C:List SFS0:`. Copy a small file to the disposable
   volume and read it back. Complete a filesystem flush or clean inhibit,
   then remount or reboot from the separate boot volume and verify the bytes
   again.

`FileSystem = L:CopperSFS` selects the installed executable, and
`DosType = 0x53465300` identifies classic SFS. `Forceload = 1` forces the
file to load even if a matching filesystem is in `FileSystem.resource`.
The handler uses classic 32-bit AmigaDOS file sizes and seek positions.

## Buffered writes, strict writes and recovery

The buffered handler publishes a checkpoint after five seconds from the first
pending change, at buffer pressure, on `ACTION_FLUSH`, or before a successful
clean inhibit. All readers see pending changes. A completed checkpoint makes
them durable; a crash may lose changes since the previous checkpoint.

**Closing a file or calling `dos.library/Flush(file)` is not confirmation of
a filesystem checkpoint.** That call may flush only the DOS stream. Software
requiring confirmed durability must send the filesystem `ACTION_FLUSH` packet
and check success, or successfully inhibit the volume with `Inhibit()` before
unmounting or switching handlers. Use a tool that performs and checks one of
those operations. Waiting five seconds or seeing the activity light go out
does not report whether a device flush succeeded.

The strict executable commits each successful filesystem mutation before
replying. It trades the buffered handler's performance for per-operation
durability. An Open that creates an empty file and a later Write are separate
operations; a crash between them may leave the committed empty file. Neither
variant makes a multi-operation application update automatically atomic.

Both variants retain the same `CSUN` undo journal and four ordered durability
barriers. After an interrupted CopperSFS write, recover with CopperSFS before
using original SFS or another implementation. Do not give another handler an
unresolved `CSUN` transaction. Recovery requires an intact journal and honored
flush barriers. See [compatibility and recovery](compatibility.md) and
[transaction batching](transaction-batching.md).

At 512-byte blocks, the physical queue uses 69,408 bytes per mounted volume,
down from 133,920 with the same 128-block capacity. Buffered mode additionally
uses a 50,256-byte logical write buffer; caches and other state need more RAM.
If buffer or timer allocation fails, the handler falls back to strict writes.

## What was validated

The packaged 68020 binaries are byte-for-byte the binaries in the 2026-10-22
qualification. Both handlers used the same Kickstart 3.1 / 68020 configuration
and fresh clones of an independently formatted 16 MiB image. The comparison
included original SmartFilesystem 1.279, concurrent writes and reads, positions
and sizes, metadata, clean remounts, clean handoff in both directions, original
`TRST` replay, and independent amidisk extraction and deep checks.

All 329 interruption/reboot cases passed: 98 buffered CopperSFS, 107 strict
CopperSFS, and 62 original-handler cases repeated for each mode. Cuts cover
every relevant sector write and flush, including sectors inside combined
requests. The model assumes atomic 512-byte sectors, honored flush barriers
and loss of unflushed writes. Torn sectors and physical devices remain
unqualified.

Across five trials, buffered 68020 write CPU medians were 5.9% below original
SFS for sequential 1 MiB writes, 2.5% below for small-file creation, and 1.5%
below for mixed access. Measured request totals matched or beat the original;
all sector/request/flush counts matched the preceding CopperSFS build. These
are emulator CPU/I/O results, not physical-device throughput. See
[the complete validation record](validation.md) for hashes, counts, settings,
host tests, compiled instruction tests and coverage limits.

When reporting a problem, include the package and handler hash, CPU, OS,
device, partition geometry and reproducible steps with a disposable image.
Do not upload licensed ROMs or private data.
