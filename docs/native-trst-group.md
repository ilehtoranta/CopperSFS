# Buffered TRST default and rejected checksum caches

The subsequent [extent-update optimization](native-trst-extents.md) is now integrated. It improves overwrite throughput by another 23.3%; that report contains the latest benchmark and recovery qualification. The measurements below describe the original buffered-default checkpoint.

Classic standalone native development builds now default to buffered TRST. It improves measured throughput by **55–117%** over the [previous strict checkpoint](native-trst-mount-cache.md), preserves whole-write atomicity, and uses **237.13 KiB** of sampled handler memory. It still falls short of original SFS throughput. Two checksum-cache prototypes were rejected because they made strict appends slower.

**The default now acknowledges whole Writes before durability.** A successful buffered Write makes the complete request visible to other handles; it becomes durable on a successful filesystem `ACTION_FLUSH`, writer Close, Inhibit or timer commit. A power loss before that commit may lose acknowledged Writes. Use `-StrictWrites` to commit every successful Write before returning. `dos.library/Flush(file)` alone may flush only the DOS stream, without sending the filesystem barrier. Existing downloads and the ROM-extension/SFS2 policies are unchanged.

## Bounded group implementation

Two indexed staging arenas share one allocation. Before each Write, the inactive arena receives the previously accepted metadata. The current request then runs against that private copy. Success exchanges the arenas; failure restores the earlier accepted view and reports zero bytes. A second bitmap check prevents a failing request from overwriting unpublished data belonging to earlier accepted Writes.

The initial allocation is **8,496 bytes at 512-byte blocks**, containing two eight-slot arenas and a 64-byte descriptor. The existing **192 KiB cap** covers both arenas together. Larger packets, a missing writeback timer, and exhausted group capacity use the strict whole-request path. It temporarily borrows the complete allocation, grows only before publication, and retries the entire request. It never commits a prefix to save memory.

Group publication uses the existing original-compatible TRST encoding and barriers. A failed commit retains the accepted view; a retry first recovers any journal already published. Other mutations flush the group before proceeding. Legacy CSUN recovery is retained.

A new disk-full regression exposed a defect in the first prototype: it could acknowledge data that consumed the journal's remaining space. The corrected version proves sufficient space before acknowledgement. On roomy volumes it uses a conservative bound on how many originally free blocks staged bitmaps can consume. Near capacity it checks candidate journal pages against the real and final bitmaps, excluding staged targets. Failure discards the current request while keeping the earlier group flushable. The check uses existing scratch memory and performs no journal writes.

## Compiled benchmark

Five cold trials per cell write 1 MiB on the same emulated 68020 at 7,093,790 cycles/s, with 512-byte blocks, 32 filesystem buffers, 512 KiB chip RAM, 512 KiB slow RAM and 2 MiB fast RAM. The device adds no physical seek or flush delay. All **60 runs** passed independent image checks and exact-byte extraction. The strict baseline and buffered default use the same frozen benchmark driver, original handler, fixture and ROM. The final comparison rebuilds both CSUN variants from the integrated source.

Durable throughput includes open, all Writes, Close and Inhibit:

| Workload | Previous strict TRST | Buffered default | Improvement | Original SFS |
| --- | ---: | ---: | ---: | ---: |
| 8 KiB append | 185.17 KiB/s | 400.92 KiB/s | 116.5% | 518.65 KiB/s |
| 64 KiB append | 1,215.34 KiB/s | 2,050.96 KiB/s | 68.8% | 3,326.49 KiB/s |
| 8 KiB existing-file overwrite | 161.83 KiB/s | 250.29 KiB/s | 54.7% | 2,452.26 KiB/s |

Buffered-default Write latency and I/O per MiB:

| Workload | p50 / p95 | Journal writes | Total writes | Write requests | Flushes |
| --- | ---: | ---: | ---: | ---: | ---: |
| 8 KiB append | 18.82 / 21.35 ms | 1.0 KiB | 1,031.0 KiB | 142 | 8 |
| 64 KiB append | 20.89 / 67.30 ms | 1.0 KiB | 1,031.0 KiB | 30 | 8 |
| 8 KiB overwrite | 28.17 / 31.26 ms | 2.0 KiB | 1,036.5 KiB | 153 | 16 |

Strict TRST's corresponding journal writes are 64.5 / 8.5 / 64 KiB, and its flush counts are 516 / 68 / 512. Buffered TRST reads eight device sectors and no journal sectors in each workload. Original SFS uses four flushes and writes 1 / 1 / 0.5 KiB of journal data.

Maximum sampled buffered-TRST retention is **237.13 KiB**, versus 233.32 KiB for the previous strict TRST build and approximately 264.4–264.6 KiB for original SFS. This is about 10.3% below original SFS, but is a sampled working set rather than an allocation high-water mark. The complete [benchmark report](../artifacts/native-trst-group/default-benchmark/report.md) includes both rebuilt CSUN policies and all measurement boundaries. The summarizer accepts `--trst-policy buffered` so its durability description matches the qualified binary.

Original SFS groups metadata commits and overwrites existing file data in place. Buffered TRST also groups commits, but preserves old data through copy-on-write or staged redo. These results therefore do not establish equivalent durability or overwrite atomicity between the handlers. Throughput parity remains unmet, especially for overwrites.

The final overwrite profile attributes 4.15 million instruction cycles to extent lookup, 4.12 million to checksums and 2.09 million to copying, within a 29.02-million-cycle measured run. Small appends spend 2.01 million instruction cycles in checksums and 1.83 million in copying; large appends also expose free-run scanning. These are targets for further investigation, not predictions that removing a helper's work will save its entire measured cost.

## Rejected cache experiments

An eight-entry verification cache was fitted inside the existing arena budget. Raw stores, edits and arena resets revoked cached verification; collisions caused revalidation. A second version bypassed arena lookup on cache hits and retained verification when the writer had just sealed metadata. Both passed 293 strict host tests and 80 compiled buffer tests, including invalid-image rejection and self-store invalidation.

Full-handler profiles nevertheless showed regressions. With journal I/O unchanged, the first version slowed strict appends by **2.0–2.2%** and overwrites by **1.1%**. The second slowed appends by **2.7–3.2%**, while overwrite throughput changed by only **+0.1%**. Neither was promoted. Their frozen sources, binaries, profiles and test receipts remain available under `artifacts/native-trst-group/`.

This reinforces the need to measure the complete compiled path: avoiding checksum scans did not compensate for the added lookup, call and bookkeeping costs in short strict transactions. The earlier [compiler copy-folding experiment](native-trst-mount-cache.md#remaining-copy-bottleneck) also remains separate from the default.

## Qualification and reproducibility

The qualified buffered implementation passed:

- **300 host core tests**, with one optional external-image test skipped. Eight new group cases cover crash states with subsets of unflushed sectors, failed commits and retries, arena exhaustion, unpublished-data protection, and the disk-full regression.
- **17 compiled classic integration tests**, including two-Write group recovery, Close durability, timer commits, legacy CSUN recovery, whole large-Write recovery, low-memory retry, shared/concurrent handles and sequential-read compatibility.
- All five compiled relocation crash scenarios, with both CopperSFS and original SFS recovery.
- **60 benchmark trials** and three instruction profiles, with independent image and byte checks.

The compiled writer suites cover **1,529 sector/flush cut points**, **81 distinct durable images** and **162 recovery boots**, plus **6 interrupted-recovery cuts in 12 boots**. The two-Write group case itself covers 53 cut points and nine durable images in 18 recovery boots. Compiled cuts use the atomic-sector/reliable-flush model; the near-full-volume regression is host coverage.

The [integration evidence audit](../artifacts/native-trst-group/default-audit.json) passes. After changing the default selection, the rebuilt profiles passed:

| Profile | Compiled qualification |
| --- | --- |
| Buffered TRST default | 17 classic integration tests, plus the five relocation scenarios previously run against the identical HUNK |
| Strict TRST opt-in | 14 classic integration tests and five relocation scenarios |
| Strict CSUN | Whole bulk-Write crash recovery test |
| Buffered CSUN | Whole bulk-Write crash recovery test |
| SFS2 TRST | 11 tests, including original-handler compatibility, recovery and arena exhaustion |

The strict TRST crash receipts cover 1,486 writer cut points, 76 durable images and 152 recovery boots, plus six interrupted-recovery cuts in 12 boots. The SFS2 suite separately covers 63 sector/flush cut points in 30 cut/reboot groups. All selected compiled tests passed without skips. The final 60-trial benchmark uses the rebuilt default and CSUN binaries; its complete I/O and memory counters match the expected policies.

The audit checks source and HUNK identities, test outcomes, complete cut receipts, benchmark inputs and compiler compatibility reports. The normal default build and a fresh build from the current checkout are byte-for-byte identical to the qualified buffered HUNK. Three concurrent changes in CopperStart's Exec sources are recorded separately: that assembly contributes no reachable code to these standalone handlers. Filesystem sources match the frozen integration manifest. The earlier [pre-integration audit](../artifacts/native-trst-group/audit.json) retains the prototype and rejected-cache evidence. Physical-device qualification is not claimed.

The [317-file integration manifest](../artifacts/native-trst-group/source-default-v1.json) identifies `CopperStart/artifacts/native-trst-group/source-default-v1`. The normal checkout now builds buffered classic TRST with `build/Build-SfsHandler.ps1`; `-StrictWrites` selects strict TRST. `-BufferedTrst` explicitly selects the default and cannot be combined with legacy CSUN, strict-write or SFS2 options. Direct standalone MSBuild clients can set `CopperStartSfsTrstBuffered=false` for strict TRST. The earlier [316-file qualification manifest](../artifacts/native-trst-group/source-group-v5.json) records the source before default selection was changed.

The tested HUNK is **157,824 bytes**, SHA-256 `9777826B949D59E8D08681EC2A8F731B6A25DE358491C62DB8A1FFBF63D9B3F9`. Native compilation reports no managed allocations. No installation, package release, or physical-device power-loss qualification is included.

All five standalone handler builds succeed. The required full `CopperStart.slnx -c Release` build still fails on three unrelated `CopperOS.Shell.Dos` API errors: missing `LoadSegmentNative`, `UnloadSegmentNative` and `UnloadDosProcessSegment`. The [build log](../artifacts/native-trst-group/release-build.log) records those failures; the successful standalone and focused tests do not imply a successful full-solution build.
