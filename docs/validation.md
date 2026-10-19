# Validation boundary

On 2026-10-19 the standalone core's 30 host semantic tests passed in Release
configuration. They cover allocation, metadata and directory mutation,
interleaved reads and writes, injected failures at write and flush points, and
recovery after an interrupted upper-block append on a sparse logical volume
larger than 4 GiB.

These tests exercise the core on the host. The preview handler is compiled
for MC68000. A separate Kickstart 3.1 emulator check successfully loaded and
unloaded the packaged HUNK with native AmigaDOS `LoadSeg` and `UnLoadSeg`.
It did not start or mount the handler, so neither check establishes handler
packet behavior under native AmigaOS or a complete boot path.

Before treating the handler as qualified, test `ACTION_STARTUP`, reads, writes,
remounting, concurrent DOS tasks and interrupted writes under a native
AmigaOS ROM on independently formatted media. Run an independent consistency
checker on recovered images. Record the device's TD64/NSD behavior for
partitions extending beyond 4 GiB.
