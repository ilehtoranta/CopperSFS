# Contributing

Please reproduce filesystem changes with a small disposable SFS image or a
host semantic test. For guest-visible behavior, prefer Amiga autodocs, SDK
ABI evidence, or comparison with an original handler over modern defensive
assumptions. State whether a result is a host semantic test, compiled MC68000
test, or native-ROM/media replay.

Run `dotnet test CopperSFS.slnx -c Release` before proposing a change. Do not
commit ROMs, disk images, credentials or generated build artifacts. Document
the origin and license of source or test fixtures taken from other projects.
