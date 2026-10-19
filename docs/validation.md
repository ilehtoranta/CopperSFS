# Validation boundary

On 2026-10-19 the standalone core's 31 host semantic tests passed in Release
configuration. They cover allocation, metadata and directory mutation,
interleaved reads and writes, injected failures at write and flush points, and
recovery after an interrupted upper-block append on a sparse logical volume
larger than 4 GiB.

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

The native runs used a synthetic SFS floppy image. Independent SFS tools,
independently formatted media, physical Amiga hardware, other ROM versions,
different block devices, and TD64/NSD partitions above 4 GiB have not been
checked. The host-side reader shares the CopperSFS core, so it is not an
independent implementation check.
