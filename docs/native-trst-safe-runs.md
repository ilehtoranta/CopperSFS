# Native TRST durable-free allocation

Buffered TRST improves existing-file overwrite throughput by **21.0%** over the [validation-cache checkpoint](native-trst-proof-cache.md). Append throughput changes by **-0.34%** for 8 KiB packets and **-1.60%** for 64 KiB packets. Overwrite reaches **41.7% of original SFS throughput**; overall throughput parity remains unmet.

The **192 KiB shared staging cap**, whole-write atomicity, buffered durability and legacy CSUN recovery remain intact. The private header remains 200 bytes; initial staging remains 8,696 bytes with eight slots per arena. The full shared budget still holds 186 slots per arena, or 374 slots on the single-arena path. The journal encoding and publication order are unchanged.

## What caused the extra work

Read-only snapshots of emulated RAM confirmed that Writes 64 and 128 in the previous benchmark selected blocks retired by the pending group but still reachable on disk. Those blocks correctly required payload staging. The eight-slot arena overflowed, so the handler flushed the accepted group and retried the current Write separately. This explained the two large latency spikes.

The allocator now prefers a complete replacement run free in all three views: durable disk, accepted checkpoint and current candidate. It excludes reservations, the transaction marker and every staged home target. If no safe run exists, it retains the existing staged-data fallback. The unpublished-data writer independently rechecks the destination before transfer. Failed transfers leave the accepted arena unchanged.

Two related bottlenecks needed fixes before this policy could improve performance. The first prototype took 40.78 million overwrite cycles: journal allocation walked blocks individually when the durable bitmap said free but the final bitmap said allocated. The journal search now intersects both bitmaps once into its existing mutable scratch, then skips occupied spans. It never changes an owned arena image or a borrowed platform view.

Next, the conservative journal-space test charged an entire bitmap's capacity for every touched map. Crossing into a fourth map disabled continuations on this roomy fixture. When that cheap bound fails, the new bound counts the originally free bits in each touched allocation bitmap. It charges all of those bits plus every staged target against the original free-space total. Enough remaining space therefore guarantees journal pages even if subsequent continuations consume every originally free block in those maps. This remains conservative for the entire unchanged slot set and requires no extra persistent memory. Bitmap-looking file payload is distinguished by its target address.

The final compiled overwrite profile drops from **8,599,333 to 7,106,674 cycles**. Journal pages fall from four to one, flushes from sixteen to four, home/marker/data writes from 2,073 to 2,057 sectors, and write requests from 153 to 137. The slowest profiled Write falls from about 182.5 to 71.5 ms. See the [profile comparison](../artifacts/native-trst-safe-runs/comparison-v5.json) and [remaining costs](../artifacts/native-trst-safe-runs/remaining-costs.json).

## Benchmark

Five cold trials per cell write 1 MiB on the same emulated 68020 at 7,093,790 cycles/s, using 512-byte blocks, 32 buffers, 512 KiB chip RAM, 512 KiB slow RAM and 2 MiB fast RAM. Copper handlers target MC68000; original SFS requires 68020. No device seek or flush latency is added. All **60 trials** pass independent image checks and exact-byte extraction. Driver, emulator, fixture, ROM and original handler are unchanged.

Durable throughput includes open, Writes, Close and the final successful checkpoint, in KiB/s:

| Workload | Previous TRST | Updated TRST | Change | Strict CSUN | Buffered CSUN | Original SFS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 617.85 | 615.73 | -0.34% | 80.73 | 600.30 | 520.34 |
| 64 KiB append | 3243.28 | 3191.40 | -1.60% | 719.13 | 2514.87 | 3326.49 |
| 8 KiB existing-file overwrite | 844.71 | 1022.13 | +21.00% | 42.59 | 42.47 | 2452.28 |

TRST Write latency, sampled memory and journal traffic:

| Workload | p50 ms | p95 ms | Maximum ms | Maximum sampled RAM KiB | Journal writes KiB | Flushes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 11.876 | 14.605 | 26.739 | 250.06 | 1.0 | 8 |
| 64 KiB append | 12.764 | 27.272 | 27.272 | 250.06 | 1.0 | 8 |
| 8 KiB existing-file overwrite | 5.807 | 9.905 | 71.547 | 250.06 | 0.5 | 4 |

Sampled RAM remains about **5.4% below original SFS**. These samples measure retained allocations at probe checkpoints, not the allocation high-water mark. All other paired benchmark I/O counters are unchanged. The [full benchmark](../artifacts/native-trst-safe-runs/benchmark/report.md) retains every policy's latency and memory measurements.

## Qualification

The final frozen source passes **463 host tests**, with one optional external-image test skipped, and **80 compiled MC68000 buffer/codec tests**. The allocation oracles independently compare current-view and three-view searches over 73,984 combinations. Sixteen journal-allocation cases cover two-map intersections, an absent final map, misleading payload signatures, word/map/end-of-volume boundaries, monotonic distinct pages, conservative admission and disk-full behavior. Tests cover both cached and scratch-returning metadata views.

The new host scenarios exercise exhausted durable-free prefixes, safe alternate runs, the no-safe-run staged fallback, every data-sector failure for 8 KiB packets and first/middle/last failures for 64 KiB packets, unchanged accepted arenas, old-source preservation, retries, remounts and reordered pending writes.

Four compiled tests issue 5 × 8 KiB, 5 × 64 KiB, 65 × 8 KiB and 9 × 64 KiB Writes. They check pending reads before Close, then independently check all 3 MiB of the file under both handlers after recovery. Larger scenarios allow complete Write prefixes when a timer or capacity boundary commits an accepted group; partial Writes fail. Both handlers must agree on the recovered prefix. Eight clean Close handoffs also pass.

Buffered and strict TRST pass **17 and 14 native tests**, plus both five-scenario relocation suites. Both CSUN policies pass bulk-write crash suites. All **11 SFS2 tests** pass, including **63 sector/flush cuts in 30 representative cut/reboot groups**. The four new compiled scenarios are included in the buffered totals below:

| Compiled policy | Sector/flush cuts | Distinct durable images | Copper/original recovery boots | Interrupted-recovery cuts / boots |
| --- | ---: | ---: | ---: | ---: |
| Buffered default | 4,490 | 101 | 202 | 6 / 12 |
| Strict TRST | 1,486 | 76 | 152 | 6 / 12 |

Equivalent durable prefixes share representative recovery boots. Compiled tests assume atomic sectors and reliable flushes; they do not qualify physical power loss or torn sectors. Host tests separately exercise reordered pending writes.

## Integration

Sixteen implementation/test files are integrated into CopperStart's embedded core and native tests. The [integrated audit](../artifacts/native-trst-safe-runs/integrated-audit.json) verifies **330 frozen source files**, the unchanged **181-file compiler snapshot**, HUNK identities, qualification receipts and preserved unrelated Exec changes. All five native profiles pass allocation-free MC68000 compatibility checks. A fresh standalone build reproduces the **171,216-byte HUNK**, SHA-256 `C59E0F0859FA6B99707076F8D701C2A83CF975DFA64E85D8E1A5236B9332D659`.

Buffered TRST remains the default: successful Writes are visible immediately and become durable on successful filesystem `ACTION_FLUSH`, writer Close, Inhibit or timer commit. Power loss before commit may lose acknowledged Writes. `-StrictWrites` retains per-Write durability.

The required full solution build result is **three pre-existing missing CopperOS shell APIs**; see the [build log](../artifacts/native-trst-safe-runs/release-build.log). Standalone builds pass. No installation, package release, commit or public-core synchronization was performed.
