# Validation boundary

On 2026-10-20 the standalone core's 109 host semantic tests passed in Debug
and Release configurations. They cover allocation, metadata and directory mutation,
interleaved reads and writes, injected failures at write and flush points, and
recovery after an interrupted upper-block append on a sparse logical volume
larger than 4 GiB. Batched transactions are tested with buffer capacities of
1, 2 and 32 blocks, both buffered and immediately persistent device writes,
cuts after every write and flush, and live I/O failure followed by recovery
and retry. An overwrite case also enumerates every subset of pending sectors
that could reach media before a flush. Allocation tests cover used bitmap
words and cursor wraparound, including every write/flush interruption point.
Transfer tests cover aligned and unaligned buffers, partial sectors, fragmented
files, and preservation of bytes outside an overwritten range. These are host
semantic tests, not compiled MC68000 execution.

Eight compiled MC68000 transfer cases also passed for reads and overwrites,
including aligned and unaligned buffers, fragmented extents, and guard bytes.
They execute the core against a RAM-backed test platform; they do not boot
AmigaDOS or establish real-device DMA behavior.

The current batched MC68000 handler passed all 12 native A/B tests against
original SmartFilesystem 1.279 under Kickstart 3.1, using a 68020 emulator
profile with 512 KiB chip RAM and 2 MiB fast RAM. Each independent workload
started from its own byte-for-byte copy of the same independently formatted
16 MiB, 512-byte-block image. The shared workload includes interleaved writers
and a reader, file positions, directory changes and metadata. Clean remounts,
clean handoff in both directions, and CopperSFS replay of an original `TRST`
transaction passed, with amidisk's independent deep consistency and file checks.

The unsampled native recovery sweep passed all 177 recorded write and flush
cut points: 115 for CopperSFS and 62 for the original handler, across create,
overwrite, truncate, rename and delete. Reboots used the writing handler.
CopperSFS recovered the old or new state of each individual mutation; the
create workload can also retain the empty file committed by Open before its
separate Write packet. The original handler's observed outcomes do not imply
stronger guarantees than its documentation establishes. These tests model
atomic 512-byte sectors and honored flush barriers, not torn sectors or
controller reordering.

The same native run completed 60 performance trials: five trials per handler
for each of five cold workloads, plus separate repeated-read trials after
warming the cache. Measurements exclude boot and image-copy time and describe
guest CPU cycles and device I/O, not physical Amiga throughput.
The tested handler's SHA-256 is
`304F2D04AF19277DAD1E911CF5D060549D4DBB3A43D537260AE7ABB028639622`.

The preview handler is compiled for MC68000. A separate check loaded and
unloaded its HUNK with native AmigaDOS `LoadSeg` and `UnLoadSeg`. In an emulated
A500 with a licensed Kickstart 3.1 ROM, the handler then received
`ACTION_STARTUP` from native AmigaDOS on `trackdisk.device`, read an existing
1,300-byte file, created and read back a 1,024-byte file, and read it again
after a fresh boot. Another native run launched two DOS processes that each
wrote a separate 512-byte file while the parent read the SFS volume. The
completed images were mounted and their file bytes checked by a host-side
CopperSFS reader.

One native power-cut replay captured a complete ADF while the SFS transaction
marker indicated an unfinished write. A fresh Kickstart 3.1 boot recovered
that image and produced a mountable volume whose file bytes passed the same
host-side reader. This is one interruption point, not exhaustive crash coverage.

The earlier native runs described above used a synthetic SFS floppy image.
Their host-side reader shares the CopperSFS core, so those checks alone are
not independent implementation checks. The newer hardfile A/B suite
uses amidisk as described above. Physical Amiga hardware, other ROM versions,
and native TD64/NSD partitions above 4 GiB have not been qualified by this
comparison.
