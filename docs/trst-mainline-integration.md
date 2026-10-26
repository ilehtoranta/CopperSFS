# TRST mainline integration — 27 October 2026

The core now includes original-format TRST journaling, bounded staging,
copy-on-write payload allocation and whole-Write atomicity. Recovery still
accepts existing CSUN volumes. Buffered groups acknowledge complete Writes
before durability; Flush, Close, Inhibit and timer commit are durability points
in the native handler. The native adapter is maintained in CopperStart.

This source integration preserves mainline's clean-data cache and recycling
changes. Root selection now carries the recycling flag, including across
buffered group operations using fresh Work records. The group's existing
policy word stores case sensitivity and recycling separately; no additional
arena allocation is required. The native staging budget remains 192 KiB.

Validation of the merged core: 774 host tests passed and one optional
external-image test skipped. Two additional integration cases passed for
successive writes to a recycled file, fresh Work records, both case policies,
durable remount, exact payload and recycled-block accounting.

The preceding qualified native implementation is documented in
[the original-handler study](native-trst-original-study.md). Its measurements
and crash evidence identify that build, not a newly compiled mainline merge.
The native merge is qualified separately before promotion. No package release
or change to the previously distributed binary is implied by this source commit.

The retained local evidence is under `artifacts/publish-trst/`; ROMs, reference
handler binaries, disk images and generated artifacts are excluded from Git.
