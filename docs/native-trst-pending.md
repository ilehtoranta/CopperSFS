# Native TRST pending-write entry

This checkpoint is superseded by the [deferred metadata run optimization](native-trst-deferred.md). Measurements and qualification below describe the pending-entry build identified here.

The native buffered handler now enters a pending overwrite directly through its existing continuation proof. It avoids rebuilding the full general-purpose write workspace for every accepted packet. The matched benchmark improves overwrite throughput by 5.95%, with unchanged device I/O. This is progress toward parity, but overwrite throughput remains 52.49% of original SFS 1.279.

The change follows the [original-handler investigation](native-trst-original-study.md): original SFS retains file and extent state between writes. Our journal encoder is less than 1% of measured overwrite cycles. The original also writes existing data in place; that behavior cannot replace our atomic replacement-data protocol.

## Implementation and invariants

[TryContinueTrstGroup](../../CopperStart/external/CopperSFS/src/CopperSFS.Core/AmigaSmartFileSystemTrstPending.cs) needs only metadata scratch, reservations and a 28-byte local continuation context. It does not initialize the 152-byte Work record containing allocation and journal state. The caller may still reserve stack-frame space for its fallback; this is an initialization and dispatch optimization, not a claimed 124-byte stack-allocation saving.

The native packet path checks eligibility and invokes that entry before constructing Work. In the overwrite profile, full WriteTrstGroup calls fall from 128 to eight, and InitializeSmartWriteWork calls from 133 to 13. The other 120 Write packets use the pending entry. First writes, missing proofs, extent boundaries, allocation pressure and recovery after failed publication retain the full path. Strict TRST, CSUN, SFS2 and non-standalone compilation preserve the original dispatch order.

Both entries share the continuation validation and update code. Group identity and mapping, the used scratch buffer, request overlap/mapping, volume and object protection, object size/type, proof checksum, slot identity, extent links, allocation bits and reservation checks remain. The smaller API does not accept the unused data, bitmap or journal scratch buffers. The original WriteTrstGroup API retains their validation.

Every fallible check and payload transfer precedes the accepted-image edits. A failed payload write invalidates the continuation hints while preserving the accepted arena byte-for-byte. Successful payload I/O is followed only by the existing bounded edits to owned images. Pending reads therefore see the accepted data immediately. Buffered durability, whole-write atomicity, CSUN recovery and the shared 192 KiB staging cap are preserved. No group, proof, arena-slot or native-state layout changes, and no new persistent allocation, are introduced.

## Measurements

Five cold trials per handler and workload, 60 runs total, compare buffered TRST with strict CSUN, buffered CSUN and original SFS on the same emulated 68020 and disk fixture. Each writes 1 MiB. Rates use guest cycles; the device adds no seek, rotation or flush delay. Every final image passes independent deep checking and exact-byte extraction. See the [full benchmark](../artifacts/native-trst-pending/benchmark/report.md) and [comparison receipt](../artifacts/native-trst-pending/metrics.json).

| Workload | Previous TRST KiB/s | New TRST KiB/s | Change | Original throughput reached |
| --- | ---: | ---: | ---: | ---: |
| append-8k | 673.17 | 673.46 | +0.04% | 129.84% |
| append-64k | 3,513.61 | 3,520.50 | +0.20% | 105.83% |
| existing-file-overwrite-8k | 1,214.92 | 1,287.21 | +5.95% | 52.49% |

Latency is mixed. The typical overwrite improves, while rare and boundary writes remain expensive. The full path can repeat validation when constructing a missing proof; these measurements do not attribute every tail-latency change to that code alone.

| Workload | Write p50 ms, before → after | Write p95 ms, before → after | Worst Write ms, before → after | Checkpoint ms, before → after |
| --- | ---: | ---: | ---: | ---: |
| append-8k | 10.922 → 10.905 | 13.451 → 13.451 | 32.692 → 32.768 | 31.046 → 30.759 |
| append-64k | 11.541 → 11.504 | 33.226 → 33.326 | 33.226 → 33.326 | 31.049 → 30.757 |
| existing-file-overwrite-8k | 4.745 → 4.340 | 7.592 → 8.147 | 67.354 → 68.424 | 36.725 → 34.091 |

All measured I/O counts are identical to the previous inline-TRST build. Overwrite still writes 2,056 sectors in 136 requests, with one TRST page and four device flushes. Peak sampled handler RAM rises from 235.375 to 236.234375 KiB, matching the 880-byte code-hunk increase. The HUNK file grows by 368 bytes because relocation data also changes. The 45 original/CSUN controls retain identical I/O and sampled RAM, with at most 0.0025% variation in total cycles.

## Qualification and integration

- Host semantics: 528 passed, one optional external-image test skipped. The 36 new cases cover proof corruption, decline/error behavior, pending reads, every sector failure in an 8 KiB continuation, full retry, crash cuts and arbitrary persistence subsets.
- Compiled MC68000 instruction suite: all 115 passed. The three native diagnostic profiles also pass image and byte checks and demonstrate the new entry being exercised.
- Native ROM/media suite: 18 main tests passed, plus four continuation/allocation tests on each of the standard and bulk fixtures and all five relocation scenarios in one test.
- Buffered crash coverage: 7,439 sector/flush cut points grouped into 119 durable images, recovered in 238 boots using CopperSFS and original SFS. Interrupted inline replay adds 6 cuts/12 boots; external-journal replay adds 7 cuts/14 boots.
- Strict TRST, both CSUN profiles and SFS2 rebuild byte-for-byte identically to their previously qualified binaries. Their crash qualification is inherited by binary identity; it is not reported as newly rerun. The new default separately passes legacy CSUN recovery.

Native cuts use atomic 512-byte sectors and reliable flushes, grouping cuts that leave identical durable state when all pending sectors are lost. Host tests additionally exercise arbitrary persistence subsets. These are emulator/device-model tests, not physical power-loss measurements.

The live default reproduces the qualified 156,036-byte HUNK, SHA-256 `B6184E8D7A4CC56AC512BF0652803F937AA8FB682FAF49331C8D2BBE8FD5ACB4`. The [integrated audit](../artifacts/native-trst-pending/integrated-audit.json) verifies 339 frozen source files, compiler inputs, binary identities and test receipts. Snapshot v2 only narrows the dispatch change to buffered standalone builds; its default HUNK is identical to v1, and every core/test source used by host qualification is unchanged.

The full solution build still fails on the three pre-existing CopperOS shell API mismatches: LoadSegmentNative, UnloadSegmentNative and UnloadDosProcessSegment. The standalone handler build passes. Concurrent Exec changes were preserved and remain unreachable in the standalone output. This changes the native development default; it does not release a package or sync the public CopperSFS core.

The remaining gap is still in continuation validation/metadata bookkeeping and expensive generic transitions. The next substantial reduction needs stronger reuse of owned file/run state with explicit invalidation, rather than more journal encoding work. Median latency improved here; p95 and maximum latency still need attention. The overall performance goal is not achieved.
