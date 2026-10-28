# Native inline TRST publication

The native handler now uses the original SFS single-sector journal shortcut. A complete journal that fits in one 512-byte sector is published as TRST directly at root container + 2. This removes the separate external journal page and TRFA publication write. Journals that overflow still use external pages and TRFA. Whole-write atomicity, the 192 KiB staging limit, buffered delayed durability and legacy CSUN recovery are preserved.

The change is integrated into the native development default. The live build exactly reproduces the qualified HUNK: **155,668 bytes**, SHA-256 `DC07FAE74CFE4B29296B58354600B2CDB2B121F29161614A7DA1130F00CE7409`. The [integrated audit](../artifacts/native-trst-inline/integrated-audit.json) verifies the frozen sources, compiler inputs, binaries and qualification results. This is an I/O improvement; it does not close the overwrite performance gap.

## What came from studying original SFS

The [original implementation study](native-trst-original-study.md) traced the exact SFS 1.279 benchmark binary alongside an older published SFS-derived source tree. Its binary and sector trace establish direct TRST publication in the marker sector when the complete journal fits. The matching 1.279 C source was not found; the older C source alone does not establish this shortcut.

That investigation also identified the larger difference: original SFS overwrites file data in place, avoiding allocation and extent edits for unchanged-size writes. It still looks up the extent on each Write. Our whole-write atomicity requires replacement data and mapping changes, and our continuation path repeats substantial validation and metadata bookkeeping. The native journal planner accounts for only about 0.8% of the measured overwrite CPU time. Adopting its journal shortcut is useful, but journal encoding is not the main remaining bottleneck.

The separate cache-line presence experiment remains [parked](../artifacts/native-trst-cache-lines/parked.json). Its 1.66% overwrite gain came with a 0.78% 64 KiB append regression. It is not part of this build.

## Publication and overflow

The [writer](../../CopperStart/external/CopperSFS/src/CopperSFS.Core/AmigaSmartFileSystemTrst.cs) starts a tentative marker-addressed page in the existing journal scratch buffer. It encodes each record once. On the first overflow, it allocates an external first page, changes the still-unwritten page's own-block address, and links the next page before emitting anything. Subsequent encoding follows the existing external-page path. The staging arenas and their format are unchanged.

For an inline journal, the order is:

1. Flush unpublished replacement data.
2. Write the complete TRST sector to the marker; flush.
3. Write the final home images; flush.
4. Write TROK; flush.

The first flush is essential: writing inline TRST itself publishes the transaction. For an external journal, data and journal pages are flushed before TRFA publication, as before. The shortcut requires both a 512-byte filesystem block and a 512-byte device sector. Larger blocks and multi-sector blocks retain the external protocol. The existing classic recovery reader already supports both marker forms; SFS2 publication is unchanged.

No new runtime allocation is introduced. The code hunk grows by 168 bytes, which matches the increase in sampled RAM; the HUNK file grows by 100 bytes because its relocation data also changes. The native state layout and shared 192 KiB staging cap remain unchanged.

## Benchmark results

The [full benchmark](../artifacts/native-trst-inline/benchmark/report.md) contains all four policies, latency, I/O and memory measurements. There are five cold trials per handler/workload, 60 checked runs in total. Each writes 1 MiB on the same emulated 68020 and fixture. Rates use guest cycles; the device adds no seek, rotation or flush delay. Every output passed independent deep filesystem checking and exact-byte extraction.

| Workload | Previous TRST KiB/s | Inline TRST KiB/s | Change | Original SFS KiB/s |
| --- | ---: | ---: | ---: | ---: |
| 8 KiB append | 672.05 | 673.17 | +0.17% | 518.67 |
| 64 KiB append | 3,469.85 | 3,513.61 | +1.26% | 3,326.51 |
| 8 KiB existing-file overwrite | 1,214.75 | 1,214.92 | +0.014% | 2,452.38 |

Overwrite throughput is effectively unchanged and remains **49.54% of original SFS**. Native TRST is 29.8% faster for the tested 8 KiB appends and 5.6% faster for 64 KiB appends. Those comparisons do not make original SFS's in-place data writes whole-write atomic.

Latency is mixed, so this must not be described as a general latency improvement:

| Workload | Write p50 ms, before → after | Write p95 ms, before → after | Worst Write ms, before → after | Final checkpoint ms, before → after |
| --- | ---: | ---: | ---: | ---: |
| 8 KiB append | 10.717 → 10.922 | 13.448 → 13.451 | 25.947 → 32.692 | 33.485 → 31.046 |
| 64 KiB append | 11.169 → 11.541 | 26.515 → 33.226 | 26.515 → 33.226 | 33.474 → 31.049 |
| 8 KiB overwrite | 4.665 → 4.745 | 7.509 → 7.592 | 68.084 → 67.354 | 40.720 → 36.725 |

The roughly 6.7 ms increase in worst append Write latency remains a follow-up concern. The total-cycle figures include Open, every Write, Close and Inhibit; they do not imply that individual Writes became faster. The 45 original/CSUN control trials retain identical I/O and memory observations, with at most 0.0045% total-cycle variation from the previous run. The [comparison receipt](../artifacts/native-trst-inline/metrics.json) retains these controls.

| Workload | Sector writes, before → after | Device write requests, before → after | Flush commands, before → after |
| --- | ---: | ---: | ---: |
| 8 KiB append | 2,062 → 2,060 | 142 → 140 | 8 → 7 |
| 64 KiB append | 2,062 → 2,060 | 30 → 28 | 8 → 7 |
| 8 KiB overwrite | 2,057 → 2,056 | 137 → 136 | 4 → 4 |

The no-data commit in the append interval no longer needs an actual device flush before publishing its inline journal when the device is already clean. Every commit still requires data durability before publication. Overwrite keeps four device flushes. Journal payload writes remain 1 KiB for the append intervals and 512 bytes for overwrite; removing TRFA reduces marker writes, which the benchmark reports separately. Peak sampled native RAM is **235.375 KiB**, up 168 bytes. This is total sampled handler memory, distinct from the staging cap.

## Qualification

The same production change has been used throughout qualification. Later frozen versions repair or extend test harnesses only. The final 337-file source manifest has SHA-256 `B7E9015E394DB5DD719854632562C264BB854E2DCD7C3A449BBC83F97207B663`.

- **Host semantics:** 492 passed; one optional external-image test skipped. Seven new cases cover an exactly full inline sector, overflow at record boundaries, split headers, split literals, a multi-sector geometry fallback, arbitrary persistence subsets, write/flush failures, and data-before-marker ordering.
- **Compiled MC68000 instruction tests:** 115 passed against the candidate HUNK.
- **Native ROM/media tests:** 18 buffered and 15 strict tests passed. Both policies passed all five relocation scenarios; four continuation/allocation tests passed on each of the standard and bulk fixtures. Legacy CSUN recovery remains covered. The two CSUN profile builds are byte-for-byte identical to the qualified baseline.
- **Original-handler compatibility:** every grouped crash image was recovered and byte-checked with both CopperSFS and original SFS 1.279. A new unaligned 1,537-byte Write forces a four-page external journal and exercises the promotion path through actual compiled code. Both handlers also resume interrupted replay.
- **SFS2 regression:** all 11 tests passed, covering 63 sector/flush cut points and 30 representative cut/reboot groups.
- **Performance:** 60 acceptance trials and all three diagnostic profiles passed their image/byte checks.

| Policy | Sector/flush cut points covered | Distinct durable images | Recovery boots | Interrupted inline replay | Interrupted external replay |
| --- | ---: | ---: | ---: | ---: | ---: |
| Buffered TRST | 7,439 | 119 | 238 | 6 cuts / 12 boots | 7 cuts / 14 boots |
| Strict TRST | 1,479 | 75 | 150 | 6 cuts / 12 boots | 7 cuts / 14 boots |

Native crash coverage groups cuts with identical durable state under a device model that loses all unflushed sectors. Host tests additionally enumerate arbitrary persistence subsets. These results assume atomic 512-byte sectors and reliable flushes; they are not physical-drive power-loss measurements.

Test-harness repairs are retained in the evidence: the initial new host checksum assertion incorrectly expected zero instead of the SFS sum of `0xFFFFFFFF`; native hardfile and floppy capture needed to recognize TRST as well as TRFA publication; and each new replay export needed a unique filename. The corrected suites pass without changing the production candidate.

The live default handler build passes and exactly matches the qualified binary. `dotnet build CopperStart.slnx -c Release` still fails on the same three pre-existing CopperOS shell API mismatches: `LoadSegmentNative`, `UnloadSegmentNative` and `UnloadDosProcessSegment`. See the [full solution log](../artifacts/native-trst-inline/release-build.log). Nine concurrent Exec changes were preserved and are unreachable from the standalone handler; no compiler or emulator change is included.

The remaining performance work is in accepted-write continuation and its generic transitions. Any reduction in repeated bookkeeping must retain private-record lifetime checks, pending-read visibility, and the rule that a later failed Write cannot alter an earlier accepted Write. The overall performance goal remains unmet.
