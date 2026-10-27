# CopperSFS

Current source includes bounded TRST transactions and legacy CSUN recovery.
See [the mainline integration notes](docs/trst-mainline-integration.md) for
atomicity, durability policy and validation scope. Released packages retain
their separately documented build and qualification identities.
The [Write tail and p95 report](docs/trst-tail-latency.md) records the current
optimization, its checkpoint tradeoff and compiled-handler checks.

CopperSFS is an experimental, allocation-free implementation of the classic
Amiga Smart File System on-disk format (`SFS\0`). This repository contains the
portable filesystem core and its host tests.

**Status: experimental preview.** [Preview.4](https://github.com/ilehtoranta/CopperSFS/releases/tag/v0.1.0-preview.4)
provides a buffered 68020 handler with a 256 KiB clean-data cache, optional
68020 binaries with other cache sizes or strict writes, and a separate 68000
compatibility package with limited validation. Select the package for your CPU
and read the [installation and upgrade guide](docs/handler-install.md).
The handler integration source is not part of this repository. The external
handler has been compared with
original SmartFilesystem 1.279 under Kickstart 3.1, including clean handoff and
write/flush interruption sweeps. Source changes and published preview binaries
have separate qualification records: see [validation](docs/validation.md).
Other ROM versions and physical hardware remain unqualified. Use only a
disposable volume with an independent backup. Buffered writes are the default
in preview.4; recent unflushed changes may be lost after a crash.

## What is here

- `src/CopperSFS.Core`: read, write and recovery logic for classic `SFS\0`
  volumes. The caller supplies block I/O, big-endian guest memory and scratch
  buffers through `IAmigaFileSystemPlatform`.
- `tests/CopperSFS.Core.Tests`: semantic tests for traversal, mutation,
  interleaved file operations, interrupted writes, recovery and sparse volumes
  larger than 4 GiB.

Public types use the `CopperSFS` namespace. Integrators supply their own block
device and guest-memory adapter through the platform interfaces.
`BlockCount`, `BlockBytes` and `ReservedBlocks` must match the corresponding
fields in `Geometry` and remain stable while mounted. Scalar getters let native
adapters avoid constructing the full device descriptor during traversal.

The engine does not format volumes. It uses classic 32-bit AmigaDOS file sizes
and seek positions. A partition above 4 GiB additionally needs a block device
with TD64 or NSD 64-bit I/O; block-device integration is outside this core.

The core provides original-format `TRST` and legacy `CSUN` journals. Current
classic standalone builds default to buffered TRST with whole-Write atomicity.
An interrupted legacy CSUN transaction still needs CopperSFS recovery before
the volume is exposed through another SFS implementation. Recovery requires a
writable device and an intact journal. See [compatibility and recovery](docs/compatibility.md).

Integrators can select bounded buffered writes or strict per-operation
durability. Buffered changes are immediately visible to readers; an explicit
filesystem flush makes the checkpoint durable. Recent unflushed changes may
be lost after a crash. Both policies use the same recovery protocol. See
[buffering and integration](docs/transaction-batching.md).

An optional caller-owned [clean-data cache](docs/data-cache.md) can retain file
data without adding device requests or changing durability. The packaged 68020
handlers offer fixed cache budgets; choose the executable in the
[installation guide](docs/handler-install.md). Integrators control memory,
admission and invalidation in their own adapters.

## Build and test

Install .NET 10 and clone the public
[CopperSharp68k](https://github.com/ilehtoranta/CopperSharp68k) repository
beside this one, or provide compatible CopperSharp SDK packages. Then run:

```sh
dotnet test CopperSFS.slnx -c Release
```

The default local project reference expects `../CopperSharp68k`. Set
`CopperSharp68kRoot` when it is elsewhere. These tests exercise host semantics;
they do not execute a compiled MC68000 handler. See
[validation](docs/validation.md) for the coverage boundary.

## Name and provenance

CopperSFS is a new implementation and is not the original
[Smart File System C project](https://sourceforge.net/projects/smartfilesystem/).
That project's source is published under LGPLv2. This repository is
MIT-licensed; the original C source is not included. The classic format
references and independent validation sources are listed in
[provenance](docs/provenance.md).

Contributions and independent disk-image compatibility reports are welcome.
Please use disposable images when testing writes and include the exact handler,
device and system version in bug reports.
