# Native runtime control protocol

CopperSFSCtrl uses a serialized private filesystem packet on the mounted
handler port. The CLI first verifies an INFO response before making changes
or sending standard ACTION_FLUSH. Foreign handlers are rejected.

Action is `0x43534643`, arg1 is `0x43534631`, arg2 is a pointer to a 64-byte
record and arg3 is 64. Fields are big-endian ULONGs. Version is 1. Callers
zero reserved fields. Packet results follow DOS boolean/secondary-error rules.

| Offset | Field |
| ---: | --- |
| 0 | Version (1) |
| 4 | Operation: INFO=0, DATACACHE=1, WRITEMODE=2 |
| 8 | Value: cache bytes, or STRICT=0 / BUFFERED=1 |
| 12 | Reserved (zero) |
| 16 | Requested data bytes |
| 20 | Actual data bytes |
| 24 | Cache pool allocation bytes |
| 28 | Active write mode |
| 32 | Pending buffered writes (boolean) |
| 36 | Filesystem block bytes |
| 40 | Metadata buffer count |
| 44 | Logical write-buffer allocation bytes |
| 48 | Physical phase-queue allocation bytes |
| 52 | Recovery pending (boolean) |
| 56 | Response identity (`0x43534631`) |
| 60 | Reserved (zero) |

INFO and cache resizing do not issue disk I/O. Cache replacement allocates
and initializes a new clean pool before publishing it and releasing the old
pool. Failure preserves the old configuration and pending writes.

Mode changes allocate required resources first, checkpoint with the existing
write engine, then publish the new mode. Failure retains the previous mode.
An already active mode is a no-op. FLUSH is the standard filesystem packet
and must have its result checked. Settings last for this mount only; the
CLI does not write configuration files or on-disk filesystem options.

The portable core supplies protocol constants; native handler integration
and platform resource management are separate from that core.
