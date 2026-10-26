# Native TRST validation cache

Buffered TRST improves existing-file overwrite throughput by **12.2%** over the [continuation checkpoint](native-trst-continuation.md). Append throughput changes by **-0.08%** for 8 KiB packets and **+0.03%** for 64 KiB packets. Overwrites reach **34.4% of original SFS throughput**. Overall throughput parity remains unmet.

The **192 KiB shared staging cap**, whole-write atomicity, delayed durability and legacy CSUN recovery remain intact. The journal encoding and publication order are unchanged.

## Change and validity

A 76-byte private record caches validation results for consecutive aligned overwrites within one unchanged arena: a bounded source/destination window, owned slot indices, leaf record indices and layout, bitmap geometry, original free-space count and slot count. A checksum detects damaged cache fields. It stores scalar indices, never borrowed platform pointers. The group header grows from 124 to 200 bytes within the existing budget; initial 512-byte-block staging grows from 8,620 to 8,696 bytes. At the full shared budget, each of the two arenas still holds 186 slots. The single-arena path still holds 374 slots.

The first eligible Write verifies the original free-space window, the extent leaf and a conservative journal-space bound. The window stops at bitmap and physical boundaries, staged block targets, the transaction marker and blocks still allocated in the durable view. Later Writes reuse this immutable result only for the same arena, slot count and original free-space snapshot.

Every Write still validates its request, file protection and bounds, owned slot identities, current leaf layout and record indices, actual extent keys, lengths and back-links, and current source/destination allocation bits. Boundary crossings, a changed arena or slot set, uncertain journal space, and destinations beyond the proven window use the generic path. Failed Writes, generic fallback and commits invalidate the cache.

Data remains unpublished until successful transfer. A failed transfer leaves every accepted arena byte unchanged. Afterwards the existing no-fail update phase changes only already-owned metadata, allocation bits and incremental checksums. Extracting that phase also reduces live values in the main function. The compiled main continuation has **10 spilled values instead of 140** and a **44-byte frame instead of 120 bytes**; these figures exclude the separate helper frames.

The compiled overwrite profile falls from **9,649,464 to 8,599,333 cycles**, a **10.9% cycle reduction**. It retains 120 successful continuations, builds the proof four times, and avoids repeated durable-bitmap reads, bitmap-position divisions and slot searches. Copying and device I/O remain unchanged. See the [paired profiles](../artifacts/native-trst-proof-cache/comparison.json).

## Benchmark

Five cold trials per cell write 1 MiB on the same emulated 68020 at 7,093,790 cycles/s, using 512-byte blocks, 32 buffers, 512 KiB chip RAM, 512 KiB slow RAM and 2 MiB fast RAM. Copper handlers target MC68000; original SFS requires 68020. No device seek or flush latency is added. All **60 trials** pass independent image checks and exact-byte extraction. The benchmark driver, emulator, fixture, ROM and original handler are unchanged.

Durable throughput includes open, Writes, Close and the final successful checkpoint, in KiB/s:

| Workload | Previous TRST | Updated TRST | Change | Strict CSUN | Buffered CSUN | Original SFS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 618.33 | 617.85 | -0.08% | 80.63 | 600.38 | 518.67 |
| 64 KiB append | 3242.25 | 3243.28 | +0.03% | 718.77 | 2519.96 | 3326.49 |
| 8 KiB existing-file overwrite | 752.80 | 844.71 | +12.21% | 42.54 | 42.44 | 2452.40 |

TRST Write latency, sampled memory and journal traffic:

| Workload | p50 ms | p95 ms | Maximum sampled RAM KiB | Journal writes KiB | Flushes |
| --- | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 11.849 | 14.398 | 248.88 | 1.0 | 8 |
| 64 KiB append | 12.372 | 27.291 | 248.88 | 1.0 | 8 |
| 8 KiB existing-file overwrite | 5.865 | 14.101 | 248.88 | 2.0 | 16 |

Every paired trial has identical I/O counters. Maximum sampled RAM is **about 5.9% below original SFS**. This measures retained allocations at probe checkpoints, not the allocation high-water mark. The HUNK grows by 1,376 bytes. The [full benchmark](../artifacts/native-trst-proof-cache/benchmark/report.md) and [paired metrics](../artifacts/native-trst-proof-cache/metrics.json) retain all policies, latency and memory measurements.

Original SFS updates existing data in place; CopperSFS also allocates replacement data and publishes metadata to preserve whole-write atomicity. That difference does not account for the entire remaining gap. The [remaining profile](../artifacts/native-trst-proof-cache/remaining-costs.json) attributes time across continuation validation, block copying, data transfer, write dispatch, cache invalidation and metadata work. Writes 64 and 128 are the slowest at 109.6 and 182.5 ms in the diagnostic run; together they account for 24.1% of measured cycles. [Per-Write entry counts](../artifacts/native-trst-proof-cache/write-boundaries.json) show that both invoke strict fallback after attempting buffered staging, flush the accepted group and execute the current Write separately. The precise staging rejection and the extra publication work merit separate profiling before the next optimization.

## Qualification

The final frozen sources pass **404 host tests**, with one optional external-image test skipped, plus **80 compiled MC68000 buffer/codec tests**. Twenty-six new host cases cover cached reuse with the durable bitmap made unreadable, each corrupted cache field, changed slot sets and journal bounds, retired but durably reachable destinations, all 16 failed-data-sector positions for both view providers, unchanged accepted arena bytes on failure, retries, remounts and reordered pending writes. The existing allocation oracle and continuation boundary tests remain in the passing suite.

Two compiled tests issue five consecutive Writes at 8 KiB and 64 KiB, check pending reads before Close, then independently check all 3 MiB of the file after recovery. The maximum-packet case crosses a source bitmap boundary before later continuations. Together they cover **743 sector/flush cut points**, grouped into **10 durable images** and **20 Copper/original recovery boots**, plus four clean Close handoffs. Both handlers agree on a wholly old or wholly updated file. Receipts: [8 KiB](../artifacts/native-trst-proof-cache/continuation-native/native-trst-continuation-8192-cuts.json), [64 KiB](../artifacts/native-trst-proof-cache/continuation-native/native-trst-continuation-65536-cuts.json).

Buffered and strict TRST pass **17 and 14 native tests**, plus both five-scenario relocation suites. Both CSUN policies pass bulk-write crash suites. All **11 SFS2 tests** pass, including **63 sector/flush cuts in 30 representative cut/reboot groups**. The two continuation tests are included in the buffered totals below:

| Compiled policy | Sector/flush cuts | Distinct durable images | Copper/original recovery boots | Interrupted-recovery cuts / boots |
| --- | ---: | ---: | ---: | ---: |
| Buffered default | 2,272 | 91 | 182 | 6 / 12 |
| Strict TRST | 1,486 | 76 | 152 | 6 / 12 |

Equivalent durable prefixes share representative recovery boots. These compiled tests assume atomic sectors and reliable flushes; they do not qualify physical power loss or torn sectors. Host tests separately exercise reordered pending writes.

## Integration

Eight implementation/test files are integrated into CopperStart's embedded core and native tests. The [integrated audit](../artifacts/native-trst-proof-cache/integrated-audit.json) verifies **326 frozen source files**, the unchanged **181-file compiler snapshot**, exact HUNK identities, all qualification receipts and unrelated shared-handler changes. All five native profiles pass allocation-free MC68000 compatibility checks. A fresh standalone build reproduces the **169,952-byte HUNK**, SHA-256 `2BA48297BE6399867183A824DB446CBCDFFB546CAB7F9E6AFCFCCAEE4028CDC9`.

Buffered TRST remains the default: successful Writes are visible immediately and become durable on successful filesystem `ACTION_FLUSH`, writer Close, Inhibit or timer commit. Power loss before commit may lose acknowledged Writes. `-StrictWrites` retains per-Write durability.

The required full solution build result is **three pre-existing missing CopperOS shell APIs**; see the [build log](../artifacts/native-trst-proof-cache/release-build.log). Standalone builds pass. No installation, package release, commit or public-core synchronization was performed.
