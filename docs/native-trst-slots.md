# Native TRST staged metadata and arena copying

Follow-up: [occupied-word scanning](native-trst-wordscan.md) records the next qualified optimization and its updated comparison with original SFS. This page retains the preceding checkpoint.

The native buffered TRST handler is **10.6–18.9% faster** than the [MC68000 arithmetic checkpoint](native-trst-arithmetic.md). It now exceeds original SFS throughput for 8 KiB appends by about **4%**. Original SFS remains faster for 64 KiB appends and existing-file overwrites, so the overall throughput goal remains unmet.

The shared **192 KiB staging cap**, whole-write atomicity, buffered durability policy and legacy CSUN recovery are preserved. These are private memory-layout changes; the journal format and publication order are unchanged.

## Changes

Each indexed arena slot now holds a verified metadata ID alongside its block number. A typed read can reuse that verification through the existing hash lookup. Returning an editable view revokes it before the caller can modify the image; raw stores revoke it too, including self-stores. Only internally sealed metadata can restore it without another checksum. A present but invalid staged image remains an error and cannot fall through to an older disk image. SFS2 retains its existing linear arena layout and validation path.

Buffered Writes previously initialized the alternate arena, looked up every prior block and copied its image separately. The new clone copies the header, index and occupied slots in one span, then rebases the private index pointers. It leaves unused capacity untouched and preserves the accepted source arena while the next whole Write is staged. It checks the owned header, capacity, mapped spans and disjoint destination before writing. It neither allocates memory nor issues device I/O.

Isolated compiled profiles attribute **6.9–12.7%** throughput improvement to slot verification, with another **3.5–5.8%** from cloning the arena. In the overwrite profile, the main checksum method falls from 2.54 million to 0.071 million exclusive cycles. The [attribution comparison](../artifacts/native-trst-slots/comparison.json) retains all paired profiles and unchanged I/O checks.

**Tradeoff:** strict TRST profiles lose **1.6–1.9%** throughput. Their arenas reset on every Write, so retained verification has less opportunity to repay its bookkeeping. The buffered default receives the larger measured gain. Rebuilt CSUN profiles also lose roughly 0.1–0.8% in these workloads. These regressions are included in the comparison, not presented as improvements.

## Compiled benchmark

Five cold trials per cell write 1 MiB on the same emulated 68020 at 7,093,790 cycles/s, with 512-byte blocks, 32 filesystem buffers, 512 KiB chip RAM, 512 KiB slow RAM and 2 MiB fast RAM. The Copper binaries target MC68000; the original SFS handler requires 68020. No device seek or flush latency is added. All **60 trials** pass independent image checks and exact-byte extraction. The driver, fixture, ROM and original handler are unchanged.

Durable throughput, including the successful final checkpoint, in KiB/s:

| Workload | Previous TRST | Updated TRST | Gain | Strict CSUN | Buffered CSUN | Original SFS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 453.17 | 539.04 | +18.95% | 80.17 | 576.83 | 518.66 |
| 64 KiB append | 2350.28 | 2600.24 | +10.64% | 691.19 | 2142.38 | 3326.58 |
| 8 KiB existing-file overwrite | 360.83 | 421.55 | +16.83% | 42.11 | 42.00 | 2452.29 |

| TRST workload | Previous p50 / p95 Write latency | Updated p50 / p95 |
| --- | ---: | ---: |
| 8 KiB append | 16.566 / 18.884 ms | 13.297 / 15.967 ms |
| 64 KiB append | 17.005 / 62.403 ms | 13.881 / 62.606 ms |
| 8 KiB existing-file overwrite | 18.859 / 21.327 ms | 15.770 / 18.492 ms |

Every paired trial retains the same I/O counters. TRST writes **1 / 1 / 2 KiB of journal data** and issues **8 / 8 / 16 flushes** per MiB. Its maximum sampled RAM retention rises from **240.79 to 241.65 KiB**, still about **8.6–8.7% below original SFS**. The HUNK grows by **832 bytes**. Sampled retention includes code and retained mount state; it is not an allocation high-water mark.

Initial group staging grows from **8,496 to 8,560 bytes** at 512-byte blocks. A single arena using the full 192 KiB budget now fits **374 slots**, previously 377. Both arena allocations together remain subject to the same shared cap; exact allocation extents and release sizes are tested. Exhaustion must fail without partially publishing a Write.

Original SFS overwrites existing data in place; its throughput does not establish the same whole-data replacement guarantee as TRST. The [full benchmark](../artifacts/native-trst-slots/benchmark/report.md) includes all policies, I/O, latency, sampled memory and binary identities.

## Qualification

The frozen source passes **320 host tests**, with one optional external-image test skipped, and **80 compiled MC68000 buffer/codec tests**. Added coverage checks verification invalidation on edit, wrong ID, raw store, self-store and reset; colliding arena keys; full capacity; both pointer-rebase directions; source preservation; unused tails; exact allocation bounds; and rejected overlap, undersized spans and malformed private headers. Four host block sizes cover 512 through 32,768 bytes.

An initial compiled-test fixture placed its clone destination in the source arena's guard region. Moving the destination and adding separate canaries and unused-tail checks fixed the fixture; the production HUNK stayed byte-identical. The final 80-test run is the qualification evidence.

Compiled buffered and strict TRST pass **17 and 14 tests**, respectively, plus both five-scenario relocation suites. Both CSUN policies pass their bulk-write crash suites. All **11 SFS2 tests** pass, including **63 sector/flush cut points in 30 representative cut/reboot groups**. The final evidence includes the 12 new diagnostic profiles and all 60 benchmark trials.

| Compiled recovery policy | Sector/flush cut points | Distinct durable images | Copper/original recovery boots | Additional interrupted-recovery cuts / boots |
| --- | ---: | ---: | ---: | ---: |
| Buffered default | 1,529 | 81 | 162 | 6 / 12 |
| Strict TRST | 1,486 | 76 | 152 | 6 / 12 |

Equivalent durable prefixes share representative recovery boots, with coverage receipts for every sector/flush cut. These tests assume atomic sectors and reliable flushes; they do not qualify physical power loss or torn sectors.

## Integration

Ten qualified implementation/test files are integrated into CopperStart and its embedded CopperSFS core. The [integrated audit](../artifacts/native-trst-slots/integrated-audit.json) verifies all 319 frozen source files, the unchanged 181-file compiler snapshot, exact HUNKs, qualification receipts, method-map equivalence and unchanged I/O. Existing unrelated work is preserved. All five native profiles pass allocation-free MC68000 compatibility checks.

A fresh normal standalone build reproduces the qualified **162,472-byte HUNK**, SHA-256 `1FC4DBA3C10CD8356F054022AA6AFFF65CCDF256BA1E7549B738BAFD84A4DB0B`. No installed handler or release package was changed.

The default remains buffered TRST: complete Writes become visible on success and durable on successful filesystem `ACTION_FLUSH`, writer Close, Inhibit or timer commit. A power loss before commit may lose acknowledged Writes. `-StrictWrites` retains per-Write durability.

The required full solution build still fails on the three pre-existing CopperOS shell references to missing `LoadSegmentNative`, `UnloadSegmentNative` and `UnloadDosProcessSegment` APIs; see the [build log](../artifacts/native-trst-slots/release-build.log). Standalone builds pass.

## Remaining cost

In the updated overwrite diagnostic, block copying accounts for about **11.9%** of cycles and extent-record lookup for **6.2%**. Most remaining copy calls come from cloning owned images and materializing cached block reads; direct bulk-data transfers do not appear among the CopyBlock callers in that profile. Removing repeated metadata copying or traversal is a more promising next investigation than changing the now-small checksum cost. These are profile observations, not claims of another implemented optimization.
