# CopperSFS preview.4: installation and upgrade

The `v0.1.0-preview.4` packages contain relocatable Amiga HUNK filesystem
handlers for installation as `L:CopperSFS`. The default 68020 handler uses
**buffered writes** and a 256 KiB clean-data cache. Successful changes are
visible immediately, but recent unflushed changes can be lost after a crash.
The 68020 package also includes fixed cache-size choices and a strict-write
variant. Read the durability section before upgrading.

This remains an **experimental preview** for disposable SFS volumes with
independent backups. Keep a separate boot volume. The handler does not format
disks or install itself in the RDB, and its integration source is not included
in the public repository or binary package.

## Choose the package and handler

| Package | Executable | Intended use | Qualification |
| --- | --- | --- | --- |
| `CopperSFS-v0.1.0-preview.4-68020.zip` | `CopperSFS` | Buffered writes; 256 KiB cache; 68020 or newer | Full Kickstart 3.1 A/B correctness, recovery and performance comparison |
| Same 68020 ZIP | `CopperSFS-strict` | Per-operation durability; 256 KiB cache | Strict correctness, recovery and clean handoff; no performance claim |
| Same 68020 ZIP | `CopperSFS-cache0`, `CopperSFS-cache128`, `CopperSFS-cache512`, `CopperSFS-cache2048` | Buffered writes; named cache budgets in KiB | Memory and coherence tests; no full A/B or performance claim for these builds |
| `CopperSFS-v0.1.0-preview.4-68000.zip` | `CopperSFS` | Buffered handler with 256 KiB cache, compiled for the 68000 instruction set | Five mount/shared-workload smoke cases on the 68020 emulator profile only |

**Do not install any 68020 executable on a 68000 or 68010.** The 68000
package does not include a strict variant. Its full 68000 Kickstart boot and
physical 68000 hardware remain unqualified; the previous boot replay stopped
at an emulator assertion before filesystem startup. Do not apply the 68020
performance or exhaustive recovery results to that binary.

`BUILDINFO.txt` identifies the package, CPU, policy and exact handler hashes;
`RELEASE.json` records all seven variants. The handlers have no `$VER:` string,
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
4. Select the executable for your CPU, cache budget and durability policy,
   then install and mount it using the steps below. The cache budget and strict
   mode are fixed in each executable; neither is a mount-file flag.

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

   Or, for a 128 KiB clean-data cache with buffered writes on a 68020:

   ```text
   C:Copy CopperSFS-cache128 L:CopperSFS
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

## Choose a clean-data cache budget

The cache keeps clean file data in Fast RAM. These sizes are compiled into the
executables; there is no runtime cache-size mount option. `ADDBUFFERS` controls
the existing block cache and does not change this pool. Switching cache sizes
requires a successful filesystem flush or clean inhibit, unmounting the volume,
replacing `L:CopperSFS`, and remounting from a separate boot volume.

The pool includes bookkeeping in addition to the advertised data capacity.
The last column is the measured reduction in available Fast RAM from before
loading through a mounted read-and-close on the shared 68020 test profile. It
includes the handler, process, stack, buffers and other mount allocations; it
is not just the data cache or a peak-open-files measurement.

| 68020 executable | Requested data capacity | Pool bytes at 512-byte blocks | Measured loaded + mounted RAM |
| --- | ---: | ---: | ---: |
| `CopperSFS-cache0` | 0 | 0 | 321,440 bytes |
| `CopperSFS-cache128` | 128 KiB | 131,360 | 452,808 bytes |
| `CopperSFS` | 256 KiB | 262,688 | 584,136 bytes |
| `CopperSFS-cache512` | 512 KiB | 525,344 | 846,792 bytes |
| `CopperSFS-cache2048` | 2 MiB | 2,101,280 requested; 1,050,656 obtained in test | 1,372,104 bytes when it fell back to 1 MiB |
| `CopperSFS-strict` | 256 KiB | 262,688 | 533,632 bytes |

The default pool's 262,688 bytes comprise 262,144 bytes of file data and 544
bytes of bookkeeping. If the requested contiguous Fast RAM allocation fails,
the handler halves its page count until a smaller pool fits; if none fits, it
continues without the clean-data cache. On the tested machine, the 2 MiB
variant obtained a 1 MiB pool. That fallback may differ with available RAM.
The separate 68000 package also requests 256 KiB, but the 68020 memory
measurements above do not qualify its complete 68000-machine footprint.

The first sequential pass does not populate the cache. A reread or seek on the
same open handle enables admission; the next pass fills the pool, and a later
pass can hit it. Reopening a file does not by itself seed an empty pool. Reads
of at least 8 KiB from files larger than the actual pool bypass it. Cached
data is invalidated around writes, recovery and volume handoff. The cache adds
no device writes or flushes and does not change buffered or strict durability.
See [cache behavior and integration](data-cache.md).

## What was validated

The packaged default buffered and strict 68020 binaries are byte-for-byte the
binaries in the 2026-10-23 cache qualification. Both handlers used the same
Kickstart 3.1 / 68020 configuration and fresh clones of an independently
formatted 16 MiB image. The comparison included original SmartFilesystem
1.279, concurrent writes and reads, positions and sizes, metadata, clean
remounts, clean handoff in both directions, original `TRST` replay, and
independent amidisk extraction and deep checks. The alternate cache-size
binaries passed memory and cache-coherence workloads; they were not run through
the full A/B and performance suites.

All 329 interruption/reboot cases passed: 98 buffered CopperSFS, 107 strict
CopperSFS, and 62 original-handler cases repeated for each mode. Cuts cover
every relevant sector write and flush, including sectors inside combined
requests. The model assumes atomic 512-byte sectors, honored flush barriers
and loss of unflushed writes. Torn sectors and physical devices remain
unqualified.

Across five trials, default buffered 68020 CPU medians were 19.8% below
original SFS for sequential 1 MiB reads, 6.1% below for sequential 1 MiB
writes, 10.0% below for small-file creation, 20.5% below for directory
enumeration and 1.0% below for mixed access. All six shared workloads retained
the preceding CopperSFS build's sector and device-request counts. A separate
64 KiB fully warm read used zero device requests versus 16 for the preceding
CopperSFS build and eight for original SFS. Its CPU cycles were 12.8% below
original but 98.2% above the preceding emulated direct-I/O path because copying
cached data costs guest CPU. Cold 4 KiB read requests were unchanged versus the
preceding build (16), while original used eight. These are emulator CPU/I/O
results, not physical-device throughput. The strict and alternate cache-size
binaries have no performance claim. See [the complete validation record](validation.md)
for exact hashes, counts, settings and coverage limits.

When reporting a problem, include the package and handler hash, CPU, OS,
device, partition geometry and reproducible steps with a disposable image.
Do not upload licensed ROMs or private data.
