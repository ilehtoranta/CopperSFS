# Batched undo transactions

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

This does not defer successful mutations across DOS packets or weaken their
commit boundary. A successful mutation is durable when its final flush
completes. The guarantees assume atomic block writes and a device that honors
flush barriers; torn sectors and a controller that violates flush ordering
are outside this model.
