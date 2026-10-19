# Validation boundary

On 2026-10-19 the standalone core's 30 host semantic tests passed in Release
configuration. They cover allocation, metadata and directory mutation,
interleaved reads and writes, injected failures at write and flush points, and
recovery after an interrupted upper-block append on a sparse logical volume
larger than 4 GiB.

CopperStart separately passed a compiled MC68000 replay that wrote to an SFS
partition located above the 4 GiB physical offset and read it after a fresh
boot. That replay used a headless storage qualification ROM and the `$E00000`
SFS extension. It is evidence for the storage path, not for the standalone
`L:` handler or a complete graphical Kickstart image.

Before a binary handler release, test `ACTION_STARTUP`, reads, writes,
remounting, concurrent DOS tasks and interrupted writes under a native
AmigaOS ROM on independently formatted media. Run an independent consistency
checker on recovered images. Record the device's TD64/NSD behavior for
partitions extending beyond 4 GiB.
