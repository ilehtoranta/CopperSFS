# Native TRST transfer setup and cache invalidation

Buffered TRST improves durable throughput by **2.13%** for 8 KiB append, **1.10%** for 64 KiB append, and **3.71%** for existing-file overwrite, relative to the [write setup and checksum checkpoint](native-trst-dispatch.md). Overwrite reaches **45.9% of original SFS throughput**. The overall parity target remains unmet.

The 192 KiB shared staging cap, whole-write atomicity, delayed durability, journal format and legacy CSUN recovery are unchanged. All paired benchmark I/O counters match the previous checkpoint.

## Changes and evidence

The native streaming transfer path now checks 512-byte range arithmetic with bounded shifts. This removes a full software 32-bit division from each common request. A request that fits MaxTransfer, the 32-bit device offset and the existing DMA-memory policy goes directly to one device transfer. Split requests, bounce buffers, non-512-byte geometry and TD64 continue through the general path, preserving complete-prefix read results and whole-request error handling.

Moving that general path into a separate method also reduces the common TransferData frame from 84 to 20 bytes and its saved registers from 40 to 24 bytes: 80 fewer stack bytes on that call. The fallback adds a call frame, so this reduction applies to the common path. There is no persistent state or memory-layout change.

Small-range cache invalidation now walks consecutive hash bucket pointers, wrapping at the end of the 64-bucket table. It preserves collision filtering and snapshots each link before changing the hash and LRU lists. This removes repeated bucket-index multiplication from the loop.

The compiled overwrite profile falls from 6,696,893 to 6,457,453 guest cycles. Separate prototypes attribute a 2.22% throughput improvement to the direct transfer path, a further 0.40% to separating its fallback frame, and a further 1.05% to walking cache bucket pointers. These sequential percentage improvements compound. The final profiled HUNK is byte-identical to the qualified candidate; the later snapshot adds tests only.

An independent inclusive call trace measures TransferData at 307,441 cycles over 130 calls, down from 501,001. InvalidateBlocks takes 290,236 cycles over 129 calls, down from 361,883. Both traces finish with zero unmapped entries and zero open frames. Profiling reads emulated RAM and registers without adding guest bus operations or cycles. Inclusive parent and child timings overlap and must not be summed. See the [profile evidence](../artifacts/native-trst-transfer/profile-evidence.json).

## Paired benchmark

Five cold trials per cell write 1 MiB on the same emulated 68020 at 7,093,790 cycles/s, with 512-byte blocks, 32 buffers, 512 KiB chip RAM, 512 KiB slow RAM and 2 MiB fast RAM. Copper handlers target MC68000; original SFS requires 68020. No physical seek or flush latency is added. Driver, emulator, ROM, fixture and original handler are unchanged. All 60 trials pass independent image checks and exact-byte extraction.

Durable throughput includes open, Writes, Close and the final successful checkpoint, in KiB/s:

| Workload | Previous TRST | Updated TRST | Change | Strict CSUN | Buffered CSUN | Original SFS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 631.86 | 645.34 | +2.13% | 82.27 | 626.03 | 518.65 |
| 64 KiB append | 3271.35 | 3307.29 | +1.10% | 736.46 | 2592.16 | 3308.34 |
| 8 KiB existing-file overwrite | 1084.67 | 1124.91 | +3.71% | 43.50 | 43.31 | 2452.40 |

TRST Write latency, sampled memory and journal traffic:

| Workload | p50 ms | p95 ms | Maximum ms | Maximum sampled RAM KiB | Journal writes KiB | Flushes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 11.314 | 13.848 | 26.965 | 250.61 | 1.0 | 8 |
| 64 KiB append | 12.267 | 27.519 | 27.519 | 250.61 | 1.0 | 8 |
| 8 KiB existing-file overwrite | 5.152 | 9.171 | 71.616 | 250.61 | 0.5 | 4 |

Sampled RAM remains about **5.2% below original SFS**. The handler grows by 332 bytes; staging capacity is unchanged. Memory samples measure retained allocations at probe checkpoints, not allocation high-water marks. The [full benchmark](../artifacts/native-trst-transfer/benchmark/report.md) includes every policy's latency and memory measurements.

Original SFS overwrites existing data in place, avoiding the mapping and bitmap work required for TRST's atomic replacement. These data guarantees differ, as established in the [parity investigation](native-trst-parity.md). That difference does not change the outstanding performance target.

## Qualification

All 103 compiled MC68000 instruction tests pass. Seventeen new cases cover transfer arithmetic, exact 32-bit offset boundaries, crossing into TD64, volume limits, caller-address wrap, short device completion, device errors, cache collisions and hash-bucket wrap. The existing cases retain coverage for DMA restrictions, bounce buffers, MaxTransfer splitting and other filesystem/device block sizes. The cache tests verify exact survivors, unchanged payload bytes, cleared verification and complete hash/LRU link integrity.

The filesystem core and its host tests are unchanged. Their previously passing 469 host tests, with one optional external-image test skipped, are reused after comparing every frozen core-source hash. This is existing host evidence, not a new host-test run.

Buffered and strict TRST pass 17 and 14 native tests, plus both five-scenario relocation suites. Both CSUN variants pass their bulk-write crash suites. All 11 SFS2 tests pass, including 63 sector/flush cuts represented by 30 actual cut/reboot groups.

Four additional compiled scenarios issue 5 × 8 KiB, 5 × 64 KiB, 65 × 8 KiB and 9 × 64 KiB Writes. They check pending reads and independently verify all 3 MiB of the file with both recovery handlers. Both handlers must agree on the number of complete recovered Writes; partial Writes fail. Eight clean Close handoffs also pass. These scenarios are included in the buffered totals:

| Policy | Sector/flush cuts | Distinct durable images | Copper/original recovery boots | Interrupted recovery cuts / boots |
| --- | ---: | ---: | ---: | ---: |
| Buffered default | 4,495 | 101 | 202 | 6 / 12 |
| Strict TRST | 1,486 | 76 | 152 | 6 / 12 |

Equivalent durable prefixes share representative recovery boots. Compiled tests assume atomic sectors and reliable flushes; they do not qualify physical power loss or torn sectors. Existing host tests separately cover reordered pending writes.

## Integration and remaining work

Three implementation/test files are integrated into CopperStart. The [integrated audit](../artifacts/native-trst-transfer/integrated-audit.json) verifies 334 frozen source files, the unchanged 181-file compiler snapshot, exact handler identities, qualification receipts and preserved unrelated Exec changes. All five profiles pass allocation-free MC68000 compatibility checks. A fresh live standalone build reproduces the 171,796-byte HUNK, SHA-256 `76D316385EFB34D40D82BC8D9C0B82972ACC8B3CD5CB6A2C6940C0F923AB456A`.

Buffered TRST remains the default: successful Writes become visible immediately and become durable on successful filesystem ACTION_FLUSH, writer Close, Inhibit or timer commit. Power loss before commit may lose acknowledged Writes. Strict mode retains per-Write durability.

The required full solution build still fails on three pre-existing missing CopperOS shell APIs: LoadSegmentNative, UnloadSegmentNative and UnloadDosProcessSegment. Standalone builds pass; see the [build log](../artifacts/native-trst-transfer/release-build.log). No installation, package release, commit or public-core synchronization was performed.

Continuation validation, metadata access and allocation work remain the largest targets. Further copy-loop tuning cannot close the remaining overwrite gap on its own. The [remaining-costs receipt](../artifacts/native-trst-transfer/remaining-costs.json) records measured methods and slowest Writes for the next investigation.
