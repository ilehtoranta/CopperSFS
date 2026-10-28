# Faster TRST extent updates

The subsequent [bitmap-run and checked extent-reuse optimization](native-trst-runs.md) is integrated and adds 3.8–7.5% throughput. The later [MC68000 arithmetic optimization](native-trst-arithmetic.md) contains the latest benchmark and recovery qualification; the figures below describe the preceding extent-update checkpoint.

The native buffered TRST handler now reuses extent-record locations within a Write and combines changes to records in the same leaf. The compiled overwrite benchmark improves from **250.29 to 308.71 KiB/s**, a **23.3% gain** over the [buffered-default checkpoint](native-trst-group.md). Appends are 0.2–0.7% slower. Original SFS is still faster; throughput parity remains unmet.

The existing whole-write transaction and delayed-durability policy are preserved. A successful Write becomes visible as a complete request, and becomes durable at a successful filesystem `ACTION_FLUSH`, writer Close, Inhibit or timer commit. The two staging arenas still share the **192 KiB cap**, with **8,496 bytes** allocated initially for 512-byte blocks. No persistent cache or additional arena storage was added.

## What changed

Sequential overwrites extend an earlier relocated extent while advancing the start of the unmodified tail. Previously, this path repeatedly searched the extent tree for records already found earlier in the same operation. It also verified and stored the same leaf separately for adjacent-record changes.

The writer now retains scalar record locations across bitmap allocation and unpublished data I/O. Those operations cannot insert or remove extent records. Before modifying a leaf, it reacquires the image through the existing checked editing path; it does not retain a cache pointer across I/O. Changes to the preceding extent, remaining tail and following extent are combined when those records share a leaf. Separate leaves retain their individual checked edits.

The complete overwrite profile falls from 29.02 to **23.53 million cycles**. Extent-tree lookups fall from **1,412 to 787**, and checksum scans from **2,274 to 1,403**. Exclusive instruction cycles in extent lookup fall from 4.15 to 2.33 million, and checksums from 4.12 to 2.54 million. Block copying remains approximately 2.09 million cycles. The on-disk format, publication barriers and journal allocation are unchanged.

## Compiled benchmark

Five cold trials per cell write 1 MiB on the same emulated 68020 at 7,093,790 cycles/s, with 512-byte blocks, 32 filesystem buffers, 512 KiB chip RAM, 512 KiB slow RAM and 2 MiB fast RAM. The device adds no seek or flush delay. The frozen benchmark driver, original handler, fixture and ROM match the previous checkpoint. All **60 runs** passed independent image checking and byte extraction.

| Workload | Previous buffered TRST | Updated TRST | Change | Original SFS |
| --- | ---: | ---: | ---: | ---: |
| 8 KiB append | 400.92 KiB/s | 400.06 KiB/s | −0.21% | 518.66 KiB/s |
| 64 KiB append | 2,050.96 KiB/s | 2,035.95 KiB/s | −0.73% | 3,326.34 KiB/s |
| 8 KiB existing-file overwrite | 250.29 KiB/s | 308.71 KiB/s | +23.34% | 2,452.29 KiB/s |

Overwrite Write latency improves from **28.17 / 31.26 ms** to **22.08 / 24.78 ms** at p50/p95. Journal and device I/O match in every paired trial. Per MiB, TRST still writes 1 / 1 / 2 KiB of journal data and issues 8 / 8 / 16 flushes for the three workloads.

Maximum sampled handler retention increases by **0.63 KiB**, from 237.13 to **237.76 KiB**, about 10.1% below original SFS's 264.4–264.6 KiB. This is sampled retention, not an allocation high-water mark. The full [benchmark report](../artifacts/native-trst-boundary/benchmark/report.md) includes both rebuilt CSUN policies, complete I/O counters and binary hashes.

Original SFS overwrites existing data in place, while TRST preserves old data through copy-on-write or staged redo. This comparison does not imply equivalent overwrite atomicity. The remaining gap includes metadata lookup/checksum work, copying and free-space calculation; the measured costs do not imply that removing one helper would save its entire attributed cost.

## Qualification

The host core suite passed **310 tests**, with one optional external-image test skipped. Compiled buffered and strict TRST passed **17 and 14 tests**, respectively, plus both five-scenario relocation suites. Both rebuilt CSUN policies passed their bulk-write crash suites. The SFS2 TRST handler passed **all 11 tests**, covering 63 sector/flush cut points in 30 representative cut/reboot groups. The three cycle profiles and all 60 benchmark trials also passed.

| Compiled recovery policy | Sector/flush cut points | Distinct durable images | Copper/original recovery boots | Additional interrupted-recovery cuts / boots |
| --- | ---: | ---: | ---: | ---: |
| Buffered default | 1,529 | 81 | 162 | 6 / 12 |
| Strict TRST | 1,486 | 76 | 152 | 6 / 12 |

Equivalent durable prefixes share a representative reboot; cut points are not all separate disk images. Compiled recovery tests use atomic sectors and reliable flushes under the emulator. They do not qualify torn-sector devices or physical hardware.

Ten new host crash cases exercise five layouts: all three records in one leaf, source/predecessor sharing, source/successor sharing, predecessor/successor sharing with decreasing physical keys in the file chain, and three distinct leaves. Each layout runs through strict and grouped TRST, cuts every sector/flush event, explores subsets of unflushed sectors, checks old-or-new file bytes and free-space counts, and preserves a neighboring file. The test platform expires metadata views at each lookup to detect stale-pointer use.

The [318-file source manifest](../artifacts/native-trst-boundary/source-prototype-v1.json) records the qualified snapshot. Only the native core's relocation implementation and the new host test file are changed from the preceding checkpoint. The portable core's public default remains separate from the standalone native handler policy.

The exact qualified files are integrated into `CopperStart/external/CopperSFS`. A fresh live standalone build produces the same **158,516-byte HUNK**, SHA-256 `100C231D7DDE59032578999BBFDD77F35EA5289ED125EAD9EF6ED2BEBCEC50A9`. The [integrated audit](../artifacts/native-trst-boundary/integrated-audit.json) verifies the source, binary identities, test receipts and unchanged I/O in every paired benchmark trial. Concurrent Exec changes remain untouched and are outside the standalone handler's reachable assemblies. All five native profiles pass allocation-free MC68000 compatibility checks.

## Rejected further shortcut

A second variant also reused the location returned by file traversal. It passed all 310 host tests, but its compiled overwrite run completed no Writes and issued no disk I/O before entering the error path. A bounded diagnostic reproduced that failure and saved the instruction profile.

The generated code at HUNK offset `0x214B6` uses `move.w d0,(a7)` for an assignment to a 32-bit length local, then reads that slot with `move.l (a7),d0`. On this big-endian target, the narrow store populates the high half of the local. The resulting invalid extent length explains the host/native disagreement. That variant is excluded; its [failed diagnostic](../artifacts/native-trst-boundary/v2-stall-diagnostic/tests.trx), [disassembly](../artifacts/native-trst-boundary/v2-relocate.asm), binary and source remain available for a compiler regression. This change does not modify the compiler.

The standalone native builds succeed. The required full `CopperStart.slnx` Release build still fails on the three existing CopperOS shell references to missing `LoadSegmentNative`, `UnloadSegmentNative` and `UnloadDosProcessSegment` APIs; see the [build log](../artifacts/native-trst-boundary/release-build.log). No release package or installed handler was changed.
