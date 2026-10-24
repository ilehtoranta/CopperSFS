# Optional clean-data cache

`AmigaFileSystemDataCache` stores clean file data in bounded caller-owned memory.
It performs no device I/O and allocates no memory. Its presence does not change
the journal format, write buffering, flush barriers or recovery contract.

The caller initializes a pool with a filesystem block size and a power-of-two
page count from 4 through 256. Each page holds sixteen blocks. Validity is tracked
per block, so unaligned ranges and partially populated pages do not expose
uninitialized memory. Four-way sets bound lookup work. Pages reused by readers
receive a second chance; new pages are probationary. This is a small second-chance
policy, not a full implementation of 2Q.

## Integration contract

Reads must check the logical write-back buffer, strict transaction staging and
physical write queue before this cache. `TryRead` succeeds only when the complete
requested range is present. A miss leaves the caller's destination untouched.
The caller can therefore issue its original contiguous transfer: cached islands
never require extra device requests. `Store` admits a successfully read physical
range; do not store dirty logical overlays or incomplete/failed transfers.

Cache admission is a platform policy. A conservative native policy observes an
initial sequential pass without copying its data into the cache. A reread or seek
on the same handle permits admission. A subsequent pass can then hit the cache.
Readers share cache contents even when their own handles have not enabled
admission. Large streaming reads from files exceeding the pool can bypass both
lookup and admission. Admission has a copy cost; warmup must be measured separately
from fully cached reads. This cache adds no speculative read-ahead.

Invalidate before logical mutations, physical writes, block reuse, recovery,
media changes and returning from an external volume handoff. The simplest correct
integration calls `Invalidate` for the whole volume. This advances a generation
in constant time; generation wrap clears entry tags so old data cannot reappear.
Failed operations also invalidate their cached data. No cached entry is required
for recovery. A power cut discards the pool without flushing it.

One serialized owner must operate the cache, as with the other caller-owned
filesystem buffers. Concurrent applications can use a handler that serializes
their requests; the cache itself is not a host-thread synchronization primitive.

## Memory

Storage is `32 + pages * (16 + 16 * blockBytes)` bytes. With 512-byte blocks:

| Data capacity | Pages | Total pool bytes | Bookkeeping bytes |
| --- | ---: | ---: | ---: |
| 128 KiB | 16 | 131,360 | 288 |
| 256 KiB | 32 | 262,688 | 544 |
| 512 KiB | 64 | 525,344 | 1,056 |
| 2 MiB | 256 | 2,101,280 | 4,128 |

These figures exclude the existing metadata cache, write-back and transaction
buffers, physical write queue, filesystem state and loaded code. Four-way set
collisions and page alignment can reduce effective capacity. At mount, allocation failure
falls back to fewer pages or uncached reads without changing durability or
making an otherwise valid volume unmountable. Explicit runtime resizing instead
requires the exact requested pool and preserves the old pool on failure; see
[handler settings](handler-install.md). Cache copies support ordinary RAM;
the physical transfer layer retains responsibility for DMA restrictions.
