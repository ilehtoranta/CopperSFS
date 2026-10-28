# Native TRST occupied-word scanning

The subsequent [metadata-view and record-hint optimization](native-trst-searchviews.md) adds another 8.7–11.8% throughput, with renewed crash and original-handler qualification. Measurements below describe this earlier checkpoint.

The native buffered TRST handler gains **2.8% for 8 KiB appends, 14.4% for 64 KiB appends and 1.6% for existing-file overwrites** over the [staged-metadata and arena-copy checkpoint](native-trst-slots.md). It is now **6.8% faster than original SFS for 8 KiB appends**, but remains **10.5% slower for 64 KiB appends** and substantially slower for overwrites. Overall throughput parity is still unmet.

The shared **192 KiB staging cap**, whole-write atomicity, buffered durability and legacy CSUN recovery remain unchanged. The journal format, publication sequence and allocation order are unchanged.

## Change and evidence

The allocation search used to repeat its run bookkeeping for every fully occupied 32-bit bitmap word. It now skips groups of four occupied words, then individual occupied words. The helper stays within the prepared bitmap and the volume's final data-sector bound. It runs only when a partial candidate is too short and the search is not a preferred-prefix probe; viable earlier runs and first-fit order are preserved.

The helper reads the existing search bitmap after allocation and reservation exclusions. It neither writes that bitmap nor allocates storage. The initial 512-byte-block group remains **8,560 bytes**; the full-budget single arena still fits **374 slots**. Both group arenas share the existing cap.

Inspecting the benchmark fixture found **739 fully occupied words, 378 fully free words and only eight mixed words** across its allocation bitmaps. This explains why skipping occupied words helps the fallback scans. An earlier mixed-word shortcut passed functional tests but regressed compiled throughput by **1.3–8.0%**; it was rejected. A second draft exposed an unsupported compiler operation when assigning an APTR argument. The final version uses a scalar local cursor and requires no compiler change.

The independent bit-by-bit oracle checks **9,248 combinations** of bitmap patterns, starting positions, minimum/maximum lengths, preferred-prefix behavior and bitmap boundaries. It runs against both the prior scanner and the final implementation. It includes the reserved marker, admin-reservation masking and final reserved sector. The oracle issues no physical writes.

## Compiled benchmark

Five cold trials per cell write 1 MiB on the same emulated 68020 at 7,093,790 cycles/s, using 512-byte blocks, 32 filesystem buffers, 512 KiB chip RAM, 512 KiB slow RAM and 2 MiB fast RAM. The Copper HUNKs target MC68000; original SFS requires 68020. No device seek or flush latency is added. All **60 trials** pass independent image checks and exact-byte extraction. The driver, fixture, ROM and original handler are unchanged.

Durable throughput includes open, Writes, Close and the successful final checkpoint, in KiB/s:

| Workload | Previous TRST | Updated TRST | Gain | Strict CSUN | Buffered CSUN | Original SFS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 539.04 | 554.15 | +2.80% | 80.49 | 602.36 | 518.65 |
| 64 KiB append | 2600.24 | 2975.89 | +14.45% | 714.91 | 2535.62 | 3326.49 |
| 8 KiB existing-file overwrite | 421.55 | 428.41 | +1.63% | 42.51 | 42.40 | 2452.28 |

TRST Write p50/p95 latency is **13.241/15.767 ms**, **13.795/32.690 ms** and **15.710/18.530 ms** for those workloads. The 64 KiB append p95 falls from 62.606 ms. The small overwrite p95 increase from 18.492 ms is retained in the complete results; throughput improves despite that tail measurement.

Every paired trial has identical I/O counters. TRST writes **1 / 1 / 2 KiB of journal data** and issues **8 / 8 / 16 flushes** per MiB. Maximum sampled RAM is **241.93 KiB**, about **8.5–8.6% below original SFS** and 288 bytes above the previous checkpoint. This is sampled retention, not an allocation high-water mark. The HUNK grows by 292 bytes.

Strict TRST's separate compiled attribution profiles improve **1.0%, 7.0% and 0.7%**, with unchanged I/O. CSUN also benefits from the shared scanner. The [complete benchmark](../artifacts/native-trst-wordscan/benchmark/report.md), [paired metrics](../artifacts/native-trst-wordscan/metrics.json) and [strict comparisons](../artifacts/native-trst-wordscan/strict-comparison.json) retain all results.

Original SFS overwrites file data in place. Its metadata journal does not establish the same whole-data replacement guarantee as TRST. This distinction remains relevant to the large overwrite performance gap.

## Qualification and integration

The frozen source passes **328 host tests**, with one optional external-image skip, and **80 compiled MC68000 buffer/codec tests**. Compiled buffered and strict TRST pass **17 and 14 tests**, respectively, plus both five-scenario relocation suites. Both CSUN policies pass their bulk-write crash suites. All **11 SFS2 compatibility tests** pass, covering **63 sector/flush cut points in 30 representative cut/reboot groups**. Nine new diagnostic profiles and the 60 benchmark trials pass.

| Compiled recovery policy | Sector/flush cut points | Distinct durable images | Copper/original recovery boots | Additional interrupted-recovery cuts / boots |
| --- | ---: | ---: | ---: | ---: |
| Buffered default | 1,529 | 81 | 162 | 6 / 12 |
| Strict TRST | 1,486 | 76 | 152 | 6 / 12 |

Equivalent durable prefixes share representative recovery boots. These tests assume atomic sectors and reliable flushes; they do not qualify physical power loss or torn sectors.

The implementation file and oracle test are integrated into CopperStart's embedded CopperSFS core. The [integrated audit](../artifacts/native-trst-wordscan/integrated-audit.json) verifies all **320 frozen source files**, the unchanged **181-file compiler snapshot**, exact HUNK identities, qualification receipts and unchanged I/O. Existing unrelated work is preserved. All five native profiles pass allocation-free MC68000 compatibility checks.

A fresh normal standalone build reproduces the qualified **162,764-byte HUNK**, SHA-256 `6BFDAF756BA39CC7BAC9E5433FB7B421536FF4B4D89EC94A184E84CC520783E8`. No installed handler, release package or public-core copy was changed.

The default remains buffered TRST: complete Writes become visible on success and durable on successful filesystem `ACTION_FLUSH`, writer Close, Inhibit or timer commit. A power loss before commit may lose acknowledged Writes. `-StrictWrites` retains per-Write durability.

The required full solution build still fails on the three pre-existing CopperOS shell references to missing `LoadSegmentNative`, `UnloadSegmentNative` and `UnloadDosProcessSegment` APIs. See the [build log](../artifacts/native-trst-wordscan/release-build.log). Standalone builds pass.

The remaining overwrite profile attributes about **12.1%** of cycles to block copying and **6.3%** to extent-record lookup. Repeated metadata materialization and traversal remain useful next targets; the [profile evidence](../artifacts/native-trst-wordscan/remaining-costs.json) records those costs without claiming another implemented optimization.
