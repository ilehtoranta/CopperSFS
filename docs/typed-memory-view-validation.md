# Typed memory view validation — 22 October 2026

The write queue now uses a borrowed, typed view for its fixed header. The
compiler removes the view's constructors, accessors, and aggregate storage from
the native paths. Queue capacity, payload ownership, commit phases, flush
barriers, checksums, and the buffered/strict durability policies are unchanged.
See [memory layouts](memory-layouts.md) for the source and code-generation checks.

Native A/B qualification passes for the development candidates below. They
isolate the view/compiler change using frozen adapter inputs and a paired core
snapshot. This is not a package release or qualification of unrelated source
changes being developed concurrently.

The source commits were subsequently isolated onto the published compiler base,
including the required pointer-wrapper folding prerequisite. That clean source
build is checked separately with focused compiler and portable-core tests. The
native A/B figures below apply to the recorded development compiler and handler
hashes; they do not qualify a handler rebuilt with the isolated compiler branch.

## Candidate and fixture identities

| Handler | Bytes | SHA-256 |
| --- | ---: | --- |
| CopperSFS buffered, 68020 | 103,308 | `D95E21A3ED9DD20F3E21287ACEEC341AF2F54000E8F8450213678252BFE4E20D` |
| CopperSFS strict, 68020 | 103,092 | `A5A82B1A03668BF1DB0EB4FB842D0F6FB36CDE392460C301B3A05FB9FCF38BDE` |
| CopperSFS buffered, 68000 | 104,996 | `5DE48EDFFAFCB19D645EE28973BCFB239AF3F8A1746D423DA602A1915C26446A` |
| Original 68k SmartFilesystem 1.279 | 97,984 | `C7DD174131035A0F5845BE1076FB5C55642F6AA2125D4B5D8C029B141DCC8205` |

Compiler assembly SHA-256:
`6EAD3072BA5FEDA3B58D17C04FB0ADE7F118011315C3DB77618AE12D7C461CE9`.
Core assembly SHA-256:
`63E779A99C32EDFE41412361F43EA69F59921E7DFBBC438C3E1164400EE4A38F`.

The immutable base is a 16 MiB `SFS\0` hardfile with 512-byte blocks, SHA-256
`36119C09B6480AD11A748EA011774F2A4F30E9659C0A9FC03E13FA4747983ED1`.
The independent checker is amidisk revision
`a4faa7b36bd7c005371d9bb509a936a13d58b43f`. Original archive SHA-256:
`E6C4C3AEE7BAC96C7E6EDDED162FDA8B5F9D9ABF9680112B5028C5CF96B47D92`.
Kickstart 3.1 SHA-256:
`8C8A0CF04F91B88EAF0C4F1126041987067E2286A8EE590BDBAE447A8000C5EE`.

Both handlers use the same 68020 emulator profile, 512 KiB chip RAM, 2 MiB fast
RAM, hardfile device, two surfaces, 32 blocks per track, cylinders 0–511, 32
buffers, and mask `0x7FFFFFFF`. Every independent workload starts from a fresh
byte-for-byte copy of the base image.

## Generated code and correctness checks

- 299 focused compiler cases pass, including imported generic views, borrowed
  adapter replacement, escaping views, explicit `NoInlining`, register
  constraints, and adjacent/overlapping frame fields.
- 60 compiled probe executions pass on 68000 and 68020. Production views retain
  one entry method, no calls, and no view frame storage. Scalar probes match
  direct access in code size, instruction count, and cycles; pointer setters
  save one instruction. This does not require identical register assignments.
- 53 compiled MC68000 filesystem buffer/transfer cases pass.
- Four native smoke cases pass for the 68000 HUNK on the shared 68020/Kickstart
  profile, including concurrent processes and a clean remount. These are not a
  complete native 68000-machine qualification.
- A separate native MC68000 extension-ROM write/fresh-boot smoke passes. It is
  excluded from the `L:` performance comparison.

All 13 buffered native cases and all five strict native cases pass. Coverage
includes shared correctness and metadata, concurrent processes, shared-handle
visibility, resize and file positions, directory mutations and copied
enumeration tokens, timer commits without another DOS packet, fresh remount,
and clean handoff in both directions. CopperSFS replay of an original `TRST`
transaction also passes. There are no failed qualifying cases or failed
disk-operation indices.

## Performance

All 60 unprofiled trials pass: five trials per handler for each of five cold
workloads plus separate repeated-read trials after warming the cache. The shared
correctness/remount gate passed before benchmarking. Boot and image-copy time
are excluded. These are guest CPU and device-I/O measurements, not physical
Amiga throughput measurements.

| Workload | Original cycles | CopperSFS cycles | Fewer cycles than original |
| --- | ---: | ---: | ---: |
| Sequential 1 MiB read | 2,539,810 | 2,119,260 | 16.6% |
| Sequential 1 MiB write | 14,419,820 | 13,336,581 | 7.5% |
| Small-file creation | 10,369,170 | 9,998,830 | 3.6% |
| Directory enumeration | 7,825,501 | 6,231,931 | 20.4% |
| Mixed access | 9,342,580 | 9,104,230 | 2.6% |
| Repeated 1 MiB read | 2,555,150 | 2,138,161 | 16.3% |

The table reports medians. In this run, all five raw cycle measurements within
each handler/workload pair equal their median. Sequential writes use 1.7% fewer
cycles than the published preview.3 qualification (13,563,520 cycles).
Small whole-handler timing differences can reflect code layout and guest timing;
the generated-code probes provide the direct evidence that the view itself
introduces no accessor calls, adapter copies, or aggregate frame storage.

All five raw trials of each pair also have exactly the following I/O counts.
Requests are read/write/flush requests; other device control commands are
outside this count. Each sector is 512 bytes.

| Workload | Handler | Sector reads | Sector writes | Read requests | Write requests | Flushes | Total requests |
| --- | --- | ---: | ---: | ---: | ---: | ---: | ---: |
| Sequential read | Original | 2,048 | 0 | 128 | 0 | 0 | 128 |
| Sequential read | CopperSFS | 2,048 | 0 | 128 | 0 | 0 | 128 |
| Sequential write | Original | 0 | 2,058 | 0 | 132 | 4 | 136 |
| Sequential write | CopperSFS | 8 | 2,071 | 1 | 131 | 4 | 136 |
| Small-file creation | Original | 64 | 56 | 4 | 9 | 4 | 17 |
| Small-file creation | CopperSFS | 16 | 70 | 2 | 9 | 4 | 15 |
| Directory enumeration | Original | 0 | 0 | 0 | 0 | 0 | 0 |
| Directory enumeration | CopperSFS | 0 | 0 | 0 | 0 | 0 | 0 |
| Mixed access | Original | 48 | 36 | 3 | 8 | 4 | 15 |
| Mixed access | CopperSFS | 8 | 36 | 1 | 8 | 4 | 13 |
| Repeated read | Original | 2,048 | 0 | 128 | 0 | 0 | 128 |
| Repeated read | CopperSFS | 2,048 | 0 | 128 | 0 | 0 | 128 |

All 60 trials retain the preview.3 sector-read, sector-write, read-request,
write-request, and flush counts. CopperSFS still writes more sectors for the
sequential-write and small-file workloads. The repeated 1 MiB read exceeds the
small data cache, so it still reads the data from the device. Queue storage
remains 69,408 bytes, logical buffering 50,256 bytes, and optional enumeration
storage 8,836 bytes for this configuration. The buffered 68020 handler is 1,260
bytes smaller than preview.3.

## Native recovery results

All 329 unsampled cut-and-reboot checks pass. Each sweep interrupts every
relevant sector-write and flush boundary in the baseline trace, starting each
cut from a fresh base-image clone. Original SFS is tested separately in both
mode comparisons, giving 124 reference checks in addition to 98 buffered and
107 strict CopperSFS checks.

| Operation | Original per comparison | CopperSFS buffered | CopperSFS strict |
| --- | ---: | ---: | ---: |
| Create/write | 15 | 26 | 35 |
| Overwrite | 9 | 15 | 15 |
| Truncate | 12 | 18 | 18 |
| Rename | 11 | 15 | 15 |
| Delete | 15 | 24 | 24 |
| Total | 62 | 98 | 107 |

Every recovered image mounted and passed the independent deep consistency and
file-content checks. CopperSFS's promised old-or-new results pass for overwrite,
truncate, rename, and delete. The create/write workload spans separate DOS
operations: recovery may find no file, a valid empty file after creation, or
the completed written file. Original SFS outcomes are recorded separately.
There are no failed recovery cases or failed disk-operation indices.

## Recovery model and evidence

Power cuts discard unflushed writes. The model assumes atomic 512-byte sectors
and honored flush barriers; it does not cover torn sectors, controller
reordering across flushes, or physical hardware. Each image is recovered using
the handler that wrote it, preserving previously flushed data. Buffered mode may
lose recent unflushed changes; strict mode retains its per-operation durability
contract. An unresolved CopperSFS `CSUN` transaction is never handed to the
original handler. CopperSFS replay of an original `TRST` transaction is checked
separately.

Local machine-readable evidence records the candidate/base/ROM hashes, emulator
settings, each operation's disk index and ordered sector/flush events, recovered
outcomes, independent file/deep-consistency checks, and raw performance trials.
Reference binaries, ROMs, and disk images stay outside Git. No package is
published by this qualification.
