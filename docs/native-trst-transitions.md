# Native TRST allocation transitions

The native buffered handler now retains continuation state after a general relocation and searches forward when adjacent replacement space runs out. The five-trial benchmark improves overwrite throughput by **7.65%** over the [deferred-metadata build](native-trst-deferred.md), with identical I/O. Overwrite p95 latency improves from 7.85 to 6.34 ms and the worst Write from 73.60 to 57.77 ms. Append throughput is effectively unchanged.

Overwrite reaches **64.10% of original SFS 1.279 throughput**. The change is integrated and qualified, but the performance goal remains unmet. Whole-Write atomicity, buffered delayed durability, CSUN recovery and the shared **192 KiB staging cap** remain requirements.

## Changes grounded in the original-handler study

The [original study](native-trst-original-study.md) showed that original SFS retains file/extent state and avoids allocation on existing-file overwrites. Its older published allocation source also starts searches at the end of the current extent, or at a retained allocation cursor. That source is an implementation reference, not matching 1.279 C source; the exact binary and runtime traces establish the version-specific comparisons.

Our general relocation path previously discarded useful final record locations. The next Write cloned the accepted arena and ran allocation/relocation again before retaining a continuation hint. [RememberTrstRelocation](../../CopperStart/external/CopperSFS/src/CopperSFS.Core/AmigaSmartFileSystemRelocation.cs) now reacquires the final leaf after all insertions/removals, checks its layout and links, and records scalar fields and record locations when the relevant records share that leaf. It retains no borrowed image pointer. The next Write still validates the hint and proves durable replacement space before issuing data I/O. Other layouts use the existing fallback.

[FindTrstFreeRun](../../CopperStart/external/CopperSFS/src/CopperSFS.Core/AmigaSmartFileSystemTrstAllocation.cs) now searches forward from the preferred adjacent position. If no complete run is found there, it retains the full search from the beginning. All three allocation views, reservations, metadata targets and the publication marker remain excluded as before. This changes search order without adding a reservation or relaxing validation.

No persistent fields, arena layouts or staging allocations are added. Failures preserve the previously accepted arena. The on-disk journal format and durability policy are unchanged. The original's in-place payload overwrite remains unsuitable for our whole-Write atomicity requirement.

## Benchmark and tradeoffs

Five cold trials per handler and workload give 60 runs. Each writes 1 MiB on the same emulated 68020 and fixture, includes the final durability checkpoint, and passes deep image checking and exact-byte extraction. Timings use guest cycles with no added disk seek, rotation or flush latency. See the [benchmark](../artifacts/native-trst-transitions/benchmark-v2/report.md) and [comparison receipt](../artifacts/native-trst-transitions/metrics-v2.json).

| Workload | Previous TRST KiB/s | New TRST KiB/s | Change | Original KiB/s | Original throughput reached |
| --- | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 657.22 | 659.07 | +0.28% | 518.67 | 127.07% |
| 64 KiB append | 3,456.36 | 3,462.41 | +0.17% | 3,326.49 | 104.09% |
| 8 KiB existing-file overwrite | 1,464.74 | 1,576.73 | +7.65% | 2,459.80 | 64.10% |

The 45 original/CSUN control runs retain identical I/O and sampled RAM, with at most 0.301% cycle variation from the previous benchmark. The small append throughput changes fall within that control variation. CSUN performance controls remain the pinned previously qualified binaries; the rebuilt derivatives below receive fresh crash qualification. Their identities are recorded in `measurements.json`.

| Workload | Strict CSUN KiB/s | Buffered CSUN KiB/s |
| --- | ---: | ---: |
| 8 KiB append | 83.85 | 649.25 |
| 64 KiB append | 752.75 | 2,680.48 |
| 8 KiB existing-file overwrite | 44.61 | 44.45 |

| Workload | Write p50 ms, before → after | Write p95 ms, before → after | Worst Write ms, before → after | Checkpoint ms, before → after |
| --- | ---: | ---: | ---: | ---: |
| 8 KiB append | 11.163 → 10.989 | 13.711 → 13.690 | 35.213 → 32.870 | 31.537 → 32.995 |
| 64 KiB append | 11.435 → 11.714 | 35.702 → 33.403 | 35.702 → 33.403 | 31.911 → 32.099 |
| 8 KiB existing-file overwrite | 3.377 → 3.398 | 7.846 → 6.344 | 73.605 → 57.767 | 41.023 → 39.230 |

Median overwrite latency is slightly higher despite improved throughput and tail latency; append checkpoint latency also increases. These measurements do not attribute every small change to a single routine.

I/O matches the preceding handler in every workload. Overwrite writes 2,056 sectors in 136 requests, reads eight sectors in one request, publishes one 512-byte inline TRST page and uses four flushes. Original SFS writes 2,051 sectors in 131 requests with no reads, one inline page and four flushes. The extra native home metadata updates support replacement allocation/mapping.

Peak sampled native RAM increases by **784 bytes**, from 239.6875 to **240.453125 KiB**. The code hunk grows by 780 bytes; the complete HUNK file grows by 828 bytes to 161,032 bytes. Group/header/slot sizes stay at 220/24/8 bytes. Sampled handler RAM is separate from the 192 KiB staging limit. Original peaks at 264.41–264.59 KiB in these workloads.

## Profile and remaining work

The [packet analysis](../artifacts/native-trst-transitions/original-comparison-v2.json) compares a new diagnostic from this binary with the saved preceding-handler and original diagnostics. It sums disjoint packet intervals; overlapping dynamic parent/child timings are not added. The new diagnostic takes 4,606,154 cycles, versus 4,954,893 previously and 2,959,553 for original SFS.

General allocation/relocation calls fall from five to three, arena clones from four to two, and merged-bitmap loads from nine to three. The two eliminated generic packets are zero-based indices 1 and 64. Prototype v1 isolated the final-layout hint change; v2 adds forward search. Both frozen snapshots are retained, and only v2 is integrated.

| Packet path | Packets | New native cycles | Original cycles at those indices | Share of remaining excess |
| --- | ---: | ---: | ---: | ---: |
| Generic allocation/relocation | 3 | 907,013 | 64,883 | 51.14% |
| Proof construction | 3 | 234,518 | 60,138 | 10.59% |
| Run admission | 3 | 108,008 | 60,138 | 2.91% |
| Deferred continuation | 119 | 2,995,184 | 2,528,334 | 28.35% |
| Other work and checkpoint | — | 361,431 | 246,060 | 7.01% |

The remaining generic packets occur at indices 0, 22 and 63. Packets 0 and 63 still split an extent, remove the temporary prefix, then install its replacement. Moving the remaining source tail directly, where its validated layout permits, is the next substantial target. Packet 22 crosses a source bitmap boundary and still falls back to the general path. Both changes require renewed failure/rollback and compatibility qualification; neither is implemented here.

Typical deferred packets take 24,046 cycles versus 23,926 previously and 20,046 for original. Their private-state validation and cache invalidation costs also remain. The codec accounts for about **1.01%** of total cycles and is not the main remaining bottleneck.

## Qualification and integration

- Host semantics: **585 passed**, one optional external-image case skipped. The ten new cases cover first and interior relocations, expiring scratch views, replacement-window changes, every sector failure in the second Write, full retry, crash/persistence subsets and forward/wrap allocation.
- Compiled MC68000 instruction suite: **115 passed**. The 60-run benchmark and task-aware diagnostic pass image and byte checks.
- Default ROM/media tests: **18 main tests**, four continuation/allocation tests on each of two fixtures, and five relocation scenarios in one test passed. Buffered coverage includes **7,439 sector/flush cuts**, 119 durable images and **238 recovery boots** with CopperSFS and original SFS. Interrupted inline replay adds 6 cuts/12 boots; external replay adds 7 cuts/14 boots.
- Rebuilt strict TRST: 15 main tests and five relocation scenarios passed, covering 1,479 cut points, 75 durable images and 150 recovery boots, plus the same interrupted-replay coverage.
- Both rebuilt CSUN variants passed: strict covers 161 cuts/9 durable images/18 boots; buffered covers 152 cuts/5 images/10 boots. CopperSFS replays CSUN before original SFS receives the clean recovered volume. The default suite also passes legacy CSUN recovery.
- Rebuilt SFS2 TRST: **11 passed**, with 63 sector/flush cuts in 30 actual cut/reboot groups. Sparse wide-device checks inspect affected structures rather than the entire allocation graph.

Native crash cuts assume atomic sectors and reliable flushes and group identical durable prefixes. Host tests also enumerate arbitrary pending-sector subsets. These are emulator/device-model results, not physical power-loss measurements. All derivative binaries changed, so their qualification was rerun.

The [integrated audit](../artifacts/native-trst-transitions/integrated-audit-v2.json) verifies 342 frozen source files, compiler inputs, all binary/test identities, benchmark and original-comparison receipts, and 52 reference-source hashes. It verifies preservation of nine concurrent Exec source changes. The native compatibility reports contain no reachable managed allocation sites or Exec assembly dependency.

Only the two implementation files and the new test file were integrated. The standalone live build reproduces the exact qualified HUNK:

`02395F82B59FCB569781F439DB6E4F42B1D688A34A797899C178A16A3F3B8B99`

The full `dotnet build CopperStart.slnx -c Release` retains the same three pre-existing CopperOS shell errors: `LoadSegmentNative`, `UnloadSegmentNative` and `UnloadDosProcessSegment`. The standalone handler build passes. This updates the native development default; it does not release/install a binary or sync the public portable core.
