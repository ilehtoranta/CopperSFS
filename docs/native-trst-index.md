# Indexed TRST staging — 2026-10-25

Follow-up: [TRST encoding and block-copy optimization](native-trst-codec.md)
retains this arena layout and records the next compiled qualification and benchmarks.

Replacing repeated staging scans with an in-arena hash index raises compiled TRST's 64 KiB append throughput from **15.10 to 33.56 KiB/s (2.22×)**. Median Write latency falls from **4.81 to 1.89 seconds**. Staging-address and lookup helpers consume **95.5% fewer cycles**, and total profiled CPU time falls 55.0%. The 192 KiB allocation bound and whole-write commit protocol are preserved.

The comparison baseline is the [previous qualified TRST/bulk-allocation build](native-write-fixes.md), not the earlier byte-at-a-time implementation. These are emulated guest cycle measurements from compiled MC68000 handlers, not managed-host throughput.

## Implementation

`AmigaSmartFileSystemRedoArena` now reserves a power-of-two table of slot addresses within the supplied arena. An inexpensive XOR/shift hash includes high block bits; probing resolves collisions. The table remains at most 75% occupied and always contains an empty bucket, including the smallest supported arenas. Block zero and `uint.MaxValue` remain valid keys because zero denotes an empty slot address, not a block number.

Both native overlay reads/writes and journal-space reservation use this index. Repeated writes replace the existing staged block without consuming another slot. The commit descriptor caches its stride, removing repeated geometry reads when iterating retained images. The table is cleared on each transaction, so aborted or completed operations cannot leave stale mappings for the next request.

The fixed **192 KiB** allocation contains a 24-byte header, a 2 KiB index at 512-byte blocks, and **377 data/metadata slots**. The previous arena held 380; the index costs three slots (1.5 KiB of payload capacity) without increasing the allocation. Oversized requests still fail before publication with `NoFreeStore`. The existing 64 KiB and 128 KiB whole-write cases remain supported. Metadata also consumes slots; the arena capacity is not a promise that every 188.5 KiB data packet will fit.

The private header's block-size high bit distinguishes indexed arenas from SFS2's existing 16-byte linear layout. Shared readers retain the old lookup/store path for SFS2, whose initializer, capacity, journal code and specific source files remain unchanged. This tag exists only in RAM. Classic CSEB/v12, SFS2 CSEC/v13 and StateBytes 2320 are unchanged. The snapshot also includes the coordinated classic record-lock dispatch update in `NativeAmigaFileSystem.cs`; that task's API qualification is separately recorded.

The public and embedded cores contain identical arena implementations. The indexed structure allocates no additional heap memory. The caller still serializes access to a volume.

## Atomicity and compatibility

The lookup change does not alter the commit sequence: flush the completed journal, publish and flush TRFA, apply retained final images, flush home writes, then write and flush TROK. The new table changes only how staged images are found. Recovery continues to validate the complete journal before applying it, supports interrupted recovery, and retains legacy CSUN recovery.

New arena tests cover deliberate collisions and wraparound, coalescing at capacity, missing-key termination in tiny arenas, zero/high block keys, reset after exhaustion, canaries outside the arena, supported block sizes, and the old SFS2 layout. An operation-count budget prevents the sequential-lookup case from regressing to a linear scan.

| Qualification | Result |
| --- | --- |
| Compiled classic TRST suite | 12 passed, no skips; crash cuts, original SFS replay, legacy CSUN recovery, arena exhaustion/reuse, shared/concurrent handles and native A500 cases |
| Compiled CSUN comparison builds | Strict and buffered 64 KiB cut tests passed; Copper recovery followed by original SFS reading the clean volume |
| Portable original-handler replay | Passed for data and metadata redo journals |
| Embedded core | 274 passed, 1 optional external-image skip |
| Public core, final source | 372 passed, 5 external-input skips; original-handler TRST replay was run separately and passed |
| Compiled SFS2 TRST compatibility | 11 passed, no skips; 68000/68020 mutation/remount, original redo, arena reuse, wide packets and 63 cut points in 30 reboot groups |
| Compiled benchmark | All 60 trials passed deep image checking and exact-byte extraction |
| Final instruction profile | Passed; no classic calls to legacy linear lookup or healthy recovery replay |
| CopperStart Release solution | Passed, 4 existing warnings, 0 errors |

Classic TRST covers **902 writer cuts plus 6 interrupted-recovery cuts**, grouped into **53 distinct cut images and 106 recovery boots** across Copper and original SFS. The 64 KiB and 128 KiB cases account for 283 and 548 covered points. Strict CSUN covers 161 points in 9 images; buffered CSUN covers 152 points in 5 images.

Crash receipts distinguish covered sector/flush points from actual reboot groups. They assume atomic 512-byte sectors and reliable flushes. Original SFS directly replays TRST; it cannot replay CSUN, whose compatibility check follows Copper recovery. The HUNK tests and native A500 integration do not establish a new extension-ROM/media replay qualification.

## Benchmark change

Durable throughput includes open, writes, close and final successful Inhibit. Values below are KiB/s. Five cold trials are checked per cell.

| Workload | Previous TRST | Indexed TRST | Improvement | Strict CSUN | Buffered CSUN | Original SFS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 19.47 | **22.01** | **13.0%** | 70.60 | 503.08 | 516.91 |
| 64 KiB append | 15.10 | **33.56** | **2.22×** | 604.74 | 1763.08 | 3249.23 |
| 8 KiB overwrite | 22.12 | **25.30** | **14.4%** | 36.83 | 36.69 | 2422.59 |

| TRST workload | Write p50 before → after, ms | Write p95 before → after, ms |
| --- | ---: | ---: |
| 8 KiB append | 422.95 → **367.22** | 426.35 → **369.81** |
| 64 KiB append | 4810.80 → **1885.20** | 4874.02 → **1965.92** |
| 8 KiB overwrite | 358.89 → **314.81** | 364.88 → **319.91** |

Every workload retains exactly the previous total read/write sector counts, journal read/write volume, flush count and device write-request count. This is a CPU improvement without reduced journaling or durability barriers. CSUN throughput remains within 0.3% of the preceding qualified build. The comparison includes the coordinated record-lock dispatch change and regenerated code layout; it is not a single-source-line experiment.

TRST's maximum sampled guest retention rises from **490.56 to 491.20 KiB**, including code, stack, caches and the 192 KiB arena. The increase is 656 bytes of sampled retained memory; it is not an additional arena allocation. The HUNK grows by 688 bytes, including compatibility dispatch and the shared record-lock change. Samples are not an allocation high-water mark.

## Profile and remaining cost

The 64 KiB diagnostic profile falls from **481,156,547 to 216,420,728 guest cycles**. Exclusive cycles in `RedoArena` methods and `RedoPlatform.Slot/Find/Image` fall from **244,698,641 to 10,907,488**, a 95.5% reduction. `Slot` alone falls from 46.23% to about 2.01% of total measured cycles. Hash probing accounts for about 1.45%.

The largest remaining costs are `TrstEncode` at **29.54%** and `CopyBlock` at **19.42%**. TRST remains substantially slower than original SFS. Whole-data-write journaling costs more than original SFS's metadata-only journal, but the profile still identifies implementation work available in encoding and memory copies. This change does not claim those costs are unavoidable.

Original SFS and buffered CSUN can acknowledge pending writes. Strict CSUN and TRST make successful Write packets durable; final Inhibit provides the common clean checkpoint for the throughput comparison. The emulator adds no seek, rotational or flush latency. The overwrite range fits within the seeded file's first extent. Full methodology and all final measurements follow below.

## Reproducible evidence

Artifacts are local under `artifacts/native-trst-index/`. The [source manifest](../artifacts/native-trst-index/source-v2.json) pins 289 production/build/test inputs in the sibling CopperStart `artifacts/native-trst-index/source-v2` snapshot. `public-source.json` pins the public core. The [final audit](../artifacts/native-trst-index/final-audit.json) passed: frozen inputs match live sources, and benchmark/crash binary identities, test receipts and profile exclusions are checked.

Use `scripts/Test-NativeTrst.ps1`, `scripts/Test-TrstOriginalHandler.ps1`, `scripts/Test-NativeSfs2.ps1` and `scripts/Measure-NativeWritePolicies.ps1` with explicit artifact paths and fresh result directories. Benchmark settings remain five trials per handler/workload. `scripts/Summarize-NativeWritePolicies.py` generates the complete tables below. The native test assembly hash is `FF734DE27E248675DF419092517DB876E009D8C68ECE85850BC492301BDDD1DD`.

The default remains TRST. No installation or release was performed; generated HUNKs and licensed media remain outside version control.

The separately checked SFS2 TRST HUNK is 197008 bytes, SHA-256 `7159CA0E1AF250516C26EDE4360823F5AAD3C85B554E555D44587C695EFF0961`. The classic benchmark HUNK identities are listed below.

## Complete final benchmark


5 cold trials per cell, 60 checked runs. Each writes 1 MiB; every output passed amidisk deep checking and exact-byte extraction.

Rates and latencies use emulated PAL time (7,093,790 cycles/s), not host wall time. All binaries run on the same emulated 68020, 512 KiB chip + 512 KiB slow + 2 MiB fast RAM, 512-byte blocks and 32 buffers. The device adds no seek, rotation or flush delay. Boot and validation are excluded. Four independent machines run concurrently; timing comes from each guest's own cycle counter.

Each fresh machine mounts and verifies the seed Hello file before the measured interval. Append workloads create a 1 MiB file. The overwrite workload replaces the first 1 MiB of the fixture's 3 MiB file; this range fits within its first extent. Independent extraction also checks that its remaining 2 MiB are unchanged. The raw workload tag retains the seeded file's name, Fragmented; this is an existing-file overwrite measurement. Payload byte i is (i * 17 + 5) & 255, so results concern this nonzero-word pattern rather than zero-filled or varied-compressibility data.

## Throughput and Write latency

Durable throughput includes file open, all writes, Close and successful Inhibit. Service throughput uses only the summed DOS.Write intervals. Latency p50/p95 pools every Write call across trials (nearest-rank p95); all other figures are medians across trials. Fixed marker overhead is included.

| Workload | Handler | Durable KiB/s | Write service KiB/s | Write p50 ms | Write p95 ms | Write max ms | Final checkpoint ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| append-8k | TRST | 22.01 | 22.06 | 367.220 | 369.811 | 415.065 | 7.340 |
| append-8k | CSUN-strict | 70.60 | 71.10 | 111.966 | 113.374 | 150.615 | 7.340 |
| append-8k | CSUN-buffered | 503.08 | 560.59 | 12.343 | 15.077 | 84.965 | 148.609 |
| append-8k | Original | 516.91 | 533.22 | 14.698 | 16.914 | 25.859 | 35.239 |
| append-64k | TRST | 33.56 | 33.68 | 1885.204 | 1965.918 | 1965.918 | 7.339 |
| append-64k | CSUN-strict | 604.74 | 643.21 | 96.319 | 159.177 | 159.177 | 8.159 |
| append-64k | CSUN-buffered | 1763.08 | 2362.75 | 14.853 | 94.312 | 94.312 | 89.075 |
| append-64k | Original | 3249.23 | 3924.81 | 14.886 | 26.001 | 26.001 | 31.423 |
| existing-file-overwrite-8k | TRST | 25.30 | 25.31 | 314.809 | 319.908 | 320.933 | 7.984 |
| existing-file-overwrite-8k | CSUN-strict | 36.83 | 36.85 | 206.954 | 212.589 | 607.997 | 7.340 |
| existing-file-overwrite-8k | CSUN-buffered | 36.69 | 36.71 | 207.723 | 213.335 | 610.796 | 7.553 |
| existing-file-overwrite-8k | Original | 2422.59 | 2633.36 | 2.828 | 4.215 | 6.823 | 24.242 |

## Device and journal I/O per 1 MiB

Journal writes include TRST pages or CSUN descriptors plus raw saved old-block images. They exclude transaction markers and final home writes, which remain included in total writes. Journal reads follow observed sector roles and valid journal headers, including stale headers examined by the allocator. Counts describe requested sector transfers, not a controller's eventual write amplification.

| Workload | Handler | Total writes KiB | Journal writes KiB | Journal reads KiB | Total reads (sectors) | Flush commands | Write requests |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| append-8k | TRST | 2563.0 | 1152.5 | 499.0 | 8152 | 516 | 1351 |
| append-8k | CSUN-strict | 1925.5 | 515.0 | 0.0 | 3072 | 516 | 904 |
| append-8k | CSUN-buffered | 1035.5 | 7.0 | 0.0 | 8 | 4 | 133 |
| append-8k | Original | 1029.0 | 1.0 | 0.0 | 0 | 4 | 132 |
| append-64k | TRST | 2155.0 | 1080.5 | 436.5 | 3080 | 68 | 197 |
| append-64k | CSUN-strict | 1141.5 | 67.0 | 0.0 | 384 | 68 | 135 |
| append-64k | CSUN-buffered | 1035.5 | 7.0 | 0.0 | 8 | 4 | 26 |
| append-64k | Original | 1029.0 | 1.0 | 0.0 | 0 | 4 | 20 |
| existing-file-overwrite-8k | TRST | 2304.0 | 1088.0 | 0.0 | 4592 | 512 | 896 |
| existing-file-overwrite-8k | CSUN-strict | 3527.0 | 2176.0 | 1507.0 | 10096 | 924 | 1066 |
| existing-file-overwrite-8k | CSUN-buffered | 3527.0 | 2176.0 | 1507.0 | 10096 | 924 | 1066 |
| existing-file-overwrite-8k | Original | 1025.5 | 0.5 | 0.0 | 0 | 4 | 131 |

## Guest memory

KiB consumed relative to Exec.AvailMem(Any) immediately before loading the handler. This includes HUNK/code, process stack, DOS mount records, caches and handler buffers. Common probe buffers are already allocated at baseline. Samples are taken mounted, opened, after first/final Write, closed and inhibited. Maximum sampled retention is not an allocation high-water mark and can miss transient allocations. Inhibit does not unload the handler process/code.

| Workload | Handler | Mounted KiB | After first Write KiB | Maximum sampled KiB | After Inhibit KiB |
| --- | --- | ---: | ---: | ---: | ---: |
| append-8k | TRST | 298.94 | 491.20 | 491.20 | 490.94 |
| append-8k | CSUN-strict | 305.52 | 305.78 | 305.78 | 305.52 |
| append-8k | CSUN-buffered | 355.34 | 355.61 | 355.61 | 355.34 |
| append-8k | Original | 248.37 | 264.59 | 264.59 | 264.10 |
| append-64k | TRST | 298.94 | 491.20 | 491.20 | 490.94 |
| append-64k | CSUN-strict | 305.52 | 305.78 | 305.78 | 305.52 |
| append-64k | CSUN-buffered | 355.34 | 355.61 | 355.61 | 355.34 |
| append-64k | Original | 248.37 | 264.59 | 264.59 | 264.10 |
| existing-file-overwrite-8k | TRST | 298.94 | 491.20 | 491.20 | 490.94 |
| existing-file-overwrite-8k | CSUN-strict | 305.52 | 369.78 | 369.78 | 369.52 |
| existing-file-overwrite-8k | CSUN-buffered | 355.34 | 419.61 | 419.61 | 419.34 |
| existing-file-overwrite-8k | Original | 248.37 | 248.55 | 264.41 | 264.10 |

## Binary identity and interpretation

These results apply to the exact binaries listed below. Source snapshots and crash qualification must be matched to those hashes; this report does not infer qualification or ABI versions from handler names. Copper binaries target 68000; original SFS 1.279 requires 68020. All use identical guest hardware and filesystem fixtures.

TRST and strict CSUN make successful Write packets durable. Buffered CSUN and original SFS can acknowledge pending writes; their short Write-return latency is a different durability contract. The successful final Inhibit brings all four to a clean checkpoint. TRST journals file data for whole-write atomicity; original SFS's metadata journal has different data guarantees.

| Handler | HUNK bytes | SHA-256 |
| --- | ---: | --- |
| TRST | 144524 | `82CB5E58CB8A1DF15949F43F26E1A7D8F4D45D86FF7A4F9D27FBBA8E0F385F23` |
| CSUN-strict | 151260 | `50DB1B28C3F264146F61B0F29F1460E7524DE74D6FCAB04E26CC8AF1D67DEBE8` |
| CSUN-buffered | 151480 | `00436C15F68ABC7C010AC6A3AE09C6CEC0BA87957036DD9D517F48CEA9A5E68C` |
| Original | 97984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |

