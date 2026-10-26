# Manual TRIM (development source)

Manual discard is explicitly requested. There is no mount-time probe, background
trimmer, delete-time discard, new normal-write barrier or permanent trim queue.
This feature is **not present in preview.3**. Use a handler and command built from
matching newer sources. No package release is implied by these source changes.

## Command

Install the compiled 68000 HUNK `CopperSFSTrim` in `C:` and the matching handler
in `L:`. The command requires dos.library V37 or newer.

```text
CopperSFSTrim SFS0: CHECK
CopperSFSTrim SFS0: DRYRUN
CopperSFSTrim SFS0:
```

`CHECK` itself queries support without checkpointing or discarding. Normal
mounting or pre-existing handler error recovery may still perform I/O before
the command is served. `DRYRUN` does
checkpoint pending changes and checks the complete free bitmap, then reports
proposed blocks/ranges without discarding. Without a switch the command performs
the checkpoint, validation and actual discard. Supply a mounted volume/device
name ending in `:`, with no file path. `CHECK` and `DRYRUN` are mutually exclusive.
Reported blocks are filesystem blocks, not bytes or device sectors. A failed
command may have discarded part of its last range; completed-range counters
exclude that partial command. Retry is safe: each invocation scans again.

Only SCSI UNMAP through the Amiga device's `HD_SCSICMD` is implemented. Discovery
requires VPD B2 LBPU support, nonzero VPD B0 UNMAP limits and READ CAPACITY(16).
Unknown/unsupported devices are rejected without a checkpoint or discard.
There is no speculative ATA pass-through and no inference from a drive model,
SSD label or standard INQUIRY alone. The driver/controller/bridge must actually
support and correctly implement these commands. Real hardware remains unqualified.
An ordinary image-file backend without an explicit discard provider reports
unsupported; this command alone does not make a hardfile release host SSD space.

## Building the command

The command source is `tools/CopperSFS.Trim`. Build its managed inputs with
`dotnet build tools/CopperSFS.Trim/CopperSFS.Trim.csproj -c Release -o artifacts/trim-inputs`.
With a compatible CopperSharp68k compiler, select entry
`CopperSFS.Tools.TrimCommand::Main`, platform `amiga`, CPU `68000`, HUNK output,
freestanding runtime, memory `none`, exceptions `yolo`, and no exports or symbols.
Supply the built core, Amiga SDK, SDK support and compiler assemblies as managed
inputs. Name the resulting HUNK `CopperSFSTrim`. A successful .NET build alone
does not produce a native Amiga executable. Qualify a newly built handler and
command together before use.

## Ordering and recovery

The handler owns the mount throughout the synchronous operation. Other DOS
packets, readers, writers, allocations, timer commits and trims wait until it
returns. Large trims can therefore stall filesystem access. The temporary native
command buffer is 256 bytes and obeys the mounted device's memory mask; there
is no additional per-mount buffer. Coalesced free ranges are sent one per UNMAP,
split to the advertised maximum and at most 4096 filesystem blocks where
physical-sector granularity allows. These are explicit maintenance I/O requests.

Before the first discard the core recovers any physical transaction, commits
acknowledged buffered changes, prepares clean roots and completes a final device
flush. Every bitmap is checksum/identity checked before any range is sent, and
known permanent metadata must be allocated. Only then are free runs scanned and
aligned inward to physical-sector boundaries. Reserved blocks, partition ends,
roots, bitmap blocks and allocated blocks are excluded. No range list survives
the call or can outlive the mount's ownership.

This uses the same undo recovery and durability barriers in buffered and strict
mode. A cut during checkpointing recovers according to that mode's existing
old-or-new contract. Once discard starts the checkpoint is already durable;
partial or failed discard can affect only free space. Reads of discarded space
are never assumed to return zero. Reallocation and subsequent writes occur only
after the outstanding discard has completed. A failed checkpoint prevents discard.
CopperSFS must replay unresolved `CSUN` transactions before another handler is
used; manual TRIM does not change that requirement.

The bitmap is authoritative allocation metadata, as it is for the allocator.
Checksums and known-root checks are not a complete filesystem consistency checker
and cannot detect every pre-existing bitmap/reference inconsistency.

## Integrating the core

Implement the optional `IAmigaDiscardFileSystemPlatform` interface and call
`AmigaSmartFileSystemWriteCore.Trim`. Serialize the **entire call** with all
operations on the mount, including readers and other trims. The portable core
does not add its own locks. Backend control storage must not overlap scratch,
buffered transactions or write queues. Backend discard is synchronous, orders
before later writes, and may destroy any requested portion even when it fails.
The supplied `AmigaScsiUnmap` codec needs 256 bytes of word-aligned, device-suitable
control memory and an `IAmigaScsiDirectPlatform` adapter that checks `io_Error`.
Partition-byte offsets are translated to 64-bit device LBAs; ranges remain
partition-relative filesystem blocks.

Native clients can send the private DOS packet `0x43535452` (`CSTR`). Arg1 is an
APTR to 32 bytes of big-endian longwords: size (at least 32), version (1), flags
(0=execute, 1=dry run, 2=query), completed/proposed blocks, ranges, maximum blocks
per request, granularity, alignment. The last five fields are outputs. Other
flags are invalid. Result1 is DOS TRUE on success; Result2 carries a DOS error.
Unsupported handlers/devices return `ERROR_ACTION_NOT_KNOWN`.

## Validation boundary

Host tests sweep every checkpoint write/flush and every irreversible discarded
sector for create, overwrite, truncate, rename and delete, with buffered/strict
policies, small/full write queues, zero and nonzero discard patterns, and volatile
sector persistence subsets. Restarted manual TRIM must recover before discarding.
Separate tests cover original TRST replay, every partial-discard failure followed
by retry, corrupt bitmaps, multi-bitmap volumes, alignment, range limits, all 120
orders of two writers/one reader/two trims, and actual threads queued behind a
blocked discard. SCSI tests check transport/status/short replies and large offsets.

Compiled MC68000 tests check the native SCSI ABI and 64-bit partition translation.
Kickstart 3.1 tests execute the L: handler and command, overlap guest writer
processes with repeated trims, remount and run amidisk's independent deep check.
The emulator provider deliberately poisons freed sectors and can fail partially.
These are distinct layers of evidence, not physical SSD/controller qualification.
The crash model assumes atomic 512-byte sectors, honest completed flushes and
discard confined to its requested range; torn sectors, lying firmware and device
loss are outside this model. Automatic trimming remains disabled.

The native emulator completes each device request synchronously. Waiting-device
serialization is additionally exercised at the portable mount-owner boundary;
real asynchronous controller behavior remains unqualified.
