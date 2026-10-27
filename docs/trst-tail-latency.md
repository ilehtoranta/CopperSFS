# TRST overwrite tail and p95 follow-up

The compiled buffered handler reduces measured maximum overwrite Write latency
from 29.062 to 24.760 ms (14.8%) and pooled p95 from 4.931 to 3.053 ms (38.1%).
It retains whole-Write atomicity, the 192 KiB staging cap, legacy CSUN recovery,
and delayed durability at Flush, Close, Inhibit or timer commit.

The tradeoff is explicit: the second overwrite Write rises from 7.077 to
9.935 ms and the final checkpoint from 29.827 to 31.882 ms (6.9%). Durable
throughput rises 0.63%. Original SFS still has a substantially shorter maximum
Write and checkpoint; this change does not establish latency parity.

## Changes and invariants

* After constructing a continuation proof, start the sealed deferred run in
  that Write. Previously the second Write applied metadata edits immediately,
  and the third repeated setup to begin deferring. Bitmap-boundary admission
  now also starts its run immediately. After accepting the spare arena, the
  private run seal is recomputed for the swapped arena identities, with no
  intervening callback or block access. All fallible work still precedes the
  complete unpublished payload transfer and bounded publication.
* Keep the already allocated eight-sector physical read window valid across
  unrelated direct writes. Copy only requested images into the existing LRU.
  This removes eager neighbor copies from the first Write without adding a
  buffer, allocation, or I/O request. Overlapping physical writes, reads that
  reuse the buffer, bounce transfers and TRIM revoke the window. Queued writes
  remain authoritative, including if the physical window was refilled before
  their drain. Partial reads never certify an unread suffix; every requested
  metadata image still passes its identity and checksum checks.
* TRIM revokes the physical window before issuing UNMAP. The existing LRU
  invalidation still runs for both success and partial/error completion.

The read-window change is limited to classic standalone TRST. CSUN and SFS2
keep their previous cache behavior. No on-disk structure, journal encoding,
publication barrier, data-placement policy or acknowledgement policy changes.
The existing public WriteTrstGroup overload continues to request eager proof
preparation by default; the native buffered adapter remains lazy.

## Paired compiled benchmark

[Machine-readable evidence](benchmarks/trst-tail-20261027.json).
Five cold trials per handler/workload, 45 independently checked runs. Each run
writes 1 MiB; throughput includes Open, Writes, Close and successful Inhibit.
Max Write and checkpoint are medians of per-trial values; p95 is nearest-rank
across all Write calls. Values include guest OS scheduling and timer activity.

| Workload | Before / after KiB/s | p95 before / after ms | Max Write before / after ms | Checkpoint before / after ms |
| --- | ---: | ---: | ---: | ---: |
| Append, 8 KiB Writes | 703.02 / 702.21 | 13.153 / 13.090 | 31.492 / 30.690 | 26.593 / 27.845 |
| Append, 64 KiB Writes | 3683.59 / 3677.89 | 32.280 / 31.445 | 32.280 / 31.445 | 27.834 / 29.785 |
| Existing-file overwrite, 8 KiB Writes | 2468.79 / 2484.37 | 4.931 / 3.053 | 29.062 / 24.760 | 29.827 / 31.882 |

Append throughput changes by -0.12% and -0.15%; append checkpoint latency
increases by 4.7% and 7.0%. This is a Write-tail/p95 optimization, not a claim
that every latency measure improves. The original SFS overwrite result in this
paired batch is 2452.35 KiB/s, 3.474 ms p95, 5.573 ms maximum Write and 24.979 ms
checkpoint. Its in-place payload updates have different atomicity guarantees.

All before/after sector, request, journal and flush counts are identical:

| Workload | Read sectors / requests | Write sectors / requests | Flushes | TRST pages / markers | Journal reads |
| --- | ---: | ---: | ---: | ---: | ---: |
| Append, 8 KiB | 8 / 1 | 2060 / 140 | 7 | 2 / 2 | 0 |
| Append, 64 KiB | 8 / 1 | 2060 / 28 | 7 | 2 / 2 | 0 |
| Overwrite, 8 KiB | 8 / 1 | 2055 / 135 | 4 | 1 / 1 | 0 |

Sampled guest RAM rises by 272 bytes, from 253824 to 254096 bytes. These are
samples, not a peak-memory bound. The HUNK grows by 612 bytes to 168368 bytes.
There are no new explicit buffer allocations or managed allocation sites;
staging still grows only within its existing 192 KiB cap.

A separate diagnostic run attributes 179580 versus 160561 cycles to handler
work in the first Write (10.6% less), including 31111 versus 19739 cycles in
CopyBlock (36.6% less). Interrupt/timer phase also changes. Those instrumented
figures explain removed work; they are not substituted for benchmark latency.

## Policy comparison

A separate five-trial comparison has 60 checked runs across three workloads.
The overwrite results are:

| Handler | KiB/s | p95 ms | Max Write ms | Checkpoint ms | Journal write/read KiB | Sampled RAM KiB |
| --- | ---: | ---: | ---: | ---: | ---: | ---: |
| TRST | 2484.30 | 3.060 | 24.761 | 31.884 | 0.5 / 0.0 | 248.14 |
| CSUN-strict | 53.40 | 153.233 | 297.186 | 7.339 | 2176.0 / 1371.0 | 374.92 |
| CSUN-buffered | 53.26 | 153.126 | 300.908 | 10.093 | 2176.0 / 1371.0 | 424.27 |
| Original | 2452.38 | 3.477 | 5.573 | 24.978 | 0.5 / 0.0 | 264.41 |


## Qualification and reproducibility

Final HUNK SHA-256:
`7E610C0ED5796F7E1E2C34C2C89AE38FF6A3CDC892CB9B5D9326634FFC082F41`.
Baseline: `ED75F216F8B22074FFC67BCA1341206138EC80B257EF99ECB7A7916D22515AA5`.
Baseline core/native commits: `b3b3c71` / `299d52a`.
The final integration build reproduces the selected HUNK byte for byte.

All required checks pass on the final identities:

| Coverage | Result |
| --- | --- |
| Core host tests | 784 passed; one optional external-image case skipped |
| Native source probes / actual-HUNK instruction cases | 257 passed; no skips |
| Default TRST crash/recovery/original-SFS compatibility | 27 passed; no skips |
| Strict TRST crash/recovery/original-SFS compatibility | 16 passed; no skips |
| Strict / buffered CSUN whole-write crash cases | 2 passed; no skips |
| SFS2 compiled writer / original-handler compatibility | 11 passed; no skips |
| Paired / policy benchmarks | 45 / 60 checked runs |

Native coverage comprises 144 fresh source probes and 113 actual-HUNK cases.
SFS2 covers 63 sector/flush cuts in 30 actual cut/reboot groups. Both CSUN
profiles and SFS2 rebuild byte for byte to their previous qualified identities;
the fresh checks above also pass on those hashes. An initial legacy-CSUN run
was skipped because its explicit opt-in was missing; the corrected fresh runs
pass without skips and supply the qualification receipts.


Four new host cases cover stable and scratch metadata views, initial lazy proof
and bitmap-boundary run starts, every sector failure of the following Write,
unchanged accepted descriptors/images, retry, bounded memory, read visibility,
flush/recovery and an untouched neighboring file. Seventeen new actual-HUNK
cases run on both MC68000 and MC68020 and cover window reuse, queue precedence,
DMA bounce paths, partial reads, identity/checksum rejection, failed writes,
and successful/failed TRIM. The two TRIM tests fail on the earlier candidate
and pass on the final binary, confirming that they detect the missing expiry.

The full managed solution builds with zero errors: 13 warnings in the isolated
clean build and 7 in the final incremental build. The temporary CopperOS project
reference override described in NativeTrstMainline.md avoids changing its older
shared checkout. This is not a full-ROM image rebuild.

The unchanged pinned runner checks exact output bytes and amidisk deep image
validation. The historical raw tag `fragmented-overwrite-8k` replaces the first
1 MiB of a 3 MiB file within its first extent and checks the untouched 2 MiB;
it does not represent random fragmented overwrite. Timing is emulated 68020
PAL time at 7093790 cycles/s, with 512 KiB chip, 512 KiB slow and 2 MiB fast RAM,
512-byte blocks, 32 buffers and no added seek, rotation or flush delay. The
handler is compiled for MC68000. Crash evidence assumes atomic sectors and
reliable flushes; physical hardware and torn sectors are outside these tests.

A wider copy loop was rejected after worse throughput/checkpoint results. A
same-type-only prefetch filter did not materially reduce the maximum Write.
The selected read-window approach has the checkpoint tradeoff reported above.
Raw measurements, source manifests, logs and negative-control results are in
`artifacts/trst-tail/`; final default ROM receipts are in
`artifacts/publish-trst/tail-v8-classic` and `tail-v8-relocation`, with the
parallel continuation receipts under `artifacts/trst-tail/v8-continuations-*`.
ROMs, reference handlers, disk images and
compiled artifacts are excluded from Git. No package release is made here.
