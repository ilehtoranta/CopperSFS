# Native TRST encoding, copying and working set

The subsequent [mount-buffer and cache optimization](native-trst-mount-cache.md) reduces sampled memory to 233.32 KiB, reports a fresh paired benchmark and qualification, and identifies a compiler copy-folding opportunity.

The native TRST writer now avoids three redundant block copies, carries only run-boundary values through its encoder, and starts with a small staging arena. Against the [previous checkpoint](native-trst-parity.md), durable write throughput improves **2.9–3.6%**, while sampled handler memory falls **36.6%**, from **498.23 to 315.71 KiB**. The journal bytes, device requests and flush counts are unchanged. Performance parity with original SFS remains unachieved.

## Implementation

The LONGCOMPRESSION planner still reads every old/final longword exactly once and emits the same four original SFS modes with the same 64-word run limit. Intermediate values now remain local to each run loop; only a mismatching pair is carried into the next run. This removes unnecessary register moves in the compiled MC68000 encoder without allocating memory or changing its wire format.

Three block copies were removed:

- A preferred append edits its bitmap directly in the private TRST arena, avoiding the scratch-to-arena copy. CSUN retains its existing undo-reservation ordering.
- The original-allocation check consumes a verified bitmap view while no other block access can expire it.
- Journal pages proven free in both original and final bitmaps use the existing immediate unpublished-write path. They no longer copy into or displace entries in the metadata LRU. Marker and home images retain their existing cache behavior.

The standalone classic handler initially allocates 16 staging slots: **8,408 bytes at a 512-byte block size**. If staging fills, it restores the original request, frees the small arena, allocates within the existing **192 KiB cap**, and retries the entire operation. Growth occurs only before journal publication; it never commits a prefix of a Write. Allocation failure preserves the old file and position. Requests exceeding the full arena still fail atomically.

Arena sizes are normalized to the exact indexed-layout extent, including index-size transitions, so teardown frees exactly what was allocated. The descriptor is initialized even when the following operation rejects its request before staging. Sizing divisions run only when allocating or growing. Mounted block geometry remains fixed for the allocation's lifetime.

The adaptive allocation applies only to the self-contained classic TRST handler. Combined SFS2 handlers and ROM extension clients retain their existing fixed-size allocation contract. Native state size and extension ABI are unchanged. The 315.71 KiB measurement is the sampled working set for these workloads, not a new worst-case memory limit: larger redo operations can still grow to the full staging cap.

## Compiled comparison

Both checkpoints were rerun with the same test driver, compiler and emulator assemblies, fixture, licensed ROM and original SFS binary. Each cell has five trials over 1 MiB. Measurements use guest cycles on an emulated 68020 at 7,093,790 cycles/s, 512-byte blocks, 32 filesystem buffers, 512 KiB chip RAM, 512 KiB slow RAM and 2 MiB fast RAM. There is no added physical seek or flush delay.

Durable throughput in KiB/s:

| Workload | Previous TRST | New TRST | Improvement | Strict CSUN | Buffered CSUN | Original SFS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 172.28 | 178.14 | 3.4% | 74.38 | 505.59 | 518.22 |
| 64 KiB append | 1,131.82 | 1,173.09 | 3.6% | 639.10 | 1,830.84 | 3,312.14 |
| 8 KiB existing-file overwrite | 152.97 | 157.38 | 2.9% | 40.00 | 39.82 | 2,442.13 |

TRST Write latency and I/O per MiB:

| Workload | Previous p50 / p95 ms | New p50 / p95 ms | Journal KiB | Total written KiB | Write requests | Flushes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 44.90 / 47.57 | 43.49 / 45.60 | 64.5 | 1,475 | 1,030 | 516 |
| 64 KiB append | 48.14 / 97.33 | 46.02 / 94.19 | 8.5 | 1,083 | 134 | 68 |
| 8 KiB overwrite | 49.85 / 60.05 | 48.22 / 58.46 | 64.0 | 1,387 | 854 | 512 |

All three TRST workloads read eight device sectors and perform no journal reads. Their I/O counters match the preceding checkpoint exactly. New sampled memory is 315.71 KiB for TRST, 309.91–373.91 KiB for strict CSUN, 359.24–423.24 KiB for buffered CSUN and about 264.59 KiB for original SFS.

The final 8 KiB append diagnostic reduces CopyBlock calls **2,994 → 2,611**, copy instructions **950,330 → 779,640**, and exclusive copy cycles **6.13 → 5.01 million**. Planner instructions fall **1,212,450 → 976,797**, with exclusive cycles **3.65 → 3.49 million**. Total workload cycles fall from about 42.16 to 40.78 million. Diagnostic entry transitions identify the removed copy sites without injecting guest code or changing guest time.

Original SFS still groups metadata commits across Write packets and overwrites existing data in place. The comparison preserves TRST's per-Write durability and whole-data replacement guarantee; it does not establish equivalent atomicity for the original handler. The earlier per-Write ACTION_FLUSH diagnostic and remaining implementation costs are discussed in the preceding checkpoint.

## Qualification

The final frozen candidate passed 292 embedded core tests, with one optional external-image test skipped, and 75 compiled MC68000 buffer/codec tests. The latter include allocation-extent/index-transition checks, memory canaries, complete arena filling, codec modes, alignment, DMA, queue ordering and cache validation.

The new compiled low-memory scenario exhausts RAM in 64 KiB allocations, forces small-arena growth to fail during a 128 KiB overwrite, checks unchanged old contents, size and position, releases the pressure and retries the full Write. The resulting image passes independent checks and readback with both Copper and original SFS.

The paired benchmark runs passed all 120 trials with independent image and exact-byte checks. Strict and buffered CSUN bulk-append crash cases also pass. All 13 classic tests and all five relocation scenarios pass, including original-handler replay, interrupted recovery, legacy CSUN migration, capacity/reuse, shared and concurrent handles, and A500 integration. Their receipts cover **1,486 writer sector/flush cut points**, represented by **76 distinct durable images and 152 recovery boots**, plus **6 interrupted-recovery points in 12 boots**. All **11 SFS2 tests** also pass, covering 63 cut points in 30 cut/reboot groups, original redo, arena reuse, wide-device behavior and 68000/68020 execution.

The full CopperStart Release solution still fails in the separate CopperOS Shell project because `LoadSegmentNative`, `UnloadSegmentNative` and `UnloadDosProcessSegment` are absent from its DOS platform API. Standalone builds and the isolated compiled filesystem tests succeed. The full solution is not reported as passing.

## Source and evidence boundary

The [313-file source manifest](../artifacts/native-trst-working-set/source-qualified-v1.json) pins `CopperStart/artifacts/native-trst-working-set/source-qualified-v1`. Native production changes live in that sibling checkout's embedded `external/CopperSFS/src/CopperSFS.Core` and `src/CopperStart.Dos` directories. They have not been copied into the separate portable source tree in this repository.

The snapshot preserves the coordinated sequential-read fast path and its regression test. Later shared read-cursor work in the live checkout is outside this frozen qualification. The benchmark uses one frozen driver for both old and new handlers, and does not attribute the separate read work to TRST.

Artifacts are under `artifacts/native-trst-working-set/`: `benchmark/report.md`, `baseline-benchmark/report.md`, the three `trst-profile-*` folders, test receipts, build logs and the source manifest. Build from the frozen `build/Build-SfsHandler.ps1`; use the repository's `Test-NativeTrst.ps1`, `Test-NativeSfs2.ps1` and `Measure-NativeWritePolicies.ps1` scripts with explicit frozen handler/test-assembly paths.

The [evidence audit](../artifacts/native-trst-working-set/audit.json) passes: all 313 frozen source hashes match, tested/benchmarked/profiled HUNK identities agree, all four native compatibility reports are allocation-free, the comparison uses identical driver inputs, and the TRST benchmark I/O counters are unchanged. It records the three live files containing later shared read work, and retains the failed full-solution build separately.

| Handler | HUNK bytes | SHA-256 |
| --- | ---: | --- |
| TRST | 153,388 | `76A06F47DC1054BCB32F7FA966C86BEA8A6D790FFCBF3C86C8BDFF08D5859855` |
| Strict CSUN | 155,692 | `B6F605C1F2B966E4D1E7DE41903F0CC2E4F71F27E202C0B3C67E8B4DC975F9C6` |
| Buffered CSUN | 155,916 | `646EB16485E50AE9235DF95B19A19138D8C63CF90DE1383B06751F6B64E02EDD` |
| SFS2 TRST | 205,060 | `79B39AD5283B3ADD9BF4BCBF99CF062686B32B8DB5F111B6A4A832D8A4533316` |

The shared comparison driver SHA-256 is `27FB87E935E78F7C064B22E8D0711AFE1D7E6B25D58D63A8F963BA3068E1CB5C`.

These are compiled-handler tests on emulated hardware, assuming atomic sectors and reliable flushes. They do not qualify physical-device power loss or a new ROM/media release. Native TRST remains the development default; no binary installation or package release is included.
