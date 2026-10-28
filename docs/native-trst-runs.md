# Native TRST bitmap runs and checked extent reuse

The subsequent [MC68000 arithmetic optimization](native-trst-arithmetic.md) is integrated and adds 7.3–11.3% throughput. It contains the latest benchmark and recovery qualification; the figures below describe the preceding bitmap-run checkpoint.



The native buffered TRST handler improves throughput by **3.8–7.5%** over the [extent-update checkpoint](native-trst-extents.md). It checks preferred allocation runs one bitmap word at a time, reuses already computed bitmap positions, and avoids a duplicate extent-tree search on aligned overwrites. Whole-write atomicity, delayed durability, journal layout and the shared **192 KiB staging cap** are preserved.



Original SFS remains faster. Throughput parity is still unachieved; sampled TRST memory is about 10.1% lower than the original handler.



## Implementation



A bitmap word describes 32 sectors. The preferred append path previously checked these sectors individually. It now checks a mask for each affected word against both current and checkpoint bitmaps, then retains the separate reservation check when reservations exist. Partial words use bounded masks; an allocated bit rejects the preferred run before any modification. The fallback allocator remains available.



The checked bitmap offset and low five block-number bits determine how many sectors remain in that map. Append, allocation, release and original-allocation verification now use that information directly. This removes repeated division and leading-zero calculations while preserving map, volume and reservation boundaries. No cache, staging slot or additional allocation was introduced.



Bitmap positioning now expresses quotient and remainder together. The existing compiler combines them into one unsigned division, removing the 32-step multiplication previously used to reconstruct the remainder. This final change adds **0.75 / 0.94 / 1.46%** throughput over the initial bitmap-run build and reduces its HUNK by 12 bytes. All recovery suites and benchmarks were rerun for this exact binary.

For an aligned overwrite, file traversal has already found the source extent record. Relocation now reacquires that leaf through its normal checked view, using the saved scalar location. No extent mutation intervenes, and no borrowed image pointer survives block I/O. The decoded fields use the existing `out uint` contract. Inspection of the emitted code confirms a widened `move.l` store, avoiding the 16-bit local-store defect documented in the preceding checkpoint without changing the compiler.



The 64 KiB append profile reduces exclusive preferred-append instructions from **66,135 to 16,880**. Overwrite extent-tree searches fall from **787 to 657**. Total measured cycles fall from 18.16 / 3.57 / 23.53 million to **17.49 / 3.32 / 22.41 million** for 8 KiB append, 64 KiB append and 8 KiB overwrite. These are whole-workload profiles; helper costs exclude callees.



## Compiled benchmark



Five cold trials per cell write 1 MiB on the same emulated 68020 at 7,093,790 cycles/s, with 512-byte blocks, 32 filesystem buffers, 512 KiB chip RAM, 512 KiB slow RAM and 2 MiB fast RAM. The device adds no seek or flush delay. All **60 runs** pass independent deep image checking and exact-byte extraction. Driver, fixture, ROM and original SFS binary match the preceding checkpoint.



| Workload | Previous TRST | Updated TRST | Change | Original SFS |

| --- | ---: | ---: | ---: | ---: |

| 8 KiB append | 400.06 KiB/s | 415.38 KiB/s | +3.83% | 518.66 KiB/s |

| 64 KiB append | 2,035.95 KiB/s | 2,189.45 KiB/s | +7.54% | 3,308.45 KiB/s |

| 8 KiB existing-file overwrite | 308.71 KiB/s | 324.10 KiB/s | +4.99% | 2,452.38 KiB/s |



| TRST workload | Previous p50 / p95 Write latency | Updated p50 / p95 |

| --- | ---: | ---: |

| 8 KiB append | 18.825 / 21.349 ms | 18.191 / 20.485 ms |

| 64 KiB append | 20.703 / 70.840 ms | 18.604 / 66.491 ms |

| 8 KiB overwrite | 22.078 / 24.780 ms | 21.074 / 23.877 ms |



Journal and device I/O remain identical in every paired trial. TRST writes **1 / 1 / 2 KiB of journal data** and issues **8 / 8 / 16 flushes** per MiB in these workloads. Maximum sampled retention increases by **64 bytes**, from 237.76 to **237.82 KiB**. This includes native code and mount state and is not an allocation high-water mark. Initial staging storage remains 8,496 bytes for two arenas at 512-byte blocks.



The shared changes also improve buffered CSUN appends to 525.05 and 1,952.59 KiB/s. CSUN overwrite throughput changes slightly downward, to 40.03 KiB/s strict and 39.92 KiB/s buffered. The full [benchmark report](../artifacts/native-trst-runs/v3-benchmark/report.md) records all four policies, latency, I/O, memory and binary identities. Original SFS's in-place overwrites have different data-atomicity guarantees from TRST's copy-on-write or redo path.



## Qualification and integration



The complete host suite passed **314 tests**, with one optional external-image test skipped. Compiled buffered and strict TRST passed **17 and 14 tests**, respectively, plus both five-scenario relocation suites. Both rebuilt CSUN policies passed their bulk-write crash suites. All **11 SFS2 tests** passed, covering 63 sector/flush cut points in 30 representative cut/reboot groups. The three cycle profiles and all 60 benchmark trials also passed.



| Compiled recovery policy | Sector/flush cut points | Distinct durable images | Copper/original recovery boots | Additional interrupted-recovery cuts / boots |

| --- | ---: | ---: | ---: | ---: |

| Buffered default | 1,529 | 81 | 162 | 6 / 12 |

| Strict TRST | 1,486 | 76 | 152 | 6 / 12 |



Equivalent durable prefixes share a representative reboot. Compiled crash tests assume atomic sectors and reliable flushes; they do not qualify physical-device power loss or torn sectors.



Four new host tests run **256 scenarios**, each performing an append and then an overwrite. They cover starts at eight word/map boundary positions, eight lengths from two through 128 blocks, strict and grouped TRST, and both clear and blocked preferred runs. They reconstruct file ownership from the final extent chain, compare every allocation bit across three bitmap blocks, check free-space counts and file bytes after remount, and preserve the neighboring file. Existing ten leaf-layout crash cases and the wider crash suites remain in the test run.



The [319-file source manifest](../artifacts/native-trst-runs/source-v3.json) records the qualified snapshot. Four production files and one new test file are integrated into `CopperStart/external/CopperSFS`; the separate portable core is unchanged by this checkpoint. A fresh standalone build produces the same **158,568-byte HUNK**, SHA-256 `DD4EE27A56B2CB1E7C66C0035405618BEA95484E903C2A42B1753BD1A304E00C`.



The [integrated audit](../artifacts/native-trst-runs/v3-integrated-audit.json) verifies source and binary identities, complete test receipts and unchanged I/O in all 60 paired trials. All five native profiles pass allocation-free MC68000 compatibility checks. Concurrent Exec changes remain untouched and contribute no reachable code to these standalone handlers.



The default remains buffered TRST: successful Writes become visible as complete requests, then durable on successful filesystem `ACTION_FLUSH`, writer Close, Inhibit or timer commit. A power loss before commit may lose acknowledged Writes. `-StrictWrites` retains per-Write durability, and legacy CSUN recovery remains available.



## Remaining costs



General 32-bit multiplication and division still use software loops on the MC68000 target when compile-time bounds are unavailable. An isolated compiler experiment is testing native partial products and guarded hardware division. Checksums, block copying and extent traversal also remain substantial costs. No compiler experiment is included in this qualified default.

The standalone builds pass. The required full `CopperStart.slnx` Release build still fails on the same three CopperOS shell references to missing `LoadSegmentNative`, `UnloadSegmentNative` and `UnloadDosProcessSegment` APIs; see the [build log](../artifacts/native-trst-runs/v3-release-build.log). No release package or installed handler was changed.

