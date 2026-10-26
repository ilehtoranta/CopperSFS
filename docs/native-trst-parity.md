# Native TRST: unpublished data, retained views and encoding — 2026-10-25

Follow-up: [fewer copies, encoder instructions and staging allocations](native-trst-working-set.md).

The compiled native handler now delivers **172.26 KiB/s for 8 KiB appends, 1,132.22 KiB/s for 64 KiB appends, and 152.97 KiB/s for 8 KiB overwrites**. These are **6.24×, 25.48× and 4.84×** the [previous codec build](native-trst-codec.md), rerun with the same benchmark driver. All 60 final trials pass independent image and byte checks.

Original SFS remains faster. The parity target is **not achieved**. Its buffered acknowledgements and in-place overwrites provide different data guarantees; enforcing filesystem flushes after each original-handler Write narrows the append gap but does not remove it.

This work changes the **native embedded CopperSFS core in the sibling CopperStart checkout** and its native platform. The public CopperSFS core retains the preceding algorithm. The source manifest and HUNK hashes below identify the implementation being measured.

## What changed

Full aligned append runs of at most 64 KiB can write their payload directly into blocks proven free in the original durable bitmap. Full aligned overwrites of that size can relocate into a contiguous free run, then publish the replacement mapping through ordinary SFS TRST metadata records. If the run is unavailable, the ordinary whole-data redo path remains available. Partial and larger operations retain their existing staging path and capacity checks.

The original-free check matters: a block freed only by the staged transaction may still contain reachable old data. Such a block cannot bypass redo. Unpublished writes are flushed with the complete journal before the TRFA marker makes the new mapping recoverable. A failed operation before publication can leave bytes only in blocks the old filesystem still considers free.

The native platform consumes these short unpublished runs directly, avoiding the physical write queue's copy/sort/drain cost. Retained journal and home images also have an immediate-write path. Both preserve overlapping queued-write ordering, DMA bounce requirements, transfer limits, wide offsets and explicit flush barriers. The bounded metadata cache retains new home images across flushes.

Metadata edits use a private arena image once staged. First edits copy directly from verified cache views into that arena; publishing the same arena image avoids a self-copy. Encoding can read an old retained cache image directly. Raw cache views perform no I/O, never grant checksum verification, and decline access when an active logical overlay would take precedence. Cache misses use the original block-read path.

The encoder dispatches once per compression run, with a short loop for unchanged words. It still reads every original/final longword exactly once, uses the original four LONGCOMPRESSION modes and 64-word run limit, and keeps its plan within the existing bitmap scratch block. Record headers use word/longword stores when they fit in the page; boundary handling remains byte-safe. Iterating arena slots by pointer avoids repeated software multiplication. Zero-filled marker and final-page tails need not be summed repeatedly.

Bitmap range changes update whole words and their checksums. A sequential relocated overwrite can extend the preceding replacement extent and advance the old tail record in place instead of splitting, removing, reinserting and merging tree records. The generic path remains for other extent shapes. CSUN retains its allocation/undo ordering.

Root and container checks consume verified views without scratch copies. When recovery finds a clean marker, preparation reuses the roots just selected in that operation; replay still causes a fresh root selection. No root descriptor is trusted across operations or media changes.

## Memory and atomicity

The arena remains **196,608 bytes**, with a **24-byte header, 2 KiB index and 377 slots** at 512-byte blocks. Its layout, maximum allocation and whole-operation capacity failure behavior are unchanged. There is no payload-sized allocation or automatic transaction splitting. The plan uses at most `BlockBytes / 4` bytes in an existing scratch block. All four compiled handler profiles report no managed allocation sites.

Maximum sampled guest retention rises from **491.41 to 498.23 KiB**, including code, stack, mount records, caches and arena. This is a **6.81 KiB** increase; it is not a transient allocation high-water mark. Classic CSEB/version 12, SFS2 CSEC/version 13 and the 2,320-byte mount state remain unchanged.

The four commit barriers remain: flush unpublished data and the complete journal; publish and flush TRFA; apply and flush home images; write and flush TROK. Recovery validates the entire journal before applying it. A published operation can roll forward even if the interrupted call reported failure. Each successful Write packet remains durable and atomic as a whole. Legacy CSUN recovery remains available.

## Compiled benchmark

Five cold trials per handler/workload write 1 MiB. Rates use emulated guest cycles at 7,093,790 cycles/s on the same emulated 68020, with 512 KiB chip, 512 KiB slow and 2 MiB fast RAM, 512-byte blocks and 32 buffers. Copper HUNKs target 68000; original SFS 1.279 requires 68020. The emulator adds no seek, rotation or flush delay.

Durable throughput includes open, Write packets, Close and the final successful Inhibit. The existing-file workload replaces the first MiB of a 3 MiB file within its first extent; independent extraction verifies the remaining 2 MiB too. Payload byte `i` is `(i * 17 + 5) & 255`. The separate crash suite exercises fragmented and middle-of-extent relocation.

| Workload | Previous TRST KiB/s | Current TRST KiB/s | Factor | Strict CSUN | Buffered CSUN | Original SFS |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 27.62 | **172.26** | **6.24×** | 74.28 | 505.07 | 518.54 |
| 64 KiB append | 44.43 | **1,132.22** | **25.48×** | 635.93 | 1,806.17 | 3,332.43 |
| 8 KiB overwrite | 31.57 | **152.97** | **4.84×** | 39.95 | 39.84 | 2,454.79 |

| TRST workload | Write p50 before → after, ms | Write p95 before → after, ms |
| --- | ---: | ---: |
| 8 KiB append | 292.43 → **44.86** | 295.96 → **47.40** |
| 64 KiB append | 1,419.53 → **48.16** | 1,499.46 → **99.41** |
| 8 KiB overwrite | 253.87 → **49.91** | 257.08 → **59.87** |

| TRST workload, per MiB | Total writes KiB, before → after | Journal writes KiB, before → after | Read sectors, before → after | Flushes, unchanged |
| --- | ---: | ---: | ---: | ---: |
| 8 KiB append | 2,563 → 1,475 | 1,152.5 → 64.5 | 8,152 → 8 | 516 |
| 64 KiB append | 2,155 → 1,083 | 1,080.5 → 8.5 | 3,080 → 8 | 68 |
| 8 KiB overwrite | 2,304 → 1,387 | 1,088 → 64 | 4,592 → 8 | 512 |

Journal counts exclude transaction markers and home images; total writes include them. Journal reads fall to zero in these three workloads. The 64 KiB journal-write reduction is **99.21%**. These are requested sector transfers, not controller-internal write amplification.

The [complete benchmark](../artifacts/native-trst-parity/cow-v7-benchmark/report.md) includes all four handlers' latency, I/O and memory tables. The [baseline rerun](../artifacts/native-trst-parity/baseline-rerun/report.md) uses the identical test, emulator and compiler assemblies. The unchanged original handler varies by at most 0.56% in throughput between those two sets.

## Why original SFS is still faster

Original SFS commits buffered metadata for many Write packets together. The normal append measurements issue four flush commands for the whole MiB, versus 516 or 68 for TRST. It also overwrites existing file data in place, avoiding the mapping and bitmap work needed for atomic replacement.

An additional diagnostic sends **ACTION_FLUSH to the filesystem after every Write** and includes that call in the measured interval. Original throughput becomes **250.75, 1,838.15 and 1,485.83 KiB/s** for the three workloads. This confirms both a durability-policy advantage and remaining implementation overhead. The original overwrite path still lacks TRST's whole-data replacement guarantee, so this is not an equivalent atomicity control. A DOS file-buffer Flush alone did not impose the filesystem barrier and was not used for this comparison.

The final 8 KiB append diagnostic spends 42.17 million cycles, down from 49.09 million at the intermediate copy-on-write checkpoint. CopyBlock calls fall from 4,411 to 2,994 and its exclusive cycles from 10.32 to 6.13 million. Planner cycles fall from 4.05 to 3.65 million. Copies, checksum scans, compression and metadata/cache bookkeeping remain measurable costs.

A separately compiled experiment routed aligned copies through Kickstart's actual CopyMemQuick. Its three full-workload timings were within 0.06% of the direct-copy version, with no improvement, so the production copy implementation was retained. The older instruction-only Exec service comparison is not evidence of Kickstart vector performance.

## Qualification status

| Check | Final result |
| --- | --- |
| Compiled classic TRST | 12 passed, no skips; crash cuts, interrupted recovery, original-handler replay, legacy CSUN migration, capacity/reuse, shared and concurrent handles, A500 integration |
| Compiled relocation | All five scenarios passed: fragmented, contiguous, middle-of-extent, successive packets and an 8 KiB packet; Copper and original SFS recovery |
| Compiled SFS2 TRST | 11 passed, no skips; 68000/68020 mutation/remount, original redo, arena reuse and wide-device operations |
| Compiled strict/buffered CSUN | Both bulk-append crash cases passed |
| Compiled MC68000 buffer/codec | 68 passed, no skips; includes raw-view precedence, invalid checksums, DMA, queue ordering, alignment and bounds |
| Embedded core | 287 passed; one optional external-image test skipped |
| Benchmark | All 60 final trials and 60 baseline rerun trials passed independent image and exact-byte checks |
| Four standalone builds | Passed; allocation-free compatibility reports |

The final classic and relocation receipts cover **1,488 writer sector/flush cut points**, represented by **76 distinct durable images and 152 recovery boots** across Copper and original SFS. They also cover **6 interrupted-recovery points in 12 boots**. SFS2 covers **63 sector/flush cut points in 30 cut/reboot groups**. The sparse wide-device cases inspect affected structures rather than the complete allocation graph.

These checks assume atomic sectors and reliable flushes. Equivalent durable prefixes share representative reboot runs; receipts retain the full cut coverage. They qualify compiled HUNKs on emulated hardware, not physical-device power loss or a new extension-ROM/media release.

The full CopperStart Release solution currently fails in the shared CopperOS Shell project: `LoadSegmentNative`, `UnloadSegmentNative` and `UnloadDosProcessSegment` are missing from its DOS platform API. The filesystem buffer cases pass unchanged in an isolated project against the frozen candidate. The earlier qualified copy-on-write checkpoint passed the full solution build before that workspace mismatch appeared. This is not reported as a successful final solution build.

## Reproduction and source boundary

Artifacts are under `artifacts/native-trst-parity/`. The [312-file manifest](../artifacts/native-trst-parity/source-cow-v7.json) pins the sibling CopperStart `artifacts/native-trst-parity/source-cow-v7` snapshot. It includes coordinated changes to the shared DOS/Exec clear/copy paths and FFS read copy; those are preserved and are not attributed to TRST. The native TRST implementation is in the snapshot's embedded `external/CopperSFS/src/CopperSFS.Core/` directory.

Build from the frozen `build/Build-SfsHandler.ps1` with an explicit `CopperSharpRoot`; CSUN uses `-LegacyCsUn` and optionally `-StrictWrites`, while SFS2 uses `-Sfs2 -Sfs2TrstWrites`. Use `scripts/Test-NativeTrst.ps1` for classic qualification; run `CompiledTrstRelocationCutsRecoverWithCopperAndOriginal` separately with the derived bulk fixture. Use `scripts/Test-NativeSfs2.ps1 -Trst` for SFS2 and `scripts/Measure-NativeWritePolicies.ps1 -Trials 5` for the comparison. Explicit test-assembly, fixture and handler arguments prevent rebuilding test dependencies into a measured run.

The comparison driver is `9C5EC415D4912A48374DFF2DC451221DB7281BA88D550767C4AA5EF5DED3D911`. The paired runs also pin the emulator, CPU and compiler assemblies in their `run.json` files.

The [final evidence audit](../artifacts/native-trst-parity/final-audit-v7.json) verifies all 312 frozen/live source hashes, each tested/benchmarked/profiled HUNK identity, the native compatibility reports, test results, cut receipts and comparison inputs. It also confirms unchanged TRST I/O between the intermediate copy-on-write checkpoint and the final view/encoder optimizations. Standalone qualification passes; the separate full-solution build failure is explicitly retained in the audit.

| Profile | HUNK bytes | SHA-256 |
| --- | ---: | --- |
| TRST | 152,068 | `EED50279DB846055F30C301E24DCD80A20FFDDA05171F0FA3C7332189BA75DEC` |
| Strict CSUN | 155,136 | `1AD58839D7F552922EE0D2A4A21A3C16703AA25A088410C4C18AD2F9BC6121D9` |
| Buffered CSUN | 155,360 | `A7C6E6137608257EA9CCA31D061E65DC05EF05A02E4D9CBD447127192BE698A6` |
| SFS2 TRST | 204,432 | `2B457B9F6747C26FDF79B79312BA35F5AB26C9756A58E619A81366B22901CA18` |

No generated binary, licensed media, installation or package release is part of this source change. Native TRST remains the development default; the performance-parity goal remains open.
