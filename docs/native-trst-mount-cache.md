# Native TRST mount buffers and cache replacement

The subsequent [buffered TRST integration](native-trst-group.md) changes the classic standalone default after user approval and qualification. This page records the preceding strict checkpoint and its measurements.

The standalone classic TRST handler now avoids unused undo storage, uses a one-block physical write queue, and replaces cached home blocks without removing and reinserting their cache entries. Against a paired baseline containing the same coordinated read changes, durable throughput improves **2.4–3.5%** and sampled handler memory falls **26.2%**, from **316.09 to 233.32 KiB**. That sampled memory is about **11.8% below original SFS**. Throughput parity with original SFS remains unachieved.

This follows the [encoding, copy and staging-arena changes](native-trst-working-set.md). The journal format, transaction barriers, whole-write atomicity and legacy CSUN recovery remain intact.

## Implementation and bounds

Classic standalone TRST stages mutations in its redo arena, so its unused 32-block CSUN undo buffer is no longer allocated. Legacy CSUN recovery already supports an absent undo buffer. CSUN profiles, combined SFS2 handlers and ROM extension clients retain their existing allocation contracts.

TRST sends retained home images and unpublished runs directly to the device. Its remaining physical queue carries isolated marker writes, so the classic standalone profile now allocates one slot instead of 128. Allocation, initialization and destruction use the same capacity constant. The queue still tracks pending writes and preserves flush ordering; a capacity-triggered drain issues writes without inserting a durability barrier. A failed drain retains its image for recovery or retry, including when the device accepted the write before reporting failure.

`WriteRetainedBlock` now keeps an existing cache entry in place, replaces its bytes after successful device I/O, clears its verification state and touches its LRU position. Directory views still expire before I/O. Failed or partial writes forget the cached image so a later read must inspect the device. Cache misses follow the existing insertion path, and DMA bounce behavior is unchanged.

The staging arena still starts with 16 slots and can grow only before publication, by retrying the complete operation within its **192 KiB cap**. No prefix of a Write is committed to save memory. The 233.32 KiB figure is a sampled working set for these workloads, not a maximum allocation high-water mark; larger operations can still grow the arena.

## Compiled comparison

Each cell has five cold trials writing 1 MiB. Baseline and candidate share one frozen test driver, emulator assemblies, licensed Kickstart 3.1 ROM, fixture and original SFS binary. Both also contain the completed sequential-read changes. The current strict and buffered CSUN binaries are held constant in both comparison runs.

Measurements use guest cycles on an emulated 68020 at 7,093,790 cycles/s, 512-byte blocks, 32 filesystem buffers, 512 KiB chip RAM, 512 KiB slow RAM and 2 MiB fast RAM, with no added physical seek or flush delay. Every output passed independent image checks and exact-byte extraction.

Durable throughput in KiB/s:

| Workload | Baseline TRST | New TRST | Improvement | Strict CSUN | Buffered CSUN | Original SFS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 178.83 | 185.17 | 3.5% | 74.33 | 506.98 | 518.65 |
| 64 KiB append | 1,174.63 | 1,215.34 | 3.5% | 637.95 | 1,832.76 | 3,326.49 |
| 8 KiB existing-file overwrite | 158.00 | 161.83 | 2.4% | 39.99 | 39.89 | 2,452.33 |

TRST Write latency and device I/O per MiB:

| Workload | Baseline p50 / p95 ms | New p50 / p95 ms | Journal KiB | Total written KiB | Write requests | Flushes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 43.30 / 45.86 | 41.74 / 44.30 | 64.5 | 1,475 | 1,030 | 516 |
| 64 KiB append | 46.56 / 91.56 | 43.35 / 95.21 | 8.5 | 1,083 | 134 | 68 |
| 8 KiB overwrite | 48.33 / 58.26 | 47.03 / 57.05 | 64.0 | 1,387 | 854 | 512 |

The 64 KiB append p95 regresses by about 4% despite improved median latency and throughput. The other two workloads improve both percentiles. All three TRST workloads retain exactly the baseline I/O counters, including eight device reads and no journal reads. Mounted TRST memory falls from 307.61 to 224.84 KiB; maximum sampled retention falls from 316.09 to 233.32 KiB.

Original SFS groups metadata commits across Write packets and overwrites existing data in place. TRST continues to make each successful Write durable and preserves whole-data replacement through recovery. The original's faster results do not establish equivalent atomicity. The [full benchmark report](../artifacts/native-trst-mount-cache/benchmark/report.md) includes service throughput, all policies' memory and I/O, and the measurement boundaries.

## Qualification

The frozen candidate passed:

- **292 embedded core tests**, with one optional external-image test skipped.
- **79 compiled MC68000 buffer and codec tests**, including one-slot queue pressure, failed drains before and after device acceptance, cache collision preservation and failed retained-write invalidation.
- **14 classic integration tests**, including original-handler recovery, legacy CSUN recovery, interrupted recovery, shared and concurrent handles, low-memory atomic retry, and the coordinated sequential-read regression.
- All five relocation crash scenarios. Together with the classic suite these cover **1,486 writer sector/flush cuts**, **76 distinct durable images** and **152 recovery boots**, plus **6 interrupted-recovery cuts in 12 boots**.
- Both strict and buffered CSUN bulk-append crash suites.
- **11 SFS2 tests**, covering **63 cut points in 30 cut/reboot groups**, original recovery, arena reuse, wide devices and 68000/68020 execution.
- **120 paired benchmark trials**, with independent image and exact-byte checks.

The full CopperStart Release solution still fails in the separate CopperOS Shell project because `LoadSegmentNative`, `UnloadSegmentNative` and `UnloadDosProcessSegment` are missing from its DOS platform API. Standalone native builds and focused filesystem tests pass; the full solution is not reported as passing.

## Remaining copy bottleneck

A separate compiler diagnostic confirmed that unreferenced CIL labels block load/store folding in `CopyBlock`, although the temporary register is dead. An isolated prototype changes the two copy-fold guards to use the existing `CanRelocateLabels` check, preserving branch targets and internal, external and PC-relative address references. It does not change the live compiler sources.

The prototype passes **509 focused compiler cases**, including 90 label/reference cases with execution comparisons on 68000 and 68020, and all **79 compiled filesystem buffer tests**. Its single 8 KiB append profile reduces copy-loop instructions from **779,640 to 573,662** and exclusive copy cycles from **5.02 to 4.34 million**, but improves the complete workload by only **1.1%**. The emitted loop now contains memory-to-memory longword moves. Its HUNK is 153,268 bytes, SHA-256 `D8D19A2890E5E1B98D2C895896B9C43888D82A839968E9C1A9D2C4E147739BAE`.

That prototype is **not part of the qualified default**. Full handler crash and compatibility qualification remains necessary before promoting it. Its source override, regression cases, diagnostic, disassembly and receipts are retained under `artifacts/native-trst-mount-cache/` for the next optimization pass.

## Reproducibility

The [313-file manifest](../artifacts/native-trst-mount-cache/source-qualified-v1.json) pins `CopperStart/artifacts/native-trst-mount-cache/source-qualified-v1`. Production changes are in the sibling checkout's native handler; the separate portable source tree is unchanged by this checkpoint. The source snapshot includes the completed coordinated read work.

The [evidence audit](../artifacts/native-trst-mount-cache/audit.json) passes: frozen and live source hashes match, tested/profiled/benchmarked HUNK identities agree, all four native compatibility reports are allocation-free, paired benchmark drivers match, and TRST I/O counters are unchanged. The failed full-solution build is recorded separately.

| Handler | HUNK bytes | SHA-256 |
| --- | ---: | --- |
| TRST | 153,644 | `2F8639C477B130D8924E7287BCA182F0514EFB4D620D669C7F21A2212C3D43EF` |
| Strict CSUN | 156,128 | `58D052D437D18270813D2798F67E28A55522DBB692BD258CA84B465C69B09D6E` |
| Buffered CSUN | 156,352 | `242DC18A5112D364334DB6D15707812BC7E896CBD99A9CF2DB009E11681878F1` |
| SFS2 TRST | 205,536 | `DD9D528D541C633E7976F3197E1C529B0CC58734539993E1AC4B6DD92AED5700` |

The frozen comparison driver SHA-256 is `5BDD3809F80AEB466B57D750D498B669C2287A37333FD21175824F94EA4AFED3`. Build and test commands use the same scripts and explicit frozen paths described in the preceding checkpoint, with artifacts under `artifacts/native-trst-mount-cache/`.

These are compiled-handler tests on emulated hardware, assuming atomic sectors and reliable flushes. They do not qualify physical-device power loss or a new ROM/media release. No installation or package release is included.
