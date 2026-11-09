# Bounded disk-partitioned joins

The second P1-02 increment supports two streamed SQLite/CSV inputs whose join
and downstream grouping share one key field. It does not qualify arbitrary join
plans, within-key spill, crash recovery or production filing acceptance.

## Admitted graph

The validated, function-expanded graph must contain exactly two `CollectRows`
nodes with distinct bindings, one `Join`, one `GroupBy` and one `FactConstruct`.
Both collections feed the join directly; joined `rows` feed the group's `items`.
`leftKey`, `rightKey` and the single `GroupBy.keys` field must be identical and
retained by both collections. Group `values` feed the sink's collection-shaped
`value` or `values` input, with no simultaneous value fallback. Group keys may
feed sink aspects. Scalar text fallbacks are not admitted.

Other nodes may only be constants, run parameters and instant/duration builders.
There must be exactly two SQLite or CSV bindings (including a mixed pair), and
no project-configured destination. Different key names, grouping by a different
field or composite key, additional reducers/transforms/sinks, self-joins and
other set graphs remain fail-closed. The bounded small/preview path is unchanged.

## Semantics and evidence

The private SQLite fact spool also stores key partitions and both source sides.
Keys use ordinal UTF-16 byte identities; no managed dictionary retains all keys.
Within a key, source order is preserved. The normal engine executes both complete
collections, the join and group, retaining existing decimal, missing/nil/zero,
field-conflict and field-local source-path semantics. Even partitions that emit
no rows execute collection validation, including unused retained numeric fields.

All existing cardinalities (Inner/Left/Right/Full), right duplicate policies
(All/First/Fail), many-to-many policies (Allow/Fail), and overlapping-field policies
(Left/Right/Fail) are admitted. Missing, nil, empty and whitespace keys never
match. Partition ordering reproduces the engine's first-seen group order: emitted
left rows first, unmatched keyed right rows next, unmatched invalid-key right
rows last. Right duplicates suppressed by First stay suppressed.

SQLite canonical source digests are recorded independently from unprojected
rows for each binding. CSV file identity and retained SQLite snapshot checks
remain unchanged. Fact source paths include the originating CollectRows, Join,
GroupBy and FactConstruct nodes. They are not a claim of complete provenance.

## Resource and failure contract

- `MaxJoinExpansionRows` bounds the complete join across all partitions. Global
  duplicate/many-to-many policy and expansion checks run before fact execution.
- `MaxGroupCount` and output `MaxCollectionItems` bound groups with joined rows.
- Each key must fit `MaxCollectionItems` original rows per side and
  `MaxBatchBytes` combined original/serialized input bytes. First does not remove
  this input bound: discarded right duplicates still require validation.
- Before Cartesian expansion, duplicated input payload estimates are checked
  against `MaxGroupWorkingBytes`. The engine also applies its existing group
  key/provenance estimate. These estimates are not peak-process-memory limits.
- Serialized rows/facts retain `MaxRowBytes`/`MaxFactBytes`; both sources' digest
  rows, join rows, partition keys, facts and indexes share `MaxSpoolBytes`.
  Logical payload checks precede insertion. SQLite's `max_page_count` is derived
  from `MaxSpoolBytes`, so the private spool database cannot grow past that byte
  limit during row or index writes. This caps the main database file, not any
  SQLite temporary sort storage or total process disk use.
- Cancellation is polled during input, partitioning, preflight, loading and engine
  execution. Native SQLite index creation is synchronous and observes cancellation
  after it returns. Failures/cancellation discard the private spool and skip
  worker validation/publication; a later run starts from verified inputs.
- Bounded node-output samples span the run, but per-partition collection positions
  restart and collection sample ordering is partition-local. Original source
  locators, not these sample positions, identify source occurrences.

Explicit file publication retains project/profile approval, verification of both
source identities and exact staged-artifact validation. Unsupported adapters,
configured destinations and broader graphs are not enabled by this change.

## Verification

`PartitionedJoinExecutionTests.cs` compares small and large report bytes, fact
order, source digests and exact source transformation paths across SQLite/CSV
cardinalities and policies. It also covers invalid keys, retained unused numeric
validation, global and per-key limits, cancellation/cleanup/restart, unsupported
shapes and publication identity/validation gates.

`JoinHundredThousandRows_PartitionsWithBoundedMemoryAndExactTotals` reads 50,000
CSV rows per side, joins 10,000 interleaved keys into 250,000 rows, and emits
10,000 groups. It checks every total, entity and contributor locator against an
independent oracle and asserts less than 64 MiB retained managed-heap variation.
CI runs it in a fresh host on Windows and Linux. This is component evidence;
Arelle process-tree memory, native allocation peaks, within-key spill, durable
recovery, real IFRS qualification and jurisdiction-specific acceptance remain
separate open gates.
