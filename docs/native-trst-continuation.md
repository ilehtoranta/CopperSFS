# Native TRST overwrite continuation

The native buffered TRST handler improves existing-file overwrite throughput by **57.1%** over the [metadata-view checkpoint](native-trst-searchviews.md). Small and large appends change by **-0.10%** and **+0.23%**. Overwrites now reach **30.7% of original SFS throughput**; the overall parity objective remains unmet.

The **192 KiB shared staging cap**, whole-write atomicity, buffered durability and legacy CSUN recovery are preserved. The original-SFS-compatible journal format and publication order are unchanged.

## Change and atomicity

Consecutive aligned overwrites can extend an existing relocated extent without cloning the accepted metadata arena. A 48-byte private continuation records the file node, next logical position, source and replacement extents, record offsets, shared leaf and active-arena identity. It stores scalars, never borrowed platform image pointers. The group header grows from 76 to 124 bytes inside the existing budget; initial 512-byte-block staging grows from 8,572 to 8,620 bytes. Full-budget single-arena capacity remains 374 slots.

Before data I/O, the continuation reacquires and validates the object, protection and file bounds; validates already-owned extent and bitmap images; checks extent links and bitmap boundaries; excludes transaction markers, reservations and staged targets; and retains the conservative journal-space admission proof. The destination must be free in both the durable and accepted allocation views. Retired blocks that are still reachable on disk use the generic redo path.

All fallible work precedes unpublished data writes. A failed data transfer leaves every accepted arena byte unchanged. After successful I/O, only bounded edits to already-owned images remain: extent keys, lengths and back-links, allocation bits, incremental checksums and the continuation position. There are no further allocations, new slots or device calls in that phase. Access remains serialized. Partial writes, different files, nonconsecutive positions, boundaries and uncertain admission use the existing two-arena path. Failed Writes and commits invalidate the continuation.

The compiled overwrite profile replaces **126 arena clones with 6**, with **120 successful continuations**. CopyBlock exclusive cycles drop from 1,641,441 to 337,068, **79.5% lower**. Total profiled cycles fall from 15,163,234 to 9,649,464, **36.4% lower**. The final guard avoids entering the larger continuation function when ordinary appends have no continuation. Profile I/O is identical. See [paired profiles](../artifacts/native-trst-continuation/comparison.json).

## Compiled benchmark

Five cold trials per cell write 1 MiB on the same emulated 68020 at 7,093,790 cycles/s, using 512-byte blocks, 32 buffers, 512 KiB chip RAM, 512 KiB slow RAM and 2 MiB fast RAM. Copper HUNKs target MC68000; original SFS requires 68020. No device seek or flush latency is added. All **60 trials** pass independent image checks and exact-byte extraction. The pinned benchmark driver, emulator, fixture, ROM and original handler are unchanged.

Durable throughput includes open, Writes, Close and the final successful checkpoint, in KiB/s:

| Workload | Previous TRST | Updated TRST | Change | Strict CSUN | Buffered CSUN | Original SFS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 618.96 | 618.33 | -0.10% | 80.63 | 600.38 | 518.66 |
| 64 KiB append | 3234.90 | 3242.25 | +0.23% | 718.80 | 2520.19 | 3326.37 |
| 8 KiB existing-file overwrite | 479.07 | 752.80 | +57.14% | 42.54 | 42.43 | 2452.29 |

TRST Write latency, sampled memory and journal traffic:

| Workload | p50 ms | p95 ms | Maximum sampled RAM KiB | Journal writes KiB | Flushes |
| --- | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 11.853 | 14.583 | 247.52 | 1.0 | 8 |
| 64 KiB append | 12.403 | 27.432 | 247.52 | 1.0 | 8 |
| 8 KiB existing-file overwrite | 7.037 | 15.968 | 247.52 | 2.0 | 16 |

Every paired benchmark trial retains identical I/O counters. RAM remains **6.4 to 6.5% below original SFS**. Sampled RAM measures retained allocations at probe checkpoints, not allocation high-water usage. The HUNK grows by 5,044 bytes. The [full benchmark](../artifacts/native-trst-continuation/benchmark/report.md) and [paired metrics](../artifacts/native-trst-continuation/metrics.json) retain all policies, latency and memory measurements.

Strict TRST profiles differ by less than 0.3%, with identical I/O. Original SFS updates existing data in place; our whole-write guarantee also requires replacement-data allocation and metadata publication. The remaining profile attributes 10.6% to continuation validation and bookkeeping, 4.4% to bitmap-position calculation, 3.5% to CopyBlock, and 8.5% across the three main metadata-view/index helpers. These measured costs guide the next investigation; they are not evidence that the checks can simply be removed.

## Qualification and integration

The frozen core passes **378 host tests**, with one optional external-image test skipped, plus **80 compiled MC68000 buffer/codec tests**. Twenty-two new host cases cover continuation and fallback, 32/64 KiB packets spanning multiple bitmap words, both cached and supplied-scratch read-only views, every allocation bit, extent back-links, all 16 failed-data-sector positions for both view providers, preservation of accepted arena bytes, durable-reachable destination rejection, journal admission and reordered pending writes. The existing 36,992-case allocation oracle remains in the passing host suite.

The new compiled test issues three consecutive 8 KiB overwrites, validates pending read-back before Close and independently checks all 3 MiB of the resulting file. It covers **59 sector/flush cuts**, grouped into **5 durable images**, recovered separately by CopperSFS and original SFS. Both handlers agree on a wholly old or wholly updated file. Two additional clean Close handoffs also pass. See the [continuation receipt](../artifacts/native-trst-continuation/continuation-native-v3/native-trst-continuation-cuts.json).

Buffered and strict TRST pass **17 and 14 native tests**, plus both five-scenario relocation suites. Both CSUN policies pass bulk-write crash suites. All **11 SFS2 tests** pass, including **63 sector/flush cuts in 30 representative cut/reboot groups**. The additional continuation test is included in the buffered totals below:

| Compiled policy | Sector/flush cuts | Distinct durable images | Copper/original recovery boots | Interrupted-recovery cuts / boots |
| --- | ---: | ---: | ---: | ---: |
| Buffered default | 1,588 | 86 | 172 | 6 / 12 |
| Strict TRST | 1,486 | 76 | 152 | 6 / 12 |

Equivalent durable prefixes share representative recovery boots. These tests assume atomic sectors and reliable flushes; they do not qualify physical power loss or torn sectors. Host tests separately exercise reordered pending writes.

Nine implementation/test files are integrated into CopperStart's embedded core and native tests. The [integrated audit](../artifacts/native-trst-continuation/integrated-audit.json) checks **324 frozen source files**, the unchanged **181-file compiler snapshot**, exact HUNK identities and all qualification receipts. Unrelated shared-handler work is preserved. All five native profiles pass allocation-free MC68000 compatibility checks. A fresh standalone build reproduces the **168,576-byte HUNK**, SHA-256 `784EE82669CE03B3C465DEBAAB3CCFD901EF8238560EE3B673D19FBA0DBBA38E`.

The final source snapshot adds four host boundary cases to the compiled candidate; every production and native-test source byte is unchanged. Earlier test-harness builds caught two missing native mode arguments and an explicit unsigned conversion needed by the enlarged host fixture. Both were corrected before the affected tests ran; those build logs are retained. The final host and native suites pass.

Buffered TRST remains the default: successful Writes are visible immediately and become durable on successful filesystem `ACTION_FLUSH`, writer Close, Inhibit or timer commit. Power loss before commit may lose acknowledged Writes. `-StrictWrites` retains per-Write durability.

The required full solution build result is **three pre-existing missing CopperOS shell APIs**; see the [build log](../artifacts/native-trst-continuation/release-build.log). Standalone builds pass. No installation, package release, commit or public-core synchronization was performed.
