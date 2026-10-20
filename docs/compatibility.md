# Compatibility and recovery

CopperSFS targets classic `SFS\0` version-3 volumes. The core reads object
containers and nodes, directory chains, bitmaps and extent trees, and supports
file and directory mutation and metadata updates. It validates original SFS
`TRST` redo records on mount and replays complete records before exposing
the volume.

Its own mutations use `CSUN` undo records, explicit flush barriers and a
commit marker. This journal is private to CopperSFS. An interrupted CopperSFS
write needs CopperSFS recovery; the original SFS handler should not be
expected to interpret `CSUN`. A damaged journal remains dirty rather than
being silently accepted. The journal reserves free blocks temporarily, so a
mutation can report a full disk before every free data block is consumed.

An optional bounded staging buffer batches undo publication and target writes
within each mutation. It retains the same on-disk journal and operation commit
boundary, including when an operation exceeds the buffer capacity. See
[transaction batching](transaction-batching.md) for the durability ordering and
the platform's read-through contract.

The core assumes the block device preserves completed block writes across
flush barriers. The external `L:` handler has passed a Kickstart 3.1 comparison
against original SmartFilesystem 1.279, including clean handoff and an
unsampled write/flush interruption sweep; see
[validation](validation.md) for the exact boundary. There is no formatter or
RDB autoboot installer here.
