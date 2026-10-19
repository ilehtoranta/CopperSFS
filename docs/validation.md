# Validation boundary

On 2026-10-19 the standalone core's 30 host semantic tests passed in Release
configuration. They cover allocation, metadata and directory mutation,
interleaved reads and writes, injected failures at write and flush points, and
recovery after an interrupted upper-block append on a sparse logical volume
larger than 4 GiB.

These tests exercise the core on the host. They do not establish the behavior
of a compiled MC68000 handler or a complete AmigaOS boot path.

Before a binary handler release, test `ACTION_STARTUP`, reads, writes,
remounting, concurrent DOS tasks and interrupted writes under a native
AmigaOS ROM on independently formatted media. Run an independent consistency
checker on recovered images. Record the device's TD64/NSD behavior for
partitions extending beyond 4 GiB.
