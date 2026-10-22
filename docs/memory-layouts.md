# Named layouts and typed memory views

The write queue, buffered writer, and strict transaction staging area use named
constants from `AmigaFileSystemBufferLayout.cs`. These describe big-endian guest
memory, independently of CLR object layout. Header fields, entries, hash tables,
image counts, and index strides have one declaration.

The physical write queue now accesses its fixed header through a borrowed view:

```csharp
var header = new AmigaFileSystemWriteQueueHeaderView<T>(ref memory, queueAddress);
header.Count++;
header.BulkBlocks = header.Capacity >> 1;
```

The view contains a reference to the platform and the guest address. Construction
does not access guest memory; each property performs one `IAmigaGuestMemory`
read or write. `Order` and `Data` have type `APTR`; other fields are `uint`.
Replacing the platform after constructing the view remains observable. The view
does not allocate a queue, copy the platform, cache fields, or copy block images.

Variable-index queue entries still use named offsets. Existing public buffer
offset constants remain available as aliases for adapter compatibility.
Payload ownership, queue capacity, sorting, savepoints, checksums, retry handling,
and commit barriers retain their existing implementation. Writable metadata must
continue through the existing Edit/Store and commit paths when adding other views.

## Compiler requirements

The original preview.3 compiler could not compile the borrowed generic view and
left constructor/accessor calls in the address-only experiment. The compiler
changes supporting this implementation resolve imported closed generic value
types, admit stateless application receivers, inline small value-type accessors
and constructors, and promote nonescaping view fields into scalar values.
They also remove obsolete call-register constraints after folding a memory call
into a direct load/store. Escaping storage and `NoInlining` boundaries are retained.

The validated compiler assembly SHA-256 is
`6EAD3072BA5FEDA3B58D17C04FB0ADE7F118011315C3DB77618AE12D7C461CE9`.
Use a compiler containing these changes; the earlier released compiler is not
sufficient to build this source. The [prototype](../prototypes/CopperSFS.WriteQueueViews)
references the production view, including its pointer-valued setters.

## Code-generation checks, 22 October 2026

| Probe | Direct, 68000 | View, 68000 | Direct, 68020 | View, 68020 |
| --- | ---: | ---: | ---: | ---: |
| Scalar reads/writes and arithmetic | 58 bytes | 58 bytes | 46 bytes | 46 bytes |
| Pointer-valued setters and reads | 44 bytes | 42 bytes | 44 bytes | 42 bytes |

Both production-view probes retain only the entry method, with no calls or local
frame storage. Scalar instruction counts and emulated CPU cycles equal direct
access for each tested input; pointer setters save one instruction (four cycles
on 68000, two on 68020). Register assignments can differ, so matching
performance does not imply identical executable hashes.

Sixty compiled probe executions cover 68000/68020, longword- and word-aligned
addresses, multiple block sizes, count wraparound, neighbouring guard bytes,
return values, and stack balance. This includes the unused address-only
alternative, which remains two code bytes larger than direct access. Compiler
regression tests also cover imported generic views, mutable borrowed platforms,
escaping views, and explicit `NoInlining` accessors. Host tests check big-endian
bytes and the exact sequence of memory operations.

The complete handler changed and passed a new native A/B run. See the
[typed-view validation](typed-memory-view-validation.md) for the exact isolated
candidate hashes, performance counts, recovery results, and coverage limits.
The earlier named-offset-only refactor produced byte-identical preview.3 handlers;
that evidence applies to those earlier binaries, not to this compiler change.

## Reproducing the code-generation comparison

Supply the compiler CLI and a directory containing the built `CopperSFS.Core.dll`,
`CopperSharp.Sdk.Amiga.dll`, and `CopperSharp.Sdk.Amiga.Support.dll`. Use a new output
directory outside the checkout:

```powershell
./prototypes/CopperSFS.WriteQueueViews/Compare-Codegen.ps1 `
  -CompilerCli <compiler-cli.dll> `
  -InputDirectory <qualified-managed-inputs> `
  -OutputDirectory <new-local-evidence-directory> `
  -RequireNoViewOverhead
```

The script retains diagnostics and maps, hashes its inputs and probe sources, and
writes `codegen-report.json`. `-RequireNoViewOverhead` checks successful compilation,
no greater code size, one retained method, and no frame storage for the production
views. It is a static code-generation gate; execution tests and native A/B runs
provide the separate correctness and performance evidence. `-RequireEquivalent`
retains the stronger byte-identical-executable comparison for all experiments and
is expected to fail when register assignments differ.
