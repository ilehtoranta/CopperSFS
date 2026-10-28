# TRST encoding and block copies — 2026-10-25

Follow-up: [native TRST unpublished data, retained views and run encoding](native-trst-parity.md) measures the subsequent native optimization and its remaining gap to original SFS. This page records the preceding codec baseline.

Single-pass run planning and faster native copies raise compiled TRST's 64 KiB append throughput from **33.56 to 44.43 KiB/s (+32.4%)**. The 8 KiB append and overwrite workloads improve **25.5%** and **24.8%**. Journal I/O, flush counts, the **192 KiB staging allocation**, its **377 slots**, and the whole-write commit protocol are preserved.

The baseline is the [previous qualified staging-index build](native-trst-index.md). These are compiled MC68000 handlers measured using emulated guest cycles. The same original SFS handler and independently formatted fixture are used in both runs.

## Implementation and memory

`AmigaSmartFileSystemTrstCodec.Plan` reads each original/final longword once, classifies it as unchanged, zero, all-ones or literal, and records one byte per run. Runs still contain at most 64 longwords. Planning returns the exact payload length required by the native record header; emission reads the saved codes and copies literal spans from the retained final image. The former encoder classified the same words in separate sizing and emission passes.

The plan occupies at most `BlockBytes / 4` bytes inside the existing `Work.Bitmap` scratch block: 128 bytes for a 512-byte block and 8 KiB for the largest supported 32 KiB block. It does not require another allocation, enlarged scratch buffer or staging slot. Journal-page allocation uses `Work.Meta`, so crossing a page boundary cannot overwrite the plan. Literal data stays in the arena. Public and embedded cores contain the same planner.

Native `CopyBlock` now copies eight longwords per iteration for compatible address parity. Matching odd addresses peel one byte first; MC68000 longword accesses require even addresses, including addresses two bytes past a four-byte boundary. Opposite-parity copies use unrolled byte accesses. Bounded tails avoid reading or writing past the supplied span. Public forward-overlap behavior and the private overlap/wrap fallback remain intact. DMA restrictions and device I/O rules are unchanged.

The indexed arena remains 196,608 bytes, with a 24-byte header, a 2 KiB index at 512-byte blocks and 377 data/metadata slots. Oversized operations still fail before publication. The planner and native copy roots allocate no managed memory. Maximum sampled guest retention is **491.41 KiB**, up **216 bytes** from 491.20 KiB; the HUNK also grows by 216 bytes. These samples include code, stack, caches and arena, and are not a transient allocation high-water mark.

The commit sequence still flushes the complete journal, publishes and flushes TRFA, applies retained final images, flushes home writes, and writes and flushes TROK. Recovery validates the complete journal before applying it and supports interrupted recovery. Legacy CSUN recovery remains available. A successful Write is durable; a published operation interrupted by failure recovers the complete new state. Requests are never split to fit the arena.

Classic CSEB/version 12, SFS2 CSEC/version 13, StateBytes 2320 and both private arena layouts are unchanged. SFS2 retains its existing encoder; it shares the native copy helper. The frozen snapshot includes the coordinated `CopperSharpNativeDosPlatform.RecordLocks.cs` capability-selection update. That work's handler record engine, state and dispatch remain unchanged; its API qualification is separate. Benchmark comparisons therefore describe the combined qualified snapshots.

## Performance

Five cold trials per handler/workload write 1 MiB each. Durable throughput includes open, Write packets, close and the final successful Inhibit. All 60 trials pass independent deep image checking and exact-byte extraction, including the untouched 2 MiB tail in the existing-file workload.

| Workload | Previous TRST KiB/s | Current TRST KiB/s | Improvement | Strict CSUN | Buffered CSUN | Original SFS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 22.01 | **27.62** | **25.5%** | 73.90 | 513.12 | 516.89 |
| 64 KiB append | 33.56 | **44.43** | **32.4%** | 624.83 | 1787.07 | 3249.32 |
| 8 KiB overwrite | 25.30 | **31.58** | **24.8%** | 38.87 | 38.74 | 2419.13 |

| TRST workload | Write p50 before → after, ms | Write p95 before → after, ms |
| --- | ---: | ---: |
| 8 KiB append | 367.22 → **292.24** | 369.81 → **296.26** |
| 64 KiB append | 1885.20 → **1419.65** | 1965.92 → **1499.28** |
| 8 KiB overwrite | 314.81 → **253.83** | 319.91 → **257.13** |

Every handler/workload retains the previous trial sets of read/write sector counts, journal pages/images/descriptors, journal reads, marker writes, flushes and device write requests. The improvement comes from CPU work. Strict CSUN improves 3.3–5.5%; buffered CSUN improves 1.4–5.6% with the shared copy change and rebuilt code. The unchanged original handler stays within 0.15% of its prior throughput.

The 64 KiB diagnostic profile drops from **216,420,728 to 163,504,778 cycles (24.45%)**. Exclusive encoding cycles fall from **63,937,866** in `TrstEncode` to **18,831,945** in `Plan` plus `TrstEmit`, a **70.55% reduction**. Literal-copy helpers are outside this encoding subtotal in both profiles.

| Compiled aligned-copy probe | Previous cycles | Current cycles | Reduction |
| --- | ---: | ---: | ---: |
| 512 bytes | 11,068 | 6,274 | **43.3%** |
| 4096 bytes | 80,956 | 41,666 | **48.5%** |

The copy comparison uses the same current instruction-test assembly and dependencies, substituting only the previous qualified `CopperStart.Dos.dll` for its baseline. Both runs verify bytes and bounds. Its separate Exec comparison is an instruction-only test service, not a measurement of Kickstart's copy-vector speed.

In the full profile, `CopyBlock` exclusive cycles increase from 42.04 to 46.62 million because more unaligned copying now executes there. The outside-handler bucket falls from 21.02 to 8.93 million. Those buckets have changed responsibilities, so their individual percentages do not establish a copy regression. `CopyBlock` is now the largest exclusive handler cost, at 28.5% of measured cycles; further copy/compiler work remains worth investigating.

TRST remains substantially slower than original SFS. Whole-data journaling and per-Write durability differ from the original handler's metadata journal and buffered acknowledgements. The benchmark uses the common final clean checkpoint, but it does not make those write guarantees identical. The emulator adds no seek, rotation or flush delay, and the payload is the fixed nonzero-word pattern documented in the complete tables below.

## Qualification

| Check | Result |
| --- | --- |
| Compiled classic TRST | 12 passed, no skips; native cuts, original replay, legacy CSUN migration, capacity/reuse, shared/concurrent handles and A500 integration |
| Compiled strict and buffered CSUN | Both 64 KiB crash tests passed; Copper recovery followed by original SFS reading the clean volume |
| Portable original SFS replay | Passed: 36 distinct dirty-image boots across seven mutation types |
| Compiled buffer and codec tests | 58 passed, no skips |
| Compiled SFS2 TRST compatibility | 11 passed, no skips; 68000/68020 mutation/remount, original redo, arena reuse and wide-device operations |
| Public core | 378 passed, 5 external-input skips; original-handler TRST replay run separately |
| Embedded core | 280 passed, 1 optional external-image skip |
| Compiled benchmark | 60 checked trials passed |
| Diagnostic profile | Passed |
| CopperStart Release solution | Build passed, 0 errors |

Classic TRST receipts cover **902 writer sector/flush cuts plus 6 interrupted-recovery cuts**, represented by **53 distinct cut images and 106 recovery boots** across Copper and original SFS. The 64 KiB and 128 KiB cases account for 283 and 548 points. Strict CSUN covers 161 points in nine images; buffered CSUN covers 152 points in five images. Original SFS directly replays TRST; its CSUN check follows Copper recovery.

SFS2 covers **63 sector/flush cut points in 30 actual cut/reboot groups**. Its sparse wide-device checks inspect affected structures rather than the complete allocation graph. This rerun qualifies the shared copy change while preserving SFS2's existing encoder and arena layout.

The new portable tests cover all four compression modes, mixed and random patterns, exact 63/64/65-word boundaries, five supported block sizes through 32 KiB, immutable inputs, plan bounds and exactly one read of each original/final longword. Compiled planner cases cover 512/4096/32768-byte blocks. Compiled copies cover every source/destination alignment modulo four, unrolled-loop boundaries, odd lengths, canaries and existing overlap behavior. The wider buffer suite retains its block I/O, DMA and wide-offset checks.

Crash results assume atomic sectors and reliable flushes. Equivalent durable prefixes share representative cut/reboot runs; the receipts retain every covered cut point. These checks qualify compiled HUNKs and native integration, without claiming a new extension-ROM/media replay or physical-device power-loss test.

## Reproducible evidence

Local artifacts are under `artifacts/native-trst-codec/`. The [source manifest](../artifacts/native-trst-codec/source-v1.json) pins 293 native production/build/test inputs in the sibling CopperStart `artifacts/native-trst-codec/source-v1` snapshot. `public-source.json` pins the public core. [Comparison data](../artifacts/native-trst-codec/comparison.json) contains all before/after measurements and the isolated copy-probe results.

The [final audit](../artifacts/native-trst-codec/final-audit.json) passed: snapshot hashes match live sources, public/embedded planner and test sources agree, benchmark/crash/profile binary identities match, every selected native test passes without skips, and all trial I/O sets match the prior build. The classic profile has no entries for the former encoder, healthy recovery replay or legacy linear arena lookup. The audit also verifies that the copy microbenchmark changes only the DOS assembly and that benchmark fixture, original archive, ROM, machine and protocol match the baseline.

Use the frozen snapshot's `build/Build-SfsHandler.ps1` with an explicit `CopperSharpRoot`. Classic TRST is the default; the CSUN comparison builds use `-LegacyCsUn` with or without `-StrictWrites`, and SFS2 uses `-Sfs2 -Sfs2TrstWrites`. Qualify with `scripts/Test-NativeTrst.ps1`, `scripts/Test-TrstOriginalHandler.ps1` and `scripts/Test-NativeSfs2.ps1`; benchmark with `scripts/Measure-NativeWritePolicies.ps1 -Trials 5`, supplying explicit handler, test-assembly and fixture paths and fresh output directories. `scripts/Summarize-NativeWritePolicies.py` generates the complete tables below.

The native test assembly SHA-256 is `E9F21D7C3522AECD417D362A4EBAC2A2408B30A0309E11E5EB8D38CF0B5626CD`. The classic HUNK hashes are listed in the complete benchmark. TRST remains the native development default. Generated binaries and licensed media remain outside version control; no installation or release was performed.

The separately qualified SFS2 TRST HUNK is 197,224 bytes, SHA-256 `3B575EFB2D7C6CF378261B5D093E6893CE3FC0C8955F5B60DBC7C8EED49AD618`.

## Complete final benchmark

5 cold trials per cell, 60 checked runs. Each writes 1 MiB; every output passed amidisk deep checking and exact-byte extraction.

Rates and latencies use emulated PAL time (7,093,790 cycles/s), not host wall time. All binaries run on the same emulated 68020, 512 KiB chip + 512 KiB slow + 2 MiB fast RAM, 512-byte blocks and 32 buffers. The device adds no seek, rotation or flush delay. Boot and validation are excluded. Four independent machines run concurrently; timing comes from each guest's own cycle counter.

Each fresh machine mounts and verifies the seed Hello file before the measured interval. Append workloads create a 1 MiB file. The overwrite workload replaces the first 1 MiB of the fixture's 3 MiB file; this range fits within its first extent. Independent extraction also checks that its remaining 2 MiB are unchanged. The raw workload tag retains the seeded file's name, Fragmented; this is an existing-file overwrite measurement. Payload byte i is (i * 17 + 5) & 255, so results concern this nonzero-word pattern rather than zero-filled or varied-compressibility data.

## Throughput and Write latency

Durable throughput includes file open, all writes, Close and successful Inhibit. Service throughput uses only the summed DOS.Write intervals. Latency p50/p95 pools every Write call across trials (nearest-rank p95); all other figures are medians across trials. Fixed marker overhead is included.

| Workload | Handler | Durable KiB/s | Write service KiB/s | Write p50 ms | Write p95 ms | Write max ms | Final checkpoint ms |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| append-8k | TRST | 27.62 | 27.69 | 292.244 | 296.263 | 339.400 | 7.984 |
| append-8k | CSUN-strict | 73.90 | 74.44 | 107.047 | 108.542 | 146.200 | 7.990 |
| append-8k | CSUN-buffered | 513.12 | 570.04 | 12.192 | 15.304 | 84.030 | 140.812 |
| append-8k | Original | 516.89 | 533.01 | 14.699 | 16.932 | 25.859 | 35.228 |
| append-64k | TRST | 44.43 | 44.60 | 1419.651 | 1499.276 | 1499.276 | 7.983 |
| append-64k | CSUN-strict | 624.83 | 664.42 | 93.587 | 151.338 | 151.338 | 7.346 |
| append-64k | CSUN-buffered | 1787.07 | 2378.09 | 14.653 | 96.678 | 96.678 | 85.835 |
| append-64k | Original | 3249.32 | 3924.86 | 14.884 | 26.001 | 26.001 | 31.420 |
| existing-file-overwrite-8k | TRST | 31.58 | 31.59 | 253.831 | 257.126 | 258.387 | 7.339 |
| existing-file-overwrite-8k | CSUN-strict | 38.87 | 38.90 | 195.840 | 201.096 | 582.291 | 7.346 |
| existing-file-overwrite-8k | CSUN-buffered | 38.74 | 38.77 | 197.139 | 201.658 | 583.983 | 7.375 |
| existing-file-overwrite-8k | Original | 2419.13 | 2638.72 | 2.828 | 4.012 | 6.764 | 24.989 |

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
| append-8k | TRST | 299.15 | 491.41 | 491.41 | 491.15 |
| append-8k | CSUN-strict | 306.27 | 306.53 | 306.53 | 306.27 |
| append-8k | CSUN-buffered | 355.09 | 355.36 | 355.36 | 355.09 |
| append-8k | Original | 248.37 | 264.59 | 264.59 | 264.10 |
| append-64k | TRST | 299.15 | 491.41 | 491.41 | 491.15 |
| append-64k | CSUN-strict | 306.27 | 306.53 | 306.53 | 306.27 |
| append-64k | CSUN-buffered | 355.09 | 355.36 | 355.36 | 355.09 |
| append-64k | Original | 248.37 | 264.59 | 264.59 | 264.10 |
| existing-file-overwrite-8k | TRST | 299.15 | 491.41 | 491.41 | 491.15 |
| existing-file-overwrite-8k | CSUN-strict | 306.27 | 370.53 | 370.53 | 370.27 |
| existing-file-overwrite-8k | CSUN-buffered | 355.09 | 419.36 | 419.36 | 419.09 |
| existing-file-overwrite-8k | Original | 248.37 | 248.55 | 264.41 | 264.10 |

## Binary identity and interpretation

These results apply to the exact binaries listed below. Source snapshots and crash qualification must be matched to those hashes; this report does not infer qualification or ABI versions from handler names. Copper binaries target 68000; original SFS 1.279 requires 68020. All use identical guest hardware and filesystem fixtures.

TRST and strict CSUN make successful Write packets durable. Buffered CSUN and original SFS can acknowledge pending writes; their short Write-return latency is a different durability contract. The successful final Inhibit brings all four to a clean checkpoint. TRST journals file data for whole-write atomicity; original SFS's metadata journal has different data guarantees.

| Handler | HUNK bytes | SHA-256 |
| --- | ---: | --- |
| TRST | 144740 | `AE4A692BB8A10B01E5AEB50A262287AECA7336C26F43E0ABE44284F74960D2F5` |
| CSUN-strict | 151516 | `96369C7D217724D7A63BB80EDCD0F42B66D24AACEB9E0C94280C0B45FFB3E35F` |
| CSUN-buffered | 151736 | `003FD6E895290C69B091CBC370819250970984108A98EBD76FE99EBE3FBDF301` |
| Original | 97984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |
