# CopperSFS

CopperSFS is an experimental, allocation-free implementation of the classic
Amiga Smart File System on-disk format (`SFS\0`). It began in CopperStart, but the core is
available here as a separate library so it can be tested and improved without
the rest of that operating system.

**Status: source preview. No binary handler release is available from this
repository.** Do not rely on it as the only copy of important data. The
standalone `L:CopperSFS` handler source is included here, but its current build
still uses CopperStart's DOS and Exec runtime. Its AmigaOS startup, read/write,
remount and interrupted-write behavior has not yet been qualified under a
native ROM. The core tests are not a substitute for that native test.

## What is here

- `src/CopperSFS.Core`: read, write and recovery logic for classic `SFS\0`
  volumes. The caller supplies block I/O, big-endian guest memory and scratch
  buffers through `IAmigaFileSystemPlatform`.
- `tests/CopperSFS.Core.Tests`: 30 semantic tests for traversal, mutation,
  interleaved file operations, interrupted writes, recovery and sparse volumes
  larger than 4 GiB.
- `integration/CopperStart`: the `L:` startup and `$E00000` extension bridge
  currently built by CopperStart. This is source for review and integration;
  it is not yet an independently buildable handler project.

The public types currently retain the `CopperStart.Dos` namespace so existing
CopperStart callers can consume this assembly without an ABI rename. The
assembly and repository are named CopperSFS.

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
That project's source is published under LGPLv2. This repository contains
source extracted from CopperStart under its MIT license; the original C source
is not included. The classic format references and independent validation
sources are listed in [provenance](docs/provenance.md).

Contributions and independent disk-image compatibility reports are welcome.
Please use disposable images when testing writes and include the exact handler,
device and system version in bug reports.
