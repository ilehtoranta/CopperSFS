# Validation boundary

## Preview.3 package identity

The `v0.1.0-preview.3` packages use the exact handler bytes qualified below,
without rebuilding or modifying them for release. The 68020 ZIP contains the
buffered `CopperSFS` and optional `CopperSFS-strict`; the separate 68000 ZIP
contains the buffered compatibility build. Its coverage remains the limited
68000-HUNK-on-68020 checks stated below, not the 68020 recovery/performance
qualification. The [release manifest](../packaging/v0.1.0-preview.3.json)
records sizes and SHA-256 hashes. Each ZIP includes the same manifest as
`RELEASE.json`, installation instructions, a mount example and file checksums.
See [installation and upgrade](handler-install.md) before selecting a variant.

## Compact queue and write CPU qualification, 2026-10-22

The buffered handler now uses fewer guest CPU cycles than original SFS in all
six measured workloads. The three write workloads also retain exactly the
previous build's sector, device-request and flush counts. The physical queue
has the same 128-block capacity while using 69,408 bytes instead of 133,920:
64,512 bytes (48.2%) less per mounted volume at 512-byte blocks. The logical
write buffer and other cache allocations are unchanged.

Sorting now permutes the owned block images in place using one scratch block
and an inverse slot map. Adjacent runs transfer directly from those images.
The queue retains every image after partial transfers and supports replacement,
further insertion and another sort before retry. Commit phases, four durability
barriers, the undo format and the buffered/strict durability policies are
unchanged. See [transaction batching](transaction-batching.md).

CPU work was reduced by reusing an extent record location within the operation
that validated it, publishing edited buffered images directly, using scalar
geometry access, shifting B-tree records with aligned backward copies, and
limiting small cache invalidations to their hash buckets.
The native adapter reads buffered metadata directly while preserving hidden
recovery views, checksum verification and tag invalidation. Borrowed bitmap
views remain read-only until their checkpoint image has been captured.

The qualified handlers are:

| Variant | Bytes | SHA-256 |
| --- | ---: | --- |
| Buffered 68020 | 104,568 | `24E56660A6251D4EB267983CF547351FA37376FBD6178534FAA563AE799593CE` |
| Strict 68020 | 104,360 | `B3AA61500E078A3B9BBB66860F7243E982C6913B499ADE6E7819076256751CA8` |
| Buffered 68000 | 106,304 | `9EC8FD11FB01153853A8D857873439A367D4D7205463E6EB17BCD34ED9B91563` |

The buffered 68020 handler is 384 bytes smaller than the preceding build and
6,584 bytes larger than original SmartFilesystem 1.279 (97,984 bytes).
Original handler SHA-256 is
`C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.
Every independent run began with a fresh byte-for-byte clone of the same
16 MiB, 512-byte-block image, SHA-256
`36119C09B6480AD11A748EA011774F2A4F30E9659C0A9FC03E13FA4747983ED1`.
Both handlers used Kickstart 3.1, a 68020, 512 KiB chip RAM, 2 MiB fast RAM,
32 mount buffers, Mask `0x7FFFFFFF` and MaxTransfer `0x1FE00`. Geometry was
two surfaces, 32 blocks per track, cylinders 0–511, two reserved blocks and
start offset zero. The compiler and emulator were pinned. Independent checks
used amidisk revision `a4faa7b36bd7c005371d9bb509a936a13d58b43f`.

All 60 unprofiled trials passed, with five fresh-image trials per handler and
workload. The shared correctness/remount gate passed before benchmarking.
The following are medians; the previous CopperSFS column is the preceding
adjacent-write qualification on this same fixture and profile.

| Workload | Original cycles | Previous CopperSFS cycles | Current CopperSFS cycles | Fewer cycles than original |
| --- | ---: | ---: | ---: | ---: |
| Sequential 1 MiB read | 2,539,469 | 2,185,050 | 2,129,610 | 16.1% |
| Sequential 1 MiB write | 14,420,000 | 15,058,870 | 13,563,520 | 5.9% |
| Small-file creation | 10,346,320 | 10,857,750 | 10,083,470 | 2.5% |
| Directory enumeration | 7,825,501 | 6,468,870 | 6,236,461 | 20.3% |
| Mixed access | 9,331,960 | 9,861,690 | 9,190,281 | 1.5% |
| Repeated 1 MiB read | 2,554,810 | 2,130,271 | 2,146,900 | 16.0% |

Read/write/flush counts below were identical in all five trials of each
handler/workload pair. Every individual CopperSFS read-request, write-request
and flush count matches the preceding build. Corresponding total requests
match or beat original SFS. Other device control commands are excluded.

| Workload | Original requests R/W/F | CopperSFS requests R/W/F | Original sectors R/W | CopperSFS sectors R/W |
| --- | ---: | ---: | ---: | ---: |
| Sequential 1 MiB read | 128 / 0 / 0 | 128 / 0 / 0 | 2,048 / 0 | 2,048 / 0 |
| Sequential 1 MiB write | 0 / 132 / 4 | 1 / 131 / 4 | 0 / 2,058 | 8 / 2,071 |
| Small-file creation | 4 / 9 / 4 | 2 / 9 / 4 | 64 / 56 | 16 / 70 |
| Directory enumeration | 0 / 0 / 0 | 0 / 0 / 0 | 0 / 0 | 0 / 0 |
| Mixed access | 3 / 8 / 4 | 1 / 8 / 4 | 48 / 36 | 8 / 36 |
| Repeated 1 MiB read | 128 / 0 / 0 | 128 / 0 / 0 | 2,048 / 0 | 2,048 / 0 |

Boot and image-copy time are excluded. These are guest CPU and I/O counts,
not physical-device throughput. The repeated-read working set exceeds both
caches and still reads 2,048 sectors; it does not establish a fully cached read
result. CopperSFS still writes 13 additional sectors for the sequential write
and 14 for small-file creation. Large in-place overwrites can use the strict
fallback, and strict performance is outside these tables. These results do not
establish parity for every workload, device or memory configuration.

All 13 buffered native correctness cases passed, including concurrent writer
processes and a reader, shared handles and file positions/sizes, clean remounts,
both clean handoff directions, metadata, directory mutation, timer-only commits
and one-direction replay of an original `TRST` transaction. All five strict
native cases passed: recovery, concurrent processes, shared handles, the shared
workload/remount and both clean handoff directions. AmigaDOS checks and
independent amidisk extraction/deep checks passed.

Both unsampled recovery sweeps passed all 329 cut-and-reboot cases, with no
failed case or disk-operation index. The original sweep was repeated for each
mode. Every relevant sector write and flush was covered, including individual
sectors within combined device requests, and each image was recovered by its
writing handler.

| Operation | Buffered CopperSFS cuts | Strict CopperSFS cuts | Original cuts per sweep |
| --- | ---: | ---: | ---: |
| Create | 26 | 35 | 15 |
| Overwrite | 15 | 15 | 9 |
| Truncate | 18 | 18 | 12 |
| Rename | 15 | 15 | 11 |
| Delete | 24 | 24 | 15 |

CopperSFS recovered exact old-or-new bytes. Buffered create produced 25 absent
and one complete file; strict create also permits the independently committed
empty file between Open and Write (14 absent, 20 empty, one complete).
The original's outcomes remain observations of its own guarantees; its non-new
overwrite result does not alone prove exact rollback. No unresolved `CSUN`
transaction was handed to original SFS.

The core passed 163 host tests in each of Debug and Release. Another 75 shared
filesystem host cases and 59 focused compiled MC68000 cases passed. Added
coverage exercises payload permutation at multiple capacities/block sizes,
replacement and reordering after partial transfers, read-only bitmap views,
scalar geometry access, cache hash collisions, invalidated checksum tags and
hidden buffered images during recovery. Instruction tests use recording
devices and Exec service doubles; native AmigaDOS execution is separate.
The 68000 HUNK passed mount and the shared workload on the 68020 profile.
A separate 68000 extension-ROM write/fresh-boot-read smoke passed and is
excluded from the performance comparison. The source audit verified 14
handler/test snapshots against the current compiled source files.

The crash model assumes atomic 512-byte sectors and honored flush barriers;
pending device writes are lost at a cut and normal shutdown flushing is
disabled. Host tests additionally exercise subsets of pending writes and live
I/O errors. Physical hardware, torn sectors, controllers that violate flush
ordering and native partitions above 4 GiB remain unqualified. Full 68000
Kickstart floppy replay remains unqualified after the preceding round's
pre-filesystem emulator assertion; it was not repeated in this round. This
source qualification does not update the published preview package.

## Previous adjacent-write qualification, 2026-10-22

The native handler now combines physically adjacent writes within each commit
phase, preserving the existing four durability barriers and undo format.
The optional queue retains owned images across partial I/O failures, allowing
recovery to replace target images with undo images and retry. It never combines
across a flush barrier. Buffered writes remain the default and strict
per-operation durability remains available; see
[transaction batching](transaction-batching.md).

The queue adds 133,920 optional bytes per mounted volume at 512-byte blocks.
Bulk-data copying is limited to 32 KiB per barrier interval, after which
streaming writes can use the caller's buffer directly. The read cache retains
mount/recovery reads, fetches small adjacent windows and excludes pending
writes from prefill. A failed speculative read retries the requested block
alone. Redundant flushes on an already clean device are omitted; failed flushes
remain pending. The fallback without queue memory retains the durability policy.

The compared buffered 68020 handler is 104,952 bytes, SHA-256
`226489F245A86FFE8D94D7AB8D7E29A62AAA720A1B2A6CB40CA4FBAA1ABCD749`.
Original SmartFilesystem 1.279 is 97,984 bytes, SHA-256
`C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.
Each trial began with its own byte-for-byte clone of the same 16 MiB,
512-byte-block image, SHA-256
`36119C09B6480AD11A748EA011774F2A4F30E9659C0A9FC03E13FA4747983ED1`.
Both handlers used Kickstart 3.1, a 68020, 512 KiB chip RAM, 2 MiB fast RAM,
32 mount buffers, Mask `0x7FFFFFFF` and MaxTransfer `0x1FE00`. Geometry was
two surfaces, 32 blocks per track, cylinders 0–511, two reserved blocks and
start offset zero. The compiler and emulator were pinned. Independent checks
used amidisk revision `a4faa7b36bd7c005371d9bb509a936a13d58b43f`.

All 60 unprofiled benchmark trials passed. Every corresponding trial matched
or beat the original's measured read/write/flush request total. Other device
control commands are outside this count. Five-trial medians are:

| Workload | Original write requests | CopperSFS write requests | Original total requests | CopperSFS total requests |
| --- | ---: | ---: | ---: | ---: |
| Sequential 1 MiB write | 132 | 131 | 136 | 136 |
| Small-file creation | 9 | 9 | 17 | 15 |
| Mixed access | 8 | 8 | 15 | 13 |

The previous CopperSFS build issued 196, 70 and 36 write requests respectively.
Both handlers now use four flushes in each write workload. Sequential and
repeated reads use 128 read requests and zero flushes; enumeration uses zero
measured requests for both handlers.

| Workload | Original CPU cycles | CopperSFS CPU cycles | CopperSFS / original |
| --- | ---: | ---: | ---: |
| Sequential 1 MiB read | 2,539,610 | 2,185,050 | 0.860 |
| Sequential 1 MiB write | 14,420,000 | 15,058,870 | 1.044 |
| Small-file creation | 10,373,880 | 10,857,750 | 1.047 |
| Directory enumeration | 7,825,441 | 6,468,870 | 0.827 |
| Mixed access | 9,332,230 | 9,861,690 | 1.057 |
| Repeated 1 MiB read | 2,555,030 | 2,130,271 | 0.834 |

The request savings add CPU work: write medians are 4.4–5.7% above original SFS
and above the preceding CopperSFS build. Read and enumeration medians remain
14–17% below the original. Boot and image-copy time are excluded. These are
guest CPU/request measurements, not physical-device throughput. CopperSFS
still writes more sectors for sequential writes (2,071 versus 2,058) and
small-file creation (70 versus 56); mixed access writes 36 for both handlers.
Its corresponding sector reads are 8/16/8, versus 0/64/48 for the original.
The repeated-read working set exceeds both caches and still reads 2,048 sectors.
Large in-place overwrites can use the strict fallback, so this result does
not establish parity for every workload or memory configuration.

All 13 buffered native correctness cases passed, including concurrent writer
processes and a reader, shared handles, positions and sizes, clean remounts,
both clean handoff directions, metadata, enumeration invalidation, timer-only
commits and replay of an original `TRST` transaction. File contents and
structure passed independent amidisk extraction and deep checks.

Both complete, unsampled recovery sweeps passed all 329 cut-and-reboot cases:
98 buffered CopperSFS cuts, 107 strict CopperSFS cuts, and the original's
62-cut sweep repeated for each mode. Every relevant sector write and flush
was covered, including individual sectors inside combined requests.

| Operation | Buffered CopperSFS cuts | Strict CopperSFS cuts | Original cuts per sweep |
| --- | ---: | ---: | ---: |
| Create | 26 | 35 | 15 |
| Overwrite | 15 | 15 | 9 |
| Truncate | 18 | 18 | 12 |
| Rename | 15 | 15 | 11 |
| Delete | 24 | 24 | 15 |

CopperSFS recovered exact old-or-new bytes. Strict create may retain the empty
file committed by Open before its separate Write; buffered create recovered
either an absent file or the complete new file. Removing a redundant
clean-device flush accounts for one fewer strict cut per operation than the
preceding run. Every cut index was checked against a complete baseline trace.
Original SFS's recorded outcomes remain observations of its own guarantees;
its non-new overwrite outcome alone does not prove exact rollback. No unresolved
`CSUN` transaction was handed to the original. There were no failed cases.

The strict 68020 handler is 104,728 bytes, SHA-256
`3C6A781F89C4D587B544C1BFF2101A1D3D4E33F06C2B47A0780E80052CE5EE19`.
All five strict native cases passed: recovery, concurrent processes, shared
handles, correctness/remount and clean handoff in both directions. Strict
performance is outside the table above. The buffered 68000 handler is
106,728 bytes, SHA-256
`12AE8BA4D4E47FE3E3BB996F775C573F7110D4E356F85191ABD19423E5F6CECC`;
it passed mount and the shared workload on the 68020 profile.

The core passed 154 host tests in each of Debug and Release. Another 75 shared
filesystem host cases and 54 focused compiled MC68000 cases passed. New tests
cover queue ownership, adjacent/disjoint runs, full queues, arbitrary pending
sector subsets, failed writes/flushes and retry, partial completions, DMA and
transfer limits, and cache coherence. Instruction tests use recording devices
and Exec service doubles. A separate 68000 extension-ROM write/fresh-boot-read
smoke passed and is excluded from the handler benchmark. The evidence audit
verified every recovery cut, all 60 unique trials, fixture and handler hashes,
compiled source checksums and identical rebuilt handler binaries.

The crash model assumes atomic 512-byte sectors and honored flush barriers;
pending device writes are lost at a cut and normal shutdown flushing is disabled.
Host tests additionally exercise subsets of pending writes and live I/O errors.
Physical hardware, torn sectors, controllers that violate flush ordering and
native partitions above 4 GiB remain unqualified. Full 68000 Kickstart floppy
replay remains unavailable after the preceding round's pre-filesystem emulator
assertion; it was not repeated in this round. This source qualification does
not update the published preview package.

## Previous buffered engine qualification, 2026-10-21

The default external handler now uses SFS-style buffered writes. Successful
mutations are immediately visible to readers; durability belongs to the next
completed filesystem checkpoint. A crash may lose recent unflushed changes.
Explicit filesystem flush, clean inhibit, buffer pressure and a five-second
timer request a checkpoint. Strict per-operation durability remains an option.
Both modes retain the undo journal and four ordered durability barriers; see
[transaction batching](transaction-batching.md).

The 68020 handler used for this comparison is 101,232 bytes, SHA-256
`0814AD1296B3D479C6B9B05056ACCF365C7933ECCF83F35EE7D38733F102FF25`.
Original SmartFilesystem 1.279 is 97,984 bytes, SHA-256
`C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205`.
Both ran under the same Kickstart 3.1 emulator profile: 68020, 512 KiB chip
RAM, 2 MiB fast RAM, and 32 mount buffers. Each run started from a separate
byte-for-byte copy of the immutable 16 MiB, 512-byte-block image, SHA-256
`36119C09B6480AD11A748EA011774F2A4F30E9659C0A9FC03E13FA4747983ED1`.
The compiler and emulator were pinned; the original handler was remeasured
with the same updated guest workload as CopperSFS.

All 60 unprofiled trials passed: five per handler for each workload. The table
shows median guest CPU cycles, excluding boot and image-copy time.

| Workload | Original 1.279 | CopperSFS | CopperSFS / original |
| --- | ---: | ---: | ---: |
| Sequential 1 MiB read | 2,539,610 | 2,106,781 | 0.830 |
| Sequential 1 MiB write | 14,419,871 | 13,759,010 | 0.954 |
| Small-file creation | 10,346,320 | 10,090,131 | 0.975 |
| Directory enumeration | 7,840,430 | 6,344,680 | 0.809 |
| Mixed access | 9,342,580 | 9,350,030 | 1.001 |
| Repeated 1 MiB read | 2,555,080 | 2,121,220 | 0.830 |

Mixed access is effectively tied; the other medians are lower for CopperSFS.
The previous strict engine took 103,448,180 cycles and 517 flushes for the
sequential write. The new buffered default takes 13,759,010 cycles and four
flushes. This comparison includes the authorized durability-policy change and
68020 code generation; it is not a claim that strict mode has the same speed.
The write buffer uses 50,256 bytes at 512-byte blocks, and the optional
enumeration result cache adds 8,836 bytes.

These are CPU measurements, not physical-device throughput. For sequential
writes CopperSFS issues 12 sector reads, 2,071 sector writes and 196 write
requests; original SFS issues no reads, 2,058 sector writes and 132 write
requests. Both use four flushes. Small-file and mixed-access writes also use
more device requests in CopperSFS, so real storage latency can change the
comparison. The repeated-read working set exceeds the cache: both handlers
still read 2,048 sectors. Large in-place overwrites can use the strict fallback
and are not covered by the sequential new-file write result.

All 13 native correctness cases passed. Coverage includes two DOS writer
processes with a parent reader, a separate two-writer/reader shared-file test,
positions and sizes, clean remounts, both clean handoff directions, directory
cache invalidation and copied enumeration tokens, metadata, timer-only commit,
and CopperSFS replay of an original `TRST` transaction. Original 1.279 rejects
resize with other open handles; that operation has a separate CopperSFS
regression rather than an assumed common behavior. Independent amidisk checks
validate structure and agreed file contents.

The complete buffered recovery sweep passed all 160 cut points: 98 for
CopperSFS and 62 for original SFS, across create, overwrite, truncate, rename
and delete. Every relevant sector write and flush was tested without sampling.
Each image was rebooted with its writing handler. CopperSFS recovered either
the previous checkpoint or the complete new checkpoint; none of these buffered
create cases retained an intermediate empty file. The original's observed
results are not a stronger guarantee than its documented behavior. No
unresolved CopperSFS transaction was handed to the original handler.
Exact old-or-new byte comparisons are required for CopperSFS. The original's
checks record structural consistency and guest-observed recovery outcomes;
its non-new overwrite result alone does not prove exact rollback.

The optional strict handler is 101,020 bytes, SHA-256
`F9DF60A9104148260AFE7988DC0D698AF7F80EE8121CE82914FE9FBB25710E74`.
Its unsampled recovery sweep also passed: 112 CopperSFS cut points and another
62 for original SFS. Strict create may preserve the empty file committed by
Open before its separate Write packet; this is consistent with per-operation
durability. Buffered mode instead commits those operations together when they
fit in one checkpoint.
Strict mode also passed the concurrent-process, shared-handle, clean-remount
and two-direction handoff cases. An evidence audit checked all 60 unique
performance trials, every cut index against complete baseline I/O traces,
the immutable fixture and handler hashes, and compiled source checksums.

The final core passed 139 host tests in each of Debug and Release. Another 75
shared filesystem host cases and 45 focused compiled MC68000 cases passed.
The instruction cases use recording device boundaries and Exec service
doubles; native AmigaDOS execution is covered separately above. A separate
68000 extension-ROM write/fresh-boot-read smoke passed and is excluded from
the performance figures.

The 68000-targeted handler also passed mount and the shared workload on the
68020 emulator. A separate full Kickstart 3.1 floppy replay on a 68000 profile
stopped in an emulator display-order assertion before filesystem startup, so
that full-68000 replay is not qualified by this run. Physical hardware, torn
sectors, controllers that violate flush ordering, and native TD64/NSD
partitions above 4 GiB remain outside this qualification.

## Previous strict engine qualification, 2026-10-20

On 2026-10-20 the standalone core's 113 host semantic tests passed in Debug
and Release configurations. They cover allocation, metadata and directory mutation,
interleaved reads and writes, injected failures at write and flush points, and
recovery after an interrupted upper-block append on a sparse logical volume
larger than 4 GiB. Batched transactions are tested with buffer capacities of
1, 2 and 32 blocks, both buffered and immediately persistent device writes,
cuts after every write and flush, and live I/O failure followed by recovery
and retry. An overwrite case also enumerates every subset of pending sectors
that could reach media before a flush. Allocation tests cover used bitmap
words and cursor wraparound, including every write/flush interruption point.
Transfer tests cover aligned and unaligned buffers, partial sectors, fragmented
files, and preservation of bytes outside an overwritten range. These are host
semantic tests, not compiled MC68000 execution. Four checksum regression cases
corrupt the zero tail of transaction markers and undo headers and require
recovery to reject the damaged block.

Thirty-seven focused compiled MC68000 cases also passed. They cover aligned
and unaligned transfers, fragmented extents, guard bytes, copy and clear
boundaries, forward overlap, DMA masks and memory requirements, transfer
splitting, and TD64 offsets. They use RAM-backed platforms, Exec copy service
doubles, and recording device boundaries. Native AmigaDOS and real Exec
execution are covered separately by the booted tests below; these instruction
tests do not establish real-device DMA behavior.

The earlier optimized MC68000 handler passed all 12 native A/B tests against
original SmartFilesystem 1.279 under Kickstart 3.1, using a 68020 emulator
profile with 512 KiB chip RAM and 2 MiB fast RAM. Each independent workload
started from its own byte-for-byte copy of the same independently formatted
16 MiB, 512-byte-block image. The shared workload includes interleaved writers
and a reader, file positions, directory changes and metadata. Clean remounts,
clean handoff in both directions, and CopperSFS replay of an original `TRST`
transaction passed, with amidisk's independent deep consistency and file checks.

The unsampled native recovery sweep passed all 177 recorded write and flush
cut points: 115 for CopperSFS and 62 for the original handler, across create,
overwrite, truncate, rename and delete. Reboots used the writing handler.
CopperSFS recovered the old or new state of each individual mutation; the
create workload can also retain the empty file committed by Open before its
separate Write packet. The original handler's observed outcomes do not imply
stronger guarantees than its documentation establishes. These tests model
atomic 512-byte sectors and honored flush barriers, not torn sectors or
controller reordering.

The same native run completed 60 performance trials: five trials per handler
for each of five cold workloads, plus separate repeated-read trials after
warming the cache. Measurements exclude boot and image-copy time and describe
guest CPU cycles and device I/O, not physical Amiga throughput.
The tested handler's SHA-256 is
`B35735D4DDFA32E8AC349DE97061783D5DA01F45C97E3E3CECD80E0923A128CD`.

Compared with the previous optimized build (`EE2A78382C28…`), the sequential
1 MiB write median fell from 152,281,271 to 103,448,180 guest CPU cycles:
32% fewer cycles, or 1.47 times faster. Original 1.279 measured 14,419,369 cycles
in the same run, leaving a 7.17-times CPU gap, down from 10.56 times.
CopperSFS still issued 1,162 sector reads, 3,857 sector writes and 517 flushes.
Sector reads, writes and flushes were unchanged in all 60 corresponding trials
across both handlers and all workloads. The compiler and emulator versions,
ROM, workload, geometry and immutable base image matched the preceding run.

The gains come from reusing geometry within operations, avoiding unnecessary
buffer copies, using Exec's copy routines, advancing transaction-buffer slot
pointers, and unrolling checksum sums. Checksum generation for freshly cleared
journal headers sums their populated prefix; reads and recovery still validate
the entire block. Transaction boundaries and durability barriers are unchanged.
An audit confirmed identical ordered sector writes and flushes for all five
CopperSFS recovery workloads compared with the preceding build.
Sequential reads improved 1.34 times, small-file creation 1.47 times, directory
enumeration 1.44 times, and mixed access 1.42 times. The compiled handler grew
by 396 bytes to 83,488 bytes.

The preview handler is compiled for MC68000. A separate check loaded and
unloaded its HUNK with native AmigaDOS `LoadSeg` and `UnLoadSeg`. In an emulated
A500 with a licensed Kickstart 3.1 ROM, the handler then received
`ACTION_STARTUP` from native AmigaDOS on `trackdisk.device`, read an existing
1,300-byte file, created and read back a 1,024-byte file, and read it again
after a fresh boot. Another native run launched two DOS processes that each
wrote a separate 512-byte file while the parent read the SFS volume. The
completed images were mounted and their file bytes checked by a host-side
CopperSFS reader.

One native power-cut replay captured a complete ADF while the SFS transaction
marker indicated an unfinished write. A fresh Kickstart 3.1 boot recovered
that image and produced a mountable volume whose file bytes passed the same
host-side reader. This is one interruption point, not exhaustive crash coverage.

The earlier native runs described above used a synthetic SFS floppy image.
Their host-side reader shares the CopperSFS core, so those checks alone are
not independent implementation checks. The newer hardfile A/B suite
uses amidisk as described above. Physical Amiga hardware, other ROM versions,
and native TD64/NSD partitions above 4 GiB have not been qualified by this
comparison.
