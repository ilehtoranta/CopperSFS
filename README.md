# CopperSFS

CopperSFS is an experimental, allocation-free implementation of the classic
Amiga Smart File System on-disk format (`SFS\0`). This repository contains the
portable filesystem core and its host tests.

**Status: source preview. No binary handler release is available from this
repository.** Do not rely on it as the only copy of important data. The
standalone AmigaOS `L:` handler is not included. Its startup, read/write,
remount and interrupted-write behavior has not yet been qualified under a
native ROM. The core tests are not a substitute for that native test.

## What is here

- `src/CopperSFS.Core`: read, write and recovery logic for classic `SFS\0`
  volumes. The caller supplies block I/O, big-endian guest memory and scratch
  buffers through `IAmigaFileSystemPlatform`.
- `tests/CopperSFS.Core.Tests`: 30 semantic tests for traversal, mutation,
  interleaved file operations, interrupted writes, recovery and sparse volumes
  larger than 4 GiB.
Public types use the `CopperSFS` namespace. Integrators supply their own block
device and guest-memory adapter through the platform interfaces.

The engine does not format volumes. It uses classic 32-bit AmigaDOS file sizes
and seek positions. A partition above 4 GiB additionally needs a block device
with TD64 or NSD 64-bit I/O; block-device integration is outside this core.

CopperSFS writes a private `CSUN` undo journal. If power is lost during a
CopperSFS mutation, remount with CopperSFS to recover it before exposing the
volume through another SFS implementation. Recovery requires a writable device
and an intact journal. See [compatibility and recovery](docs/compatibility.md).

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
references and independent validation
sources are listed in [provenance](docs/provenance.md).

Contributions and independent disk-image compatibility reports are welcome.
Please use disposable images when testing writes and include the exact handler,
device and system version in bug reports.
