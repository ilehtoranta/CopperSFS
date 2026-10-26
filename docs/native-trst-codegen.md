# Native TRST compiler profile

The standalone handler now uses the compiler's freestanding code optimization profile. Relative to the [transfer setup checkpoint](native-trst-transfer.md), durable throughput improves by **3.21%** for 8 KiB append, **4.33%** for 64 KiB append, and **3.37%** for overwrite. The HUNK is **9.4% smaller**, and sampled RAM is **11.0% below original SFS** on the overwrite workload.

Overwrite still reaches only **47.4% of original SFS throughput**. The overall throughput parity target remains unmet. This configuration issue accounts for a measurable but small portion of the speed gap.

## Configuration issue and measured choice

Build-SfsHandler.ps1 previously supplied `--rom-size-optimizations on` while emitting a freestanding HUNK. The compiler's SupportsCodeSizeProfile condition enables that ROM policy for the ROM runtime; freestanding HUNKs require CodeSizeOptimizations. As a result, the old setting did not activate the relevant call, aggregate and generated-code transformations for this handler.

The build now supplies `--code-size-optimizations on` with an explicit pass selection. It enables unused register-argument removal, incoming aggregate-home reuse, call clustering, private scalar/memory helper inlining, read-only aggregate forwarding, redundant transport removal, private stack-copy folding, compact guest accesses, narrow-operation cleanup and redundant initialization removal. It omits shared-return branches, identical-method thunks, shared arithmetic cores and size-first clearing loops. The compiler itself is unchanged.

Three controls use the exact same managed input DLLs and pinned compiler, varying only compiler options. Nine diagnostic runs check output images and exact bytes. All have identical device I/O to the previous baseline. The selected profile is both smaller and faster than the broad defaults:

| Compiler profile | HUNK bytes | 8 KiB append gain | 64 KiB append gain | Overwrite gain |
| --- | ---: | ---: | ---: | ---: |
| General defaults | 166,780 | +0.52% | +0.86% | +0.71% |
| Call and aggregate options | 169,896 | +0.89% | +1.30% | +1.29% |
| Selected explicit profile | 155,708 | +3.21% | +4.32% | +3.37% |

The selected overwrite profile falls from 6,457,453 to 6,246,743 guest cycles. Its map records 372 indexed guest accesses, 118 compact guest-memory rewrites, 144 transport rewrites, 19 private stack-copy rewrites, two private memory-helper rewrites and 844 redundant initialization removals. These are compiler rewrite counts, not independent timing gains or heap-allocation savings. The live handler's staging and state layouts are unchanged.

The selected control and the qualified source build produce identical HUNK bytes. Their maps differ only in the managed Core/Dos input build identities. The paired inclusive call traces finish with zero unmapped entries and zero open frames. Profiling reads emulated RAM and registers without adding guest bus traffic or cycles; parent and child timings overlap and must not be summed. See the [profile evidence](../artifacts/native-trst-codegen/profile-evidence.json).

## Paired benchmark

Five cold trials per cell write 1 MiB on the same emulated 68020 at 7,093,790 cycles/s, with 512-byte blocks, 32 buffers, 512 KiB chip RAM, 512 KiB slow RAM and 2 MiB fast RAM. Copper handlers target MC68000; original SFS requires 68020. No physical seek or flush latency is added. Driver, emulator, ROM, fixture and original handler are unchanged. All 60 trials pass independent image checks and exact-byte extraction. Every paired I/O counter matches the previous checkpoint.

Durable throughput includes open, Writes, Close and the final successful checkpoint, in KiB/s:

| Workload | Previous TRST | Updated TRST | Change | Strict CSUN | Buffered CSUN | Original SFS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 645.34 | 666.05 | +3.21% | 83.85 | 649.15 | 518.67 |
| 64 KiB append | 3307.29 | 3450.39 | +4.33% | 752.75 | 2679.99 | 3326.51 |
| 8 KiB existing-file overwrite | 1124.91 | 1162.84 | +3.37% | 44.61 | 44.44 | 2452.35 |

TRST Write latency, sampled memory and journal traffic:

| Workload | p50 ms | p95 ms | Maximum ms | Maximum sampled RAM KiB | Journal writes KiB | Flushes |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 11.002 | 13.544 | 26.096 | 235.27 | 1.0 | 8 |
| 64 KiB append | 11.899 | 26.601 | 26.601 | 235.27 | 1.0 | 8 |
| 8 KiB existing-file overwrite | 5.001 | 7.813 | 70.169 | 235.27 | 0.5 | 4 |

The HUNK shrinks by 16,088 bytes, from 171,796 to 155,708. Memory samples include retained code, stack, mount records and buffers at probe checkpoints; they are not allocation high-water marks. The [full benchmark](../artifacts/native-trst-codegen/benchmark/report.md) includes each policy's latency and memory results.

Original SFS overwrites existing data in place, avoiding the mapping and bitmap work required for TRST's atomic replacement. These data guarantees differ, as established in the [parity investigation](native-trst-parity.md). That distinction does not change the outstanding performance target.

## Qualification

All 103 compiled MC68000 instruction tests pass with the selected code-generation settings, including transfer boundaries, DMA restrictions, MaxTransfer splitting, bounce buffers, TD64, block sizes, partial I/O, cache collisions and hash/LRU integrity. Six cases invoke the exact handler's private leaf-edit method and compare it with an independently recomputed full checksum, including halfword alignment and carries. The audit verifies that the probe's enabled pass set matches every standalone variant's map.

The first instruction invocation omitted the handler-path environment variable, so it passed 97 cases and skipped the handler-specific theory. That run is not qualification evidence. The completed run under buffers-qualified supplies the profiled HUNK and passes all 103 cases without skips.

The runtime source and filesystem core host tests are unchanged. Existing evidence for 469 host tests, with one optional external-image skip, is reused after checking every frozen core-source hash; no new host-test run is claimed.

Buffered and strict TRST pass 17 and 14 native tests, plus both five-scenario relocation suites. Both CSUN variants pass their bulk-write crash suites. All 11 SFS2 tests pass, including 63 sector/flush cuts represented by 30 actual cut/reboot groups.

Four additional compiled scenarios issue 5 × 8 KiB, 5 × 64 KiB, 65 × 8 KiB and 9 × 64 KiB Writes. They check pending reads and all 3 MiB of the file after recovery with both handlers. Both handlers must agree on the number of complete recovered Writes; partial Writes fail. Eight clean Close handoffs pass. These scenarios are included in the buffered totals:

| Policy | Sector/flush cuts | Distinct durable images | Copper/original recovery boots | Interrupted recovery cuts / boots |
| --- | ---: | ---: | ---: | ---: |
| Buffered default | 4,495 | 101 | 202 | 6 / 12 |
| Strict TRST | 1,486 | 76 | 152 | 6 / 12 |

Equivalent durable prefixes share representative recovery boots. Compiled tests assume atomic sectors and reliable flushes; they do not qualify physical power loss or torn sectors. Existing host tests separately cover reordered pending writes.

## Integration and remaining work

The build script and instruction-test configuration are integrated into CopperStart. The [integrated audit](../artifacts/native-trst-codegen/integrated-audit.json) checks 334 frozen source files, the unchanged 181-file compiler snapshot, exact HUNK identities, qualification receipts and preserved unrelated Exec changes. All five variants pass allocation-free MC68000 compatibility checks. A fresh live standalone build reproduces the 155,708-byte HUNK, SHA-256 `7C3A8CDC0219BD6BA9648D14A8915665AEECAC91A9B270ED40508A0B2F684B8E`.

The 192 KiB shared staging cap, whole-write atomicity, delayed durability, journal encoding and legacy CSUN recovery are unchanged. Buffered TRST remains the default: successful Writes are visible immediately and become durable on successful filesystem ACTION_FLUSH, writer Close, Inhibit or timer commit. Power loss before commit may lose acknowledged Writes. Strict mode retains per-Write durability.

The required full solution build still fails on three pre-existing missing CopperOS shell APIs: LoadSegmentNative, UnloadSegmentNative and UnloadDosProcessSegment. Standalone builds pass; see the [build log](../artifacts/native-trst-codegen/release-build.log). No installation, package release, commit or public-core synchronization was performed.

Continuation validation, generic workspace checks, metadata access and allocation work remain the main targets. The [remaining-costs receipt](../artifacts/native-trst-codegen/remaining-costs.json) records the measured methods and slowest Writes. Deferring metadata edits needs a complete design for reads, mutation, clone/growth, failed Writes and publication; it is not implemented in this checkpoint.
