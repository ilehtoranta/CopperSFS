# Native TRST deferred metadata runs

This checkpoint is superseded by the [allocation-transition optimization](native-trst-transitions.md). The measurements and qualification below describe the deferred-metadata build identified here.

The native buffered handler now retains a bounded description of consecutive overwrite progress and applies the corresponding extent and bitmap edits together when metadata is needed. Overwrite throughput improves **13.79%** over the [pending-entry build](native-trst-pending.md). The change follows the [original SFS implementation study](native-trst-original-study.md), which identified repeated per-Write work as the main remaining cost.

This is a measured tradeoff: append throughput decreases 1.82–2.41%, and worst-Write and checkpoint latency increase. The new handler still exceeds original SFS throughput on both append workloads, but overwrite reaches only **59.73%** of original. The parity goal remains unmet.

## Implementation and guarantees

[AmigaSmartFileSystemTrstDeferred.cs](../../CopperStart/external/CopperSFS/src/CopperSFS.Core/AmigaSmartFileSystemTrstDeferred.cs) adds a 20-byte descriptor to the group header, increasing it from 200 to 220 bytes. The two staging arenas retain their existing 24-byte headers and 8-byte slot headers, within the same **192 KiB shared staging budget**. No payload is held in the descriptor and no per-Write allocation is introduced.

Admission checks the existing continuation proof and the complete bounded window of allocated source blocks and free replacement blocks. The durable allocation proof excludes blocks that would endanger the old on-disk view. Each successful unpublished payload transfer advances only the accepted block count and its inverse. A failed transfer preserves the previously accepted descriptor and arena byte-for-byte.

The descriptor records its owning arena, file limit and admitted range. Private group and arena geometry, ownership, mapping, extent/proof state, request eligibility and protection remain checked. A position-weighted seal detects changes to the retained group/proof fields; it is an integrity check, not authentication. The accepted count/inverse are checked separately. Owned images stay in their arena; no borrowed metadata pointer survives another cache or I/O operation.

Metadata reads and edits, raw overlay lookup, arena clone/store, full group writes and commit first materialize the accepted progress. Materialization validates the affected owned slots, extent links and allocation bits before applying bounded memory-only edits. Reads therefore see every accepted Write, and a later failed Write cannot discard earlier accepted data. An ineligible request materializes the prefix and falls back to the existing path.

`TryContinueTrstGroup` opts into this behavior through `deferUpdates`; its default remains false for existing callers. Only the buffered standalone native dispatch enables it. Overlay readers use `TryFind`, which distinguishes a missing image from failed materialization, preventing an error from falling through to an old disk image. Other metadata consumers must observe the same arena access boundaries.

Whole-Write atomicity, delayed durability on Flush/Close/Inhibit/timer commit, and recovery of existing CSUN volumes are preserved. The on-disk TRST format does not change. Original SFS's in-place payload overwrites are not adopted because they do not provide the required whole-Write payload atomicity.

## Benchmark

Five cold trials per handler and workload produce 60 runs, each writing 1 MiB on the same emulated 68020 and fixture. Throughput includes the final durability checkpoint. Every result passes independent deep image checking and exact-byte extraction. Timings derive from guest cycles with no added disk seek, rotation or flush latency; they are not physical-device measurements. See the [benchmark report](../artifacts/native-trst-deferred/benchmark-v4/report.md) and [comparison receipt](../artifacts/native-trst-deferred/metrics-v4.json).

| Workload | Previous TRST KiB/s | New TRST KiB/s | Change | Original KiB/s | Original throughput reached |
| --- | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 673.46 | 657.22 | −2.41% | 518.67 | 126.71% |
| 64 KiB append | 3,520.50 | 3,456.36 | −1.82% | 3,326.51 | 103.90% |
| 8 KiB existing-file overwrite | 1,287.21 | 1,464.74 | +13.79% | 2,452.40 | 59.73% |

The strict and buffered CSUN benchmark controls are the pinned previously qualified binaries, identified in `measurements.json`. The newly rebuilt CSUN variants below receive fresh crash qualification but do not replace these performance controls. All 45 original/CSUN control runs retain identical I/O and sampled RAM, with at most 0.0213% cycle variation from the previous benchmark.

| Workload | Strict CSUN KiB/s | Buffered CSUN KiB/s |
| --- | ---: | ---: |
| 8 KiB append | 83.87 | 649.25 |
| 64 KiB append | 752.77 | 2,680.70 |
| 8 KiB existing-file overwrite | 44.61 | 44.45 |

| Workload | Write p50 ms, before → after | Write p95 ms, before → after | Worst Write ms, before → after | Checkpoint ms, before → after |
| --- | ---: | ---: | ---: | ---: |
| 8 KiB append | 10.905 → 11.163 | 13.451 → 13.711 | 32.768 → 35.213 | 30.759 → 31.537 |
| 64 KiB append | 11.504 → 11.435 | 33.326 → 35.702 | 33.326 → 35.702 | 30.757 → 31.911 |
| 8 KiB existing-file overwrite | 4.340 → 3.377 | 8.147 → 7.846 | 68.424 → 73.605 | 34.091 → 41.023 |

I/O is identical to the previous handler in every workload. For overwrite, both native TRST and original SFS publish one 512-byte inline TRST page and use four flushes. Native TRST writes 2,056 sectors in 136 requests and reads eight sectors in one request; original writes 2,051 sectors in 131 requests and reads none. The five extra native writes update replacement allocation/mapping metadata. The codec is not the main cause of the throughput difference.

Peak sampled native handler RAM rises from 236.234375 to **239.6875 KiB**, a 3,536-byte increase comprising 3,516 bytes of code and the 20-byte descriptor. Original SFS peaks at 264.41–264.59 KiB in these workloads. The HUNK file increases by 4,168 bytes to 160,204 bytes; file size includes relocation data and differs from resident code size. Sampled handler RAM is distinct from the 192 KiB staging limit.

## Remaining difference from original SFS

The [packet comparison](../artifacts/native-trst-deferred/original-comparison-v4.json) compares a new diagnostic from this exact binary with saved diagnostics from the preceding handler and original SFS 1.279. All three use the same fixture and packet sequence. These are diagnostic runs, separate from the five-trial benchmark above. The script sums disjoint packet intervals rather than overlapping parent/child call timings.

| Packet path | Packets | New native cycles | Original cycles at those indices | Share of remaining total excess |
| --- | ---: | ---: | ---: | ---: |
| Generic allocation/relocation | 5 | 1,325,815 | 104,975 | 61.18% |
| Proof construction | 3 | 233,598 | 60,138 | 8.69% |
| Run admission | 3 | 107,298 | 60,138 | 2.36% |
| Deferred continuation | 117 | 2,918,902 | 2,488,242 | 21.58% |
| Other work and checkpoint | — | 369,280 | 246,060 | 6.18% |

The complete diagnostic takes 4,954,893 cycles versus 5,642,264 previously and 2,959,553 for original SFS. Typical deferred packets fall from 30,366 to 23,926 cycles; original takes 20,046 at the corresponding indices. Encoder instructions account for 0.94% of the new total. No block-copy call occurs in the 117 deferred packets; the five generic packets contain 47 such calls.

The generic transitions remain at zero-based packet indices 0, 1, 22, 63 and 64. Our relocation code only retains a continuation hint in its existing-run extension branch; the general insertion path leaves the following Write to rediscover that state. At a bitmap boundary, proof construction also declines when the required map is absent from the active arena, causing a full generic operation. Touched-map free-space accounting repeats at packets 63, 64 and 65. These are concrete next targets for retaining validated allocation state with explicit lifetimes. Original SFS avoids allocation entirely on this overwrite workload, while still performing one extent lookup per Write.

The [original study](native-trst-original-study.md) distinguishes the older published SFS-derived C source from the exact 1.279 binary. Matching 1.279 C source was not located. Version-specific behavior is established by disassembly and executed calls/sector writes, not assumed from the older source.

## Qualification and integration

- Host semantics: **575 passed**, one optional external-image test skipped. The 47 added cases cover deferred reads, mutation/clone/commit boundaries, retry after each sector failure, changed requests, private-state corruption, future allocation conflicts, crash cuts and arbitrary pending-sector persistence subsets.
- Compiled MC68000 instruction suite: **115 passed**. The benchmark, standard profile and task-aware diagnostic also pass exact-byte and deep-image checks.
- Default ROM/media suite: **18 main tests**, four continuation/allocation tests on each of two fixtures, and one relocation test covering five scenarios all passed.
- Buffered crash coverage: **7,439 sector/flush cut points**, grouped into 119 durable images, recovered in **238 boots** using CopperSFS and original SFS. Interrupted inline replay adds 6 cuts/12 boots; external replay adds 7 cuts/14 boots.
- Strict TRST was rebuilt and rerun: 15 main tests plus five relocation scenarios passed, covering 1,479 cut points, 75 durable images and 150 recovery boots, plus the same interrupted-replay coverage.
- Both rebuilt CSUN variants passed their compiled crash suite: strict covers 161 cuts/9 durable images/18 boots; buffered covers 152 cuts/5 images/10 boots. CopperSFS first replays CSUN; original SFS then receives the clean recovered image. Original SFS does not replay CSUN itself. The default TRST suite also tests legacy CSUN recovery.
- Rebuilt SFS2 TRST: **11 passed**, covering 63 sector/flush cuts in 30 actual cut/reboot groups against the original and native reference images.

Native crash cuts assume atomic sectors and reliable flushes and group identical durable prefixes. Host tests additionally enumerate arbitrary pending-sector subsets. This is emulator/device-model qualification, not physical power-loss testing. Sparse wide-device SFS2 checks inspect the affected structures rather than the entire allocation graph.

All derivative binaries changed, so their qualification was rerun rather than inherited by identity. The [integrated audit](../artifacts/native-trst-deferred/integrated-audit-v4.json) verifies 341 frozen source files, compiler inputs, all binary/test identities, the 60-run benchmark and preservation of nine concurrent Exec source changes. The standalone build contains no reachable managed allocation sites or Exec assembly dependency.

The live native development build reproduces the exact qualified 160,204-byte HUNK:

`DAE8EBD105D7C00935E4CB140F7645D58F932DEA0A498CBCDFD4503BA3FB8741`

Only the 13 qualified implementation/test files were integrated. The portable public core and published binaries are not updated by this change. The standalone build passes. The full `dotnet build CopperStart.slnx -c Release` still fails on the same three pre-existing CopperOS shell API mismatches: `LoadSegmentNative`, `UnloadSegmentNative` and `UnloadDosProcessSegment`; the audit compares the exact errors with the preceding build.

Reproduction uses the saved [benchmark launcher](../artifacts/native-trst-deferred/Benchmark-v4.ps1), [default qualification launcher](../artifacts/native-trst-deferred/Qualify-v4.ps1), [variant launcher](../artifacts/native-trst-deferred/Qualify-Variants-v4.ps1), [packet analysis](../artifacts/native-trst-deferred/compare_original_v4.py) and [audit](../artifacts/native-trst-deferred/audit_v4.py). Paths to licensed ROMs and local fixtures remain local artifacts.
