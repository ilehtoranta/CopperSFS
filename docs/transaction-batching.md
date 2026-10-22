# Buffered writes and strict transactions

The core supports two durability policies. An embedding application selects
buffered writes by initializing caller-owned storage with
`AmigaSmartFileSystemWriteBuffer.Initialize` and supplying it in `Work.Buffered`.
Leaving `Work.Buffered` null retains strict per-operation durability. Both
policies use the same on-disk `CSUN` undo records and recovery barriers.

## Buffered policy

A successful operation becomes visible to all readers immediately. Several
operations can share a checkpoint; recent changes may be lost after a crash
until `AmigaSmartFileSystemWriteCore.Flush` succeeds. Previously flushed data
remains protected. Call Flush before clean unmount or handing a volume to
another filesystem implementation. Closing a file is not a durability barrier.

The buffer holds at most 32 distinct dirty blocks. Each slot retains its
checkpoint image and a lazy savepoint for the current operation. A failed
operation restores only its savepoint, preserving earlier acknowledged work.
Before acknowledging a new dirty block, the writer reserves two free sectors
for its undo image and chain link. Data allocation excludes these reservations
and blocks allocated at the previous checkpoint. A full disk therefore cannot
strand a previously accepted group without space for its journal.

Commit writes the undo images and links, flushes, writes and flushes the dirty
marker, writes and flushes home blocks, then writes and flushes the clean
marker. A crash recovers the previous or completed checkpoint. On an I/O error
the RAM images remain available; physical recovery hides this overlay before
replaying the journal, then a later commit can retry. A full buffer first rolls
back the current operation, commits prior successful work, and retries. An
operation too large for the buffer uses the strict transaction path.

At 512 bytes per block the 32-slot write buffer uses 50,256 bytes, in addition
to scratch space, the strict transaction buffer and the platform's read cache.
This is bounded memory supplied by the caller; the core performs no allocation.

The platform must consult this overlay before its read cache. Physical writes
and flushes bypass the overlay. `ReadMetadataView` may expose a verified,
read-only cache image whose lifetime ends at the next block I/O. Mutators use
owned edit images with savepoints, never a platform's read-only view. Cached
checksums are invalidated on writes and media changes. Known field changes
update checksums incrementally; initial loads and recovery still validate the
whole block.

## Strict policy

The optional transaction buffer groups metadata and overwritten data sectors
within one core mutation. It holds up to 32 complete blocks in caller-owned
memory. Repeated writes to the same block replace its staged image; reads
observe that image immediately. The platform must consult the staging buffer
before its physical read cache. Physical writes always bypass staging.

Each original target image is logged once using the existing `CSUN` undo
format. Newly allocated data can be written directly because the old bitmap
and extent records still make those blocks unreachable after rollback.

A batch is published in this order:

1. Write all new undo images and chain links, then flush them.
2. Write the transaction marker pointing to the complete chain, then flush it.
3. Write the staged target sectors.

Completion flushes all target sectors before writing and flushing the clean
marker. An operation that fits in the buffer therefore needs four flushes,
independent of how many distinct metadata sectors it modifies. An empty dirty
marker is unnecessary before the first batch: until publication, only free
data sectors and unused journal sectors have been written.

If the buffer fills, its targets are applied after publication and its memory
slots are reused. The undo chain remains live across every drain. Later
changes to an already logged target retain its original before-image. The
whole operation therefore still recovers to the old state if its final clean
marker has not committed. A drain whose targets are all covered by the already
published chain needs no additional publication barriers.

Recovery first discards the staged view, including after an I/O error in a
running mount, and then reads and restores physical media through the existing
undo replay. It can be retried after another interruption. Callers that omit
the optional buffer retain the original per-target flush protocol.

The writer keeps geometry scalars and lookup hints in its per-operation work
record. The bitmap hint refers to the immutable undo image, including at most
one cached word from that image. It never substitutes the modified bitmap for
the original allocation state. Journal allocation advances monotonically until
its cursor wraps; after a wrap, candidates are checked against the complete
undo chain. Recovery resets these hints before reading media, and buffer drains
retain the original transaction's undo chain. None of these hints is needed
after reboot.

Strict mode does not defer successful mutations across operations. A
successful mutation is durable when its final flush
completes. The guarantees assume atomic block writes and a device that honors
flush barriers; torn sectors and a controller that violates flush ordering
are outside this model.

## Combining physical writes

`AmigaFileSystemWriteCombiner` provides optional caller-owned storage beneath
both durability policies. It owns up to 128 block images, replaces repeated
writes by block number, and drains physically adjacent blocks as contiguous
device requests. It never fills gaps or combines across a flush barrier.
New data, undo images and chain links can share requests before the first
barrier. Dirty markers, home blocks and clean markers remain separated by
the existing four barriers. The on-disk journal format is unchanged.

The platform must consult logical buffered writes, strict staging, then this
queue before reading physical storage. Capacity pressure drains writes without
adding a flush. Every platform Flush must drain the queue successfully before
issuing the device flush. A failed or partially completed drain retains all
images, including the completed prefix: live recovery can replace queued home
blocks with undo images, then retry. Discarding the queue on an I/O error would
lose acknowledged data. Destruction after a crash must not implicitly flush it.

At 512 bytes per block, a 128-block queue requires 69,408 bytes, including one
block of scratch space. Stable hash entries and an inverse slot map let sorting
permute the owned images in place. Adjacent runs can then transfer directly
from those images; a second full payload array is unnecessary. Sorting retains
every image, including after a partial transfer failure, and can repeat after
replacements or further insertions. Read-only queue views expire at the next
mutation, sort or discard; copy them before retaining or editing them. Initialization
and all operations are allocation-free. Embedders may omit it when memory is
scarce; their durability contract remains the same. The device layer must still
honor its DMA memory requirements, alignment, maximum transfer size and large
device offsets when draining a run.

The native handler limits bulk-data copying to half a queue per barrier
interval (32 KiB at 512-byte blocks). Later streaming writes can use their
caller buffers directly; overlapping queued ranges still replace the queued
images. Small writes remain eligible for combination with the journal. A
verified metadata image keeps its checksum result only until it is replaced.
The native read cache retains mount/recovery reads and can fetch an eight-block
window when its memory and transfer limit allow one request. It skips pending
writes when filling that cache and retries the requested block alone if
speculative neighbors fail. An already clean device needs no redundant flush;
a failed device flush remains pending even after the write queue has drained.
