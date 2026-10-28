# Native TRST MC68000 arithmetic

The native buffered TRST handler is **7.3–11.3% faster** than the [bitmap-run checkpoint](native-trst-runs.md). General 32-bit arithmetic was still using bit-at-a-time software loops in CopperSharp's MC68000 output. Replacing multiplication with native partial products and adding a guarded hardware division path improves both TRST and CSUN without changing their journal protocols.

Whole-write atomicity, the shared **192 KiB staging cap**, delayed durability and legacy CSUN recovery remain unchanged. Original SFS remains faster; the throughput goal is still unmet.

## Change and correctness

For unchecked 32-bit multiplication, three `MULU.W` instructions compute the low product and the two cross terms. The high/high term cannot affect the low 32 bits. This replaces the 32-step shift/add loop and preserves signed and unsigned wraparound semantics and the existing scratch-register contract. Narrow multiplication and later CPU targets retain their existing paths.

The unsigned division core now uses `DIVU.W` when the divisor fits 16 bits and the dividend's high word is less than the divisor. This proves that the packed hardware quotient fits before executing the instruction. The result is unpacked into the existing quotient and remainder registers. Other operands retain the full-width loop; the zero-divisor handling and signed wrappers are preserved. Shared helpers keep their declared register clobbers.

These changes are in `CopperSharp68k/Compiler/Backend/M68kCodeGenerator.cs`. Two existing code-generation assertions now expect the native instructions. A new execution corpus checks products, signed and unsigned quotients/remainders, combined quotient/remainder results, word boundaries, the hardware quotient-overflow boundary, and deterministic random inputs. The tests use both optimizer modes and explicitly verify that the shared-helper option is active when requested. Results, remainder stores, input canaries and stack balance are checked against independent host calculations.

Isolated compiler builds separate the effects. Multiplication alone improves the three profiles by **3.7 / 2.6 / 6.5%**; division alone improves them by **4.4 / 2.4 / 3.9%**. Combined profiles improve by **9.1 / 7.4 / 11.3%**, with identical I/O. A rebuilt baseline produces the exact preceding default HUNK, controlling for unrelated compiler changes. The [comparison](../artifacts/native-trst-arithmetic/comparison.json) and emitted instruction listings retain this evidence.

## Compiled benchmark

Five cold trials per cell write 1 MiB on the same emulated 68020 at 7,093,790 cycles/s, with 512-byte blocks, 32 filesystem buffers, 512 KiB chip RAM, 512 KiB slow RAM and 2 MiB fast RAM. No device seek or flush latency is added. All **60 runs** pass independent image checks and exact-byte extraction. The driver, fixture, ROM and original handler are unchanged.

Durable throughput, in KiB/s:

| Workload | Previous TRST | Updated TRST | Gain | Strict CSUN | Buffered CSUN | Original SFS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 415.38 | 453.17 | +9.10% | 80.55 | 577.53 | 518.67 |
| 64 KiB append | 2,189.45 | 2,350.28 | +7.35% | 696.53 | 2,147.46 | 3,326.16 |
| 8 KiB existing-file overwrite | 324.10 | 360.83 | +11.33% | 42.44 | 42.21 | 2,452.18 |

| TRST workload | Previous p50 / p95 Write latency | Updated p50 / p95 |
| --- | ---: | ---: |
| 8 KiB append | 18.191 / 20.485 ms | 16.566 / 18.884 ms |
| 64 KiB append | 18.604 / 66.491 ms | 17.005 / 62.403 ms |
| 8 KiB overwrite | 21.074 / 23.877 ms | 18.859 / 21.327 ms |

Every paired trial retains the same I/O counters. TRST writes **1 / 1 / 2 KiB of journal data** and issues **8 / 8 / 16 flushes** per MiB. The native HUNK grows by **3,072 bytes**. Maximum sampled retention rises from **237.82 to 240.79 KiB**, about **9% below original SFS**. This measures code and retained mount state at checkpoints, not an allocation high-water mark. Initial staging storage remains 8,496 bytes and the shared capacity limit remains 192 KiB.

Original SFS overwrites existing data in place; its result does not demonstrate the same whole-data replacement guarantee as TRST. The [full benchmark](../artifacts/native-trst-arithmetic/benchmark/report.md) records all policies, latency, memory, I/O and binary identities.

## Qualification and integration

The candidate passed **1,463 compiler tests**, plus the new 28-case HUNK corpus against both baseline and candidate compilers. The latter runs hundreds of operand pairs per case and explicitly covers inline and shared division. All **79 compiled MC68000 buffer and codec tests** passed with the new compiler and the current frozen native sources.

Compiled buffered and strict TRST passed **17 and 14 tests**, respectively, plus both five-scenario relocation suites. Both rebuilt CSUN policies passed their bulk-write crash suites. All **11 SFS2 tests** passed, including 63 sector/flush cut points in 30 representative cut/reboot groups. All nine attribution profiles and 60 benchmark trials passed.

| Compiled recovery policy | Sector/flush cut points | Distinct durable images | Copper/original recovery boots | Additional interrupted-recovery cuts / boots |
| --- | ---: | ---: | ---: | ---: |
| Buffered default | 1,529 | 81 | 162 | 6 / 12 |
| Strict TRST | 1,486 | 76 | 152 | 6 / 12 |

Equivalent durable prefixes share a representative reboot. These compiled tests assume atomic sectors and reliable flushes; they do not qualify physical power loss or torn sectors.

The 319-file native source snapshot is unchanged from the preceding checkpoint, whose host suite passed 314 tests with one optional external-image skip. This change integrates one compiler implementation file, updates one test file and adds one test file. The [integrated audit](../artifacts/native-trst-arithmetic/integrated-audit.json) verifies the compiler source snapshot, unchanged native sources, qualification receipts, binary identities and unchanged I/O. Existing unrelated work is preserved.

A fresh normal standalone build reproduces the qualified **161,640-byte HUNK**, SHA-256 `E9B13B6AAAA19F47BE1C3B581F79FCA07E59A05B4F001C8A5AF95075AEF0D398`. All 33 focused tests run after integration pass. All five native profiles pass allocation-free MC68000 compatibility checks. No release package or installed handler was changed.

The default remains buffered TRST: complete Writes become visible on success and durable on successful filesystem `ACTION_FLUSH`, writer Close, Inhibit or timer commit. Power loss before commit may lose acknowledged Writes. `-StrictWrites` retains per-Write durability.

The required full solution build still fails on the three pre-existing CopperOS shell references to missing `LoadSegmentNative`, `UnloadSegmentNative` and `UnloadDosProcessSegment` APIs; see the [build log](../artifacts/native-trst-arithmetic/release-build.log). Standalone builds pass.

## Remaining cost

The overwrite profile attributes about **12.6%** of total cycles to checksums and **10.3%** to block copying. TRST overlay reads validate a found image repeatedly, whereas the dirty-buffer and ordinary cache paths retain verification state. Two earlier [associative checksum caches](native-trst-group.md#rejected-cache-experiments) regressed strict appends because their bookkeeping cost exceeded the savings. A per-slot approach would need to avoid that overhead, revoke verification on every mutation, preserve arena lifetimes, and improve complete compiled workloads before promotion. No such cache is included in this checkpoint, and no performance improvement from it is claimed.
