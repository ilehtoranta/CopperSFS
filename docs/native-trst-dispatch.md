# Native TRST write setup and checksum updates

Buffered TRST improves durable throughput by **2.62%** for 8 KiB append, **2.51%** for 64 KiB append, and **6.12%** for existing-file overwrite, relative to the [durable-free allocation checkpoint](native-trst-safe-runs.md). Overwrite reaches **44.2% of original SFS throughput**. The overall parity target remains unmet.

The 192 KiB shared staging cap, whole-write atomicity, delayed durability, journal format and legacy CSUN recovery are unchanged. All paired benchmark I/O counters match the previous checkpoint.

## Changes and evidence

The native Write path now initializes its 152-byte work record directly in the caller's stack area. Previously the aggregate-return ABI copied that record through temporary stack areas. Every field is still reset on each Write; there is no persistent work-record cache. The request record is also initialized in place. The compiler-reported WriteSmartFile frame shrinks from 380 to 204 bytes, with the same saved-register footprint. This change alone improves the compiled overwrite profile by 4.13%.

Consecutive relocation writes now accumulate one checksum adjustment for the extent leaf's known edits, instead of reading and resealing its checksum for each field. Existing checks still validate the old keys, lengths, links, record layout, arena identity, allocation maps and safe destination before data I/O. After a successful unpublished transfer, the edits touch only stable owned images and cannot fail. Verification is revoked before those edits and restored afterwards. No borrowed metadata pointer is retained across I/O.

The checksum adjustment handles aligned and halfword-aligned records separately. For a halfword-aligned longword, it subtracts the two rotated values; rotating their difference would mishandle a carry across bit 16. Independent full-block checksum recomputation verifies the optimized result for both alignments, different record strides, carries, maximum extent lengths and a missing next record.

Together the changes reduce the compiled overwrite profile from 7,106,674 to 6,696,893 guest cycles, a 6.12% throughput improvement. The [profile evidence](../artifacts/native-trst-dispatch/profile-evidence.json) includes separate initialization and checksum measurements. The checksum change contributes a further 1.91% after direct initialization. Append profiles improve by about 2.5–2.6%.

An opt-in call profiler now records inclusive native method times using read-only snapshots of emulated RAM and registers. It adds no guest bus operations, instructions or cycles. All three accepted native call traces end with zero unmapped entries and zero open frames. Inclusive parent and child times overlap and must not be summed. Original SFS's diagnostic map contains address buckets rather than function entries, so no original-handler call trace is claimed.

Compiling the unchanged baseline for 68020 improves its overwrite profile by about 4.7%; the remaining gap is much larger than this ISA effect. That build is a diagnostic control only. The integrated handler continues to target MC68000.

## Paired benchmark

Five cold trials per cell write 1 MiB on the same emulated 68020 at 7,093,790 cycles/s, with 512-byte blocks, 32 buffers, 512 KiB chip RAM, 512 KiB slow RAM and 2 MiB fast RAM. Copper handlers target MC68000; original SFS requires 68020. No physical seek or flush latency is added. Driver, emulator, ROM, fixture and original handler are unchanged. All 60 trials pass independent image checks and exact-byte extraction.

Original SFS overwrites existing data in place, avoiding the mapping and bitmap work required for TRST's atomic replacement. The throughput comparison therefore covers different data guarantees, as established in the [earlier parity investigation](native-trst-parity.md). This does not change the outstanding performance target.

Durable throughput includes open, Writes, Close and the final successful checkpoint, in KiB/s:

| Workload | Previous TRST | Updated TRST | Change | Strict CSUN | Buffered CSUN | Original SFS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 615.73 | 631.86 | +2.62% | 81.01 | 614.03 | 518.66 |
| 64 KiB append | 3191.40 | 3271.35 | +2.51% | 721.63 | 2543.40 | 3326.51 |
| 8 KiB existing-file overwrite | 1022.13 | 1084.67 | +6.12% | 42.68 | 42.50 | 2452.20 |

TRST Write latency, sampled memory and journal traffic:

| Workload | p50 ms | p95 ms | Maximum ms | Maximum sampled RAM KiB | Journal writes KiB | Flushes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 11.571 | 14.102 | 27.039 | 250.30 | 1.0 | 8 |
| 64 KiB append | 12.437 | 27.532 | 27.532 | 250.30 | 1.0 | 8 |
| 8 KiB existing-file overwrite | 5.406 | 9.425 | 70.551 | 250.30 | 0.5 | 4 |

Sampled RAM remains about **5.3% below original SFS**. The handler grows by 248 bytes; staging capacity is unchanged. Memory samples measure retained allocations at probe checkpoints, not allocation high-water marks. The [full benchmark](../artifacts/native-trst-dispatch/benchmark/report.md) includes every policy's latency and memory measurements.

## Qualification

The frozen candidate passes 469 host tests, with one optional external-image test skipped, and 86 compiled MC68000 instruction tests. Six new host cases and six new instruction cases compare the optimized leaf edit against an independent full checksum. Instruction cases execute the private concrete method from the exact handler HUNK; each runs on both 512-byte and 4096-byte images, checks surrounding bytes, unchanged hints, restored callee-saved registers and stack balance. They use no host gateways.

Buffered and strict TRST pass 17 and 14 native tests, plus both five-scenario relocation suites. Both CSUN variants pass their bulk-write crash suites. All 11 SFS2 tests pass, including 63 sector/flush cuts represented by 30 actual cut/reboot groups.

Four additional compiled scenarios issue 5 × 8 KiB, 5 × 64 KiB, 65 × 8 KiB and 9 × 64 KiB Writes. They check pending reads and then independently verify all 3 MiB of the file with both recovery handlers. Both handlers must agree on the number of complete recovered Writes; partial Writes fail. Eight clean Close handoffs also pass. These scenarios are included in the buffered totals:

| Policy | Sector/flush cuts | Distinct durable images | Copper/original recovery boots | Interrupted recovery cuts / boots |
| --- | ---: | ---: | ---: | ---: |
| Buffered default | 4,495 | 101 | 202 | 6 / 12 |
| Strict TRST | 1,486 | 76 | 152 | 6 / 12 |

Equivalent durable prefixes share representative recovery boots. Compiled tests assume atomic sectors and reliable flushes; this is not physical power-loss or torn-sector qualification. Host tests separately cover reordered pending writes.

Two initial harness invocations started before the new HUNK existed and failed their input-file checks. They are retained under `buffers` and `calls-v4-overwrite`. The completed runs under `buffers-qualified` and `calls-v4-qualified-overwrite` pass without changing the candidate.

## Integration and remaining work

Six implementation and test files are integrated into CopperStart. The [integrated audit](../artifacts/native-trst-dispatch/integrated-audit.json) verifies 333 frozen source files, the unchanged 181-file compiler snapshot, exact handler identities, qualification receipts and preserved unrelated Exec changes. All five profiles pass allocation-free MC68000 compatibility checks. A fresh live standalone build reproduces the 171,464-byte HUNK, SHA-256 `48AB43B45951707721BFD614E0116194020A192FF3729F51434EF927BF12023A`.

Buffered TRST remains the default: successful Writes become visible immediately and become durable on successful filesystem ACTION_FLUSH, writer Close, Inhibit or timer commit. Power loss before commit may lose acknowledged Writes. Strict mode retains per-Write durability.

The required full solution build result is **three pre-existing missing CopperOS shell APIs**; see the [build log](../artifacts/native-trst-dispatch/release-build.log). Standalone builds pass. No installation, package release, commit or public-core synchronization was performed.

The largest remaining costs are the continuation path, native transfer and cache invalidation, repeated metadata access and allocation searches. Block copying accounts for about 3.2% of this overwrite profile, so further copy-loop tuning alone cannot close the remaining gap. The [remaining-costs receipt](../artifacts/native-trst-dispatch/remaining-costs.json) records the measured methods and slowest Writes for the next investigation.
