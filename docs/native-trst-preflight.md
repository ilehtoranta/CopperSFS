# Native TRST validation and arithmetic

Relative to the [qualified compiler profile](native-trst-codegen.md), durable throughput improves by **0.90%** for 8 KiB append, **0.56%** for 64 KiB append, and **4.46%** for overwrite. Overwrite reaches **49.5% of original SFS throughput**; the overall throughput parity target remains unmet.

The changes preserve the 192 KiB shared staging cap, whole-write atomicity, delayed durability, journal encoding and legacy CSUN recovery. All 60 paired benchmark trials retain the previous device-I/O counters.

## What the profile showed

The previous inclusive trace spent 2,457,603 of 6,269,194 measured guest cycles inside continuation processing, about 39%. CopyBlock's exclusive contribution was about 3%. This checkpoint targets repeated validation arithmetic instead of further block copying.

The generated record-offset check used three MC68000 multiplies for each cached record. The proof already validated the record's range, alignment and distinctness, and sealed its layout. It now stores the validated offset directly. Each continuation still verifies the certificate, unchanged leaf record count and stride, matching current hint offsets, extent keys and links, and source/destination allocation bits. The private certificate remains 76 bytes inside the unchanged 200-byte group header; no disk format changes.

Four scratch-buffer ranges can often be proved mapped and disjoint from staging with one containing range. The fast path computes the minimum and maximum addresses, rejects overflow for that proof, and checks the complete range against the current memory map. It caches nothing between requests. Separate memory regions, unmapped gaps, address wrap, or staging between the buffers use the original individual checks.

The shared overlap predicate now compares the distance between ordered starts. This is equivalent to the former widened interval comparison even when a range's mathematical end exceeds 2^32, and avoids 64-bit additions on MC68000. Zero-length ranges retain their behavior. The common 512-byte continuation path uses masks and shifts for alignment and block counts; other block sizes keep division. The common 520-byte arena slot uses two shifts instead of general multiplication, retaining index bounds, target identity and verification checks.

The first prototype improved the overwrite diagnostic by 4.01%. Adding the arena-slot arithmetic change raises that to 4.47%. Selected diagnostic gains are 0.90% for 8 KiB append and 0.56% for 64 KiB append. Every diagnostic independently checks image contents and exact bytes, and its I/O matches the baseline. See the [comparison](../artifacts/native-trst-preflight/comparison.json) and [paired call profiles](../artifacts/native-trst-preflight/profile-evidence.json). Both call traces finish with zero unmapped entries and zero open frames. Inclusive parent/child timings overlap and must not be summed.

## Paired benchmark

Five cold trials per cell write 1 MiB on the same emulated 68020 at 7,093,790 cycles/s, with 512-byte blocks, 32 buffers, 512 KiB chip RAM, 512 KiB slow RAM and 2 MiB fast RAM. Copper handlers target MC68000; original SFS requires 68020. No physical seek or flush latency is added. Driver, emulator, ROM, fixture and original handler are unchanged. Durable throughput includes open, Writes, Close and the final successful checkpoint, in KiB/s:

| Workload | Previous TRST | Updated TRST | Change | Strict CSUN | Buffered CSUN | Original SFS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 666.05 | 672.05 | +0.90% | 83.85 | 649.24 | 518.65 |
| 64 KiB append | 3450.39 | 3469.85 | +0.56% | 752.77 | 2680.58 | 3326.58 |
| 8 KiB existing-file overwrite | 1162.84 | 1214.75 | +4.46% | 44.61 | 44.45 | 2452.38 |

TRST Write latency, sampled memory and journal traffic:

| Workload | p50 ms | p95 ms | Maximum ms | Maximum sampled RAM KiB | Journal writes KiB | Flushes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 10.717 | 13.448 | 25.947 | 235.21 | 1.0 | 8 |
| 64 KiB append | 11.169 | 26.515 | 26.515 | 235.21 | 1.0 | 8 |
| 8 KiB existing-file overwrite | 4.665 | 7.509 | 68.084 | 235.21 | 0.5 | 4 |

The handler is 155,568 bytes, 140 fewer than the previous build. Sampled RAM is 11.0% below original SFS on the overwrite workload. Samples include retained code, stack, mount records and buffers at probe checkpoints; they are not allocation high-water marks. The [complete benchmark](../artifacts/native-trst-preflight/benchmark/report.md) includes each policy's latency and memory measurements.

Original SFS overwrites existing data in place, avoiding the mapping and bitmap work required by atomic replacement. The [parity investigation](native-trst-parity.md) establishes these different data guarantees. They do not change the outstanding performance target.

## Qualification

The updated core passes **485 host tests**, with one optional external-image test skipped. New coverage includes 6,561 combinations checked against independent widened interval arithmetic, 12 workspace configurations including holes and address boundaries, and three changed cached-offset hints rejected before data I/O. Existing tests cover stale certificates, allocation changes, scratch-view expiry, per-sector failures preserving every accepted arena byte, and reordered pending writes.

All **115 compiled MC68000 instruction tests** pass without skips. Twelve new cases call the private workspace method in the exact qualified handler, using live Exec memory-list layouts. They check separated regions, staging between buffers, overlap, nulls, aliases, overflow and the upper address boundary, plus callee-saved registers, stack balance and unchanged work records. Existing transfer, DMA, MaxTransfer, bounce-buffer, TD64, checksum, cache collision and hash/LRU cases also pass.

Buffered and strict TRST pass 17 and 14 native tests, plus both five-scenario relocation suites. Both CSUN policies pass their bulk-write crash suites. All 11 SFS2 tests pass, including 63 sector/flush cuts represented by 30 actual cut/reboot groups.

Four additional compiled scenarios issue 5 × 8 KiB, 5 × 64 KiB, 65 × 8 KiB and 9 × 64 KiB Writes. They verify pending reads and all 3 MiB of the file after recovery with Copper and original SFS. Both handlers must recover the same number of complete Writes; partial Writes fail. Eight clean Close handoffs pass. These scenarios are included in the buffered totals:

| Policy | Sector/flush cuts | Distinct durable images | Copper/original recovery boots | Interrupted recovery cuts / boots |
| --- | ---: | ---: | ---: | ---: |
| Buffered default | 4,495 | 101 | 202 | 6 / 12 |
| Strict TRST | 1,486 | 76 | 152 | 6 / 12 |

Equivalent durable prefixes share representative recovery boots. Compiled cuts assume atomic sectors and reliable flushes; they do not qualify physical power loss or torn sectors. Host semantic tests separately exercise reordered pending writes.

The first extended-scenario invocation used the standard fixture, while the previous qualification used the bulk fixture. That run passed, but it is additional coverage rather than the matched qualification. All four scenarios were then rerun successfully on the same bulk fixture; the audit now checks each fixture hash explicitly. The standard fixture separately adds 2,961 cut points, 20 durable images, 40 recovery boots and 8 clean Close handoffs, outside the table above.

## Integration and remaining work

The [integrated audit](../artifacts/native-trst-preflight/integrated-audit.json) checks 335 frozen source files, the unchanged 181-file compiler snapshot, exact HUNK identities, qualification receipts and preserved concurrent Exec changes. Seven changed files are integrated in CopperStart, including its private filesystem core and tests. All five handler variants pass allocation-free MC68000 compatibility checks. A fresh live standalone build reproduces SHA-256 `698DBD56AF82F99B0F99B2E108D4056515179290215E0248664B238CB62944DF`.

Buffered TRST remains the default. Successful Writes become visible immediately and durable on successful filesystem ACTION_FLUSH, writer Close, Inhibit or timer commit. Power loss before commit may lose acknowledged Writes. Strict mode retains per-Write durability.

The required full solution build still fails on three pre-existing missing CopperOS shell APIs: LoadSegmentNative, UnloadSegmentNative and UnloadDosProcessSegment. Standalone builds pass; see the [build log](../artifacts/native-trst-preflight/release-build.log).

The [remaining-costs receipt](../artifacts/native-trst-preflight/remaining-costs.json) records the updated method costs and slowest Writes. Continuation validation, metadata access, cache invalidation and allocation remain substantial. Deferred metadata materialization still requires a full design for all reads, mutable views, clone/growth, failed Writes and publication; it is not implemented here. The performance goal remains active.
