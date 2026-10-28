# Native TRST metadata views and record-location reuse

The native buffered TRST handler gains **11.7% for 8 KiB appends, 8.7% for 64 KiB appends and 11.8% for existing-file overwrites** over the [occupied-word scan checkpoint](native-trst-wordscan.md). It is **19.3% faster than original SFS for small appends** and within **2.8% for large appends** in this run. Overwrite throughput remains only **19.5% of original SFS**, so the overall performance objective remains unmet.

The shared **192 KiB staging cap**, whole-write atomicity, buffered durability and legacy CSUN recovery are preserved. The journal format and publication order are unchanged.

## Changes

Allocation scans now borrow a verified read-only bitmap view when no CSUN checkpoint, journal chain, admin reservation or overwrite reservation needs to be masked. If the transaction-marker bit needs clearing, they expire that view with another block access and obtain mutable scratch through the existing search-map path. This also handles platforms that return the supplied scratch as their read-only view. Other cases retain the existing scratch-and-mask path. A scan finishes consuming the current view before loading another bitmap; it never keeps the view across block I/O or edits it in place.

A TRST group now retains the last validated object node, container block and record offset. Consecutive Writes reacquire and validate that metadata image, then check the node and bounds at the saved location. They still read current file type, protection, size and first extent. The cache stores three scalars, never a borrowed image pointer. A different node uses the normal lookup; a failed staged Write invalidates the hint. New groups and successful commits clear it, so intervening namespace and metadata operations cannot leave an old location active. Those operations already require the pending group to be flushed.

The group header grows from **64 to 76 bytes**, with a size check before using the new private fields. Initial group staging at 512-byte blocks grows from **8,560 to 8,572 bytes**. The two arenas remain inside their shared budget; full-budget single-arena capacity stays at 374 slots. The temporary Work field is a pointer to the owned hint and is cleared when the staged Write returns.

Isolated compiled profiles attribute **0.4–3.6%** improvement to borrowed bitmap views. Adding the record hint gives the combined gains above. In the final overwrite profile, cached-block materialization calls drop from **159 to 23**, and CopyBlock exclusive cycles fall from **2,046,051 to 1,641,441**, about **19.8%**. The [paired profiles](../artifacts/native-trst-searchviews/comparison.json) retain exact HUNK/map identities and unchanged I/O checks.

## Compiled benchmark

Five cold trials per cell write 1 MiB on the same emulated 68020 at 7,093,790 cycles/s, using 512-byte blocks, 32 filesystem buffers, 512 KiB chip RAM, 512 KiB slow RAM and 2 MiB fast RAM. The Copper HUNKs target MC68000; original SFS requires 68020. No device seek or flush latency is added. All **60 trials** pass independent image checks and exact-byte extraction. The driver, fixture, ROM and original handler are unchanged.

Durable throughput includes open, Writes, Close and the successful final checkpoint, in KiB/s:

| Workload | Previous TRST | Updated TRST | Gain | Strict CSUN | Buffered CSUN | Original SFS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 554.15 | 618.96 | +11.70% | 80.65 | 600.38 | 518.67 |
| 64 KiB append | 2975.89 | 3234.90 | +8.70% | 718.77 | 2515.64 | 3326.39 |
| 8 KiB existing-file overwrite | 428.41 | 479.07 | +11.83% | 42.58 | 42.36 | 2452.29 |

TRST Write p50/p95 latency is **11.847/14.360 ms**, **12.331/30.083 ms**, **13.776/16.222 ms** for those workloads, down from **13.241/15.767**, **13.795/32.690** and **15.710/18.530 ms**. The original handler's measured variation is retained in the paired metrics; the small large-append gap does not establish parity yet.

Every paired trial retains identical I/O counters. TRST writes **1 / 1 / 2 KiB of journal data** and issues **8 / 8 / 16 flushes** per MiB. Maximum sampled RAM is **242.69 KiB**, about **8.2–8.3% below original SFS** and 776 bytes above the previous checkpoint. This is sampled retention, not an allocation high-water mark. The HUNK grows by 768 bytes.

Strict TRST's attribution profiles improve **0.5%**, **1.1%**, **2.2%**, with unchanged I/O. Both CSUN policies are also measured; their shared-path gains and regressions are retained in the [complete benchmark](../artifacts/native-trst-searchviews/benchmark-v3/report.md) and [paired metrics](../artifacts/native-trst-searchviews/metrics.json).

Original SFS updates existing data in place. Our whole-write guarantee uses unpublished replacement data and allocation/extent updates before metadata publication. Repeated extent lookup, owned-image copying and transaction bookkeeping remain substantial costs even though device traffic is now close. The current overwrite profile attributes **10.8%** of cycles to CopyBlock, **7.0%** to extent-record lookup, and another **12.4%** to the three main metadata-view/index helpers. These are measured costs, not proof that every one can safely be eliminated.

## Qualification and integration

The frozen source passes **356 host tests**, with one optional external-image skip, and **80 compiled MC68000 buffer/codec tests**. The independent allocation oracle covers **36,992 cases**, including borrowed-view immutability and expiry for both separate cache images and supplied scratch, and passes against both the prior and updated scanner. New group tests cover file switching, reuse, failed Writes, commit/restart, malformed private hints, and preservation of previously accepted data.

The expanded oracle exposed six read-only-view violations in the initial combined prototype: its marker-mask fallback modified the supplied scratch while it was still a borrowed view. That prototype was rejected. The final version expires the view before obtaining mutable scratch, passes all oracle cases, and was rebuilt and requalified independently. Both the failing regression evidence and final passing results are retained.

The initial buffer-test build accidentally selected an older packaged SDK because its explicit local SDK path was omitted. It failed before tests ran. The corrected command uses the same local SDK as the native handler build and passes all 80 tests; the source snapshot was unchanged. Both logs are retained.

Compiled buffered and strict TRST pass **17 and 14 tests**, respectively, plus both five-scenario relocation suites. Both CSUN policies pass their bulk-write crash suites. All **11 SFS2 tests** pass, including **63 sector/flush cut points in 30 representative cut/reboot groups**. Twelve diagnostic profiles across the isolated prototypes and final build, and all 60 final benchmark trials, pass.

| Compiled recovery policy | Sector/flush cut points | Distinct durable images | Copper/original recovery boots | Additional interrupted-recovery cuts / boots |
| --- | ---: | ---: | ---: | ---: |
| Buffered default | 1,529 | 81 | 162 | 6 / 12 |
| Strict TRST | 1,486 | 76 | 152 | 6 / 12 |

Equivalent durable prefixes share representative recovery boots. Tests assume atomic sectors and reliable flushes; they do not qualify physical power loss or torn sectors.

Six implementation/test files are integrated into CopperStart's embedded CopperSFS core. The [integrated audit](../artifacts/native-trst-searchviews/integrated-audit.json) verifies all **321 frozen source files**, the unchanged **181-file compiler snapshot**, exact HUNKs, qualification receipts and unchanged I/O. Unrelated work is preserved. All five native profiles pass allocation-free MC68000 compatibility checks.

A fresh normal standalone build reproduces the qualified **163,532-byte HUNK**, SHA-256 `29BBE0618E80082B6AE1290397DD08358E8FE2CF11B8A46586DFD01365A13BC6`. No installation, package release, commit or public-core synchronization was performed.

Buffered TRST remains the default: complete Writes become visible on success and durable on successful filesystem `ACTION_FLUSH`, writer Close, Inhibit or timer commit. A power loss before commit may lose acknowledged Writes. `-StrictWrites` retains per-Write durability.

The required full solution build still fails on the three pre-existing CopperOS shell references to missing `LoadSegmentNative`, `UnloadSegmentNative` and `UnloadDosProcessSegment` APIs. See the [build log](../artifacts/native-trst-searchviews/release-build.log). Standalone builds pass.
