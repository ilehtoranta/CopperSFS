# TRST Write-tail and checkpoint optimization

Historical qualification. The [follow-up](trst-tail-latency.md) reduces the
remaining overwrite tail and corrects the p95 regression reported below.

The compiled buffered handler reduces worst observed existing-file overwrite
Write latency from 31.274 to 29.062 ms (7.1%) and final checkpoint time from
32.677 to 29.827 ms (8.7%). It retains the 192 KiB staging cap, whole-Write
atomicity, CSUN recovery and the existing delayed-durability policy. Flush,
Close, Inhibit and timer commit remain durability points.

## Implementation

* The encoder uses the already computed payload length to emit a record directly
  into the current journal page when the complete payload fits. It copies
  literal spans in bulk. Records crossing a page retain the streaming path.
  Header, padding, journal publication and flush ordering are unchanged.
* The planner compares four unchanged old/final word pairs per loop iteration.
  It still reads each input word exactly once and carries the first mismatch
  into the next run. It adds no buffer or allocation.
* The native buffered adapter defers optional continuation-proof preparation to
  the next Write's checked fallback. Mandatory journal-space admission still
  precedes acknowledgement of the current complete Write. The previous accepted
  view survives a later proof failure. The original public WriteTrstGroup
  signature remains available and retains eager preparation by default.

This last change redistributes work: the second overwrite Write rises from
3.862 to 7.077 ms, and pooled overwrite p95 rises from 3.862 to 4.932 ms.
The selection favors the requested worst-Write bound and checkpoint time;
it is not an improvement to every latency percentile. A tested variant that
deferred only when the group was initially empty did not recover p95 and was
slightly slower, so it was not selected.

## Paired comparison

[Machine-readable measurements](benchmarks/trst-latency-20261027.json).

Five cold trials for each handler/workload, 45 checked runs. These measurements
compare the integrated baseline with the selected optimized binary in the same
batch. Throughput includes Open, Writes, Close and successful Inhibit. Max Write
and checkpoint are medians of the per-trial values; p95 pools all Write calls.

| Workload | Before KiB/s | After KiB/s | Max Write before/after ms | Checkpoint before/after ms |
| --- | ---: | ---: | ---: | ---: |
| Append, 8 KiB Writes | 698.99 | 703.03 | 31.484 / 31.492 | 30.793 / 26.596 |
| Append, 64 KiB Writes | 3616.39 | 3683.62 | 32.281 / 32.278 | 30.949 / 27.834 |
| Existing-file overwrite, 8 KiB Writes | 2470.14 | 2468.74 | 31.274 / 29.062 | 32.677 / 29.827 |

Append maximum Write latency is effectively unchanged. Append throughput rises
0.58% and 1.86%; overwrite throughput falls 0.056%. Every matched before/after
cell has identical sector, request, journal and flush counts.

A separate 60-run comparison includes both CSUN policies and original SFS:

| Handler | Overwrite KiB/s | Write p95 ms | Max Write ms | Checkpoint ms | Journal write/read KiB |
| --- | ---: | ---: | ---: | ---: | ---: |
| Buffered TRST | 2468.82 | 4.931 | 29.061 | 29.826 | 0.5 / 0 |
| Strict CSUN | 53.40 | 153.235 | 297.147 | 7.340 | 2176 / 1371 |
| Buffered CSUN | 53.26 | 153.138 | 300.919 | 10.093 | 2176 / 1371 |
| Original SFS 1.279 | 2452.18 | 3.480 | 5.569 | 24.975 | 0.5 / 0 |

Original SFS still has much lower worst overwrite latency and a shorter
overwrite checkpoint. Its payload overwrite is in place; these measurements
do not establish equivalent whole-Write atomicity. Strict and buffered policy
results must also be read with their different acknowledgement guarantees.

The optimized HUNK grows by 308 bytes to 167,756 bytes. Sampled guest RAM grows
by 336 bytes, from 253,488 to 253,824 bytes; these samples are not peak RAM.
There are no new explicit allocations or managed allocation sites. The staging
allocation still grows only within its existing 192 KiB cap.

## Qualification and reproducibility

Selected HUNK SHA-256:
`ED75F216F8B22074FFC67BCA1341206138EC80B257EF99ECB7A7916D22515AA5`.
Baseline HUNK SHA-256:
`F3BFF3A9DB64D86EBB7F05DA31C1BB0719D45F01AA36D2511D3BC7EB938E3B7E`.
Original SFS 1.279 SHA-256:
`C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.

The final core passes 780 host tests, with one optional external-image skip.
Four new cases cover deferred proof preparation, durable bitmap-read failure,
preservation of the prior accepted Write, retry, flush/remount and an untouched
neighbor, with both stable and scratch metadata views. Native qualification
passes 240 cases: 144 fresh source probes and 96 actual-HUNK instruction tests,
including twelve separately run bitmap cases found by the final inventory audit.

Fresh ROM qualification passes 27 default-handler cases, 16 strict TRST cases
and 11 SFS2 cases, including crash cuts, interrupted recovery and original-SFS
compatibility. Both CSUN binaries are byte-identical to the freshly qualified
integration builds, each of which passed its whole-write crash case. The final
integration build reproduces the selected HUNK exactly.

The unchanged pinned benchmark runner checks each output independently with
amidisk deep validation and exact-byte extraction. Each trial writes 1 MiB.
Overwrite replaces the first 1 MiB of a 3 MiB file, inside its first extent, and
checks the remaining 2 MiB. It does not measure random fragmented overwrites.
Timing is emulated 68020 PAL time at 7,093,790 cycles/s, with 512 KiB chip,
512 KiB slow and 2 MiB fast RAM, 512-byte blocks, 32 buffers and no added seek,
rotation or flush delay. Crash tests assume atomic sectors and reliable flushes.
Physical hardware and torn-sector behavior are outside this evidence.

The final full solution builds with zero errors (11 warnings) using an isolated output
directory and a temporary override of CopperOS's hardcoded reference to the
older shared checkout, as described in CopperStart's `Docs/NativeTrstMainline.md`.
Focused optimized builds pass. The full-ROM image was not rebuilt by this
qualification.

Local raw results, exact source manifests and build logs are retained under
`artifacts/publish-trst/`: `latency-v1-*`, `strict-*-latency-v1`,
`sfs2-compatibility-latency-v1`, and `profiles-latency-v1`. The narrower rejected
policy is recorded under `latency-v2-*`. ROMs, reference binaries, images and
generated executable artifacts are excluded from Git. This source update does
not publish a new package release.
