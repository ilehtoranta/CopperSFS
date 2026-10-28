# Native TRST handler integration

The latest [buffered TRST integration](native-trst-group.md) selects grouped Writes for classic standalone builds after explicit user approval and compiled qualification. Use `-StrictWrites` for the per-Write durability policy described by this earlier checkpoint.

Follow-up: [qualified TRST and bulk-allocation fixes](native-write-fixes.md)
records the optimized compiled binaries, shared-source coordination, crash
recovery, original SFS compatibility and final performance measurements.
The subsequent [staging-index optimization](native-trst-index.md) records the
current arena layout. The [encoding and block-copy optimization](native-trst-codec.md)
records the latest compiled qualification and measurements.

The native integration lives in the sibling CopperStart checkout, including
its newer embedded `external/CopperSFS` core. The portable core's TRST encoder
and commit protocol are adapted to the native platform's metadata views,
allocation hints, recycled-file accounting and physical write queue. The new
platform overlay covers scalar and range I/O and verified metadata reads.

## Memory and atomicity policy

Each mounted volume owns at most one **192 KiB** redo staging arena, allocated
on its first mutation and freed on unmount. The indexed arena has a 24-byte
header, a bounded hash table, and `4 + BlockBytes` bytes per distinct changed
block. At 512-byte blocks the 2 KiB table leaves room for 377 blocks, including
data and metadata (the previous linear arena held 380). Metadata growth reduces
the maximum successful data packet size below 188.5 KiB; callers must handle
`ERROR_NO_FREE_STORE` for a request exceeding the arena or an allocation
failure. The cap does not limit file size: successive successful DOS Write
packets can grow arbitrarily large files within classic SFS size limits.

All data and metadata for one DOS packet are staged before journal publication.
The arena is never drained into smaller transactions. Exhaustion discards the
unpublished operation, reports zero bytes written and leaves the file position
and content unchanged. A later smaller request can reuse the same arena. The
TRST policy never silently falls back to writing a private CSUN journal.

There is no mount write-back accumulation in this policy: each successful
mutation is durable. The existing physical write combiner remains bounded
and drains at TRST's barriers. Old CSUN and original SFS TRST recovery remain
available on mount. Once a new TRST journal is published, recovery completes
the whole operation forward. Create followed by Write remains two DOS packets,
so a crash between them can leave an empty file.

The native adapter's private state grows to 2,052 bytes, and the extension ABI
is **CSE7, version 8**, including the concurrent wide-position bridge changes.
Providers and extension ROM must be rebuilt together;
the signature check rejects older extension layouts. This does not change
the SFS disk format.

This qualification targets classic `SFS\0` only. The concurrent native SFS2
bridge is preserved behind `Build-SfsHandler.ps1 -Sfs2` /
`CopperStartNativeSfs2=true`. Its TRST policy has a separate
`CopperStartSfs2TrstWrites` opt-in; enabling classic TRST does not enable it.

## Qualification workflow

Build a candidate into a separate artifact directory:

```powershell
# In CopperStart:
./build/Build-SfsHandler.ps1 -ArtifactRoot <candidate-directory>
dotnet build tests/CopperStart.Sfs.Native.Tests/CopperStart.Sfs.Native.Tests.csproj `
  -c Release --output <native-test-directory>
```

In CopperSFS, run `scripts/Test-NativeTrst.ps1` with the candidate HUNK, native
test assembly, pinned A/B fixture manifest, Python executable and previously
qualified CSUN handler. `COPPERSHARP_KICKSTART31_ROM` supplies the licensed ROM.
The script retains TRX results and native cut receipts under its output folder.

The compiled tests cover arena exhaustion/reuse, legacy CSUN mount recovery,
and every write/flush cut during create/write, overwrite, truncate, rename and
delete. Each interrupted image is rebooted with both CopperSFS and original
SmartFilesystem 1.279, and independently checked/extracted with pinned amidisk.
Published dirty markers must point to TRST, successful traces must contain
TRST and no CSUN descriptors, and recovered markers must be durably clean.
Shared-handle, namespace/metadata, concurrent-process and A500 startup checks
exercise the packet-handler integration separately.

The native crash model uses atomic 512-byte sectors, reliable device flushes,
and loss of pending writes on interruption. It does not qualify torn sectors,
lying flushes, controller reordering or physical Amiga hardware. Native test
fixtures, licensed media, compiler inputs and private bridge source remain
local artifacts. No release or downloadable handler is published by this work.

## Results and default

The classic profile passed **11 compiled/native tests with no skips** under
Kickstart 3.1. The crash receipts cover:

| Matrix | Writer/recovery cut points | Durable-state classes | Recovery boots (CopperSFS + original) |
| --- | ---: | ---: | ---: |
| Create/write, overwrite, truncate, rename, delete | 71 | 29 | 58 |
| One 128 KiB DOS Write | 548 | 9 | 18 |
| Interruption during recovery | 6 | 6 | 12 |

All 88 matrix recovery boots passed. Equal durable states between completed
flushes are grouped under the documented loss-of-pending-writes model; the
receipts retain every covered sector/flush point. Separate tests passed legacy
CSUN migration, arena exhaustion/reuse, shared handles, namespace/metadata
changes, concurrent tasks, and A500/68000 startup, remount and recovery.
The embedded core suite passed 258 tests (one optional external-image test
skipped), and the CopperStart solution build passed.

The qualified default ABI-v8 handler is 126,660 bytes, SHA-256
`6F9331C6BBA0D479A06282FED2AD3994B432FB57273D0077BA39F8C303204820`.
Its exact compiled binary passed the complete gate in 5 minutes 36 seconds.
Local evidence is retained under `artifacts/native-trst/default-qualified`.
The default extension ROM also compiled to 262,144 bytes with a valid CSE7
header and checksum; that is a build check, not runtime ROM qualification.

**Classic native development builds now default to TRST**, through
`CopperStartSfsTrstWrites=true` and the default `Build-SfsHandler.ps1` path.
`-LegacyCsUn` explicitly builds the previous CSUN writer for diagnostics;
CSUN recovery is always retained. Ordinary portable-core APIs keep their
existing opt-in TRST contract. SFS2 remains a separate opt-in profile, and no
existing downloadable handler has been replaced.

The subsequent [compiled-handler benchmarks](native-write-benchmark.md) compare
this exact TRST binary with strict CSUN, buffered CSUN and original SFS 1.279.
They show substantial throughput and memory costs in the current native TRST
implementation, despite lower journal-write volume for overwrites.
