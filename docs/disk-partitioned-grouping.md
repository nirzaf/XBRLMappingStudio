# Bounded disk-partitioned grouping

The first P1-02 increment supports large single-source grouping without retaining
all source rows or all group provenance in memory. It is not general disk-backed
set execution, a spill-resume protocol or production filing qualification.

## Admitted graph

The validated, function-expanded graph must contain exactly one `CollectRows`,
one directly connected `GroupBy`, and one `FactConstruct`. Group `values` must
connect directly to the sink's `values` port (or a collection-shaped legacy
`value` port); `keys` may also feed sink aspects. Simultaneously connected
`value` and `values` ports are not admitted.
Other nodes may only be constants, run parameters and instant/duration builders.
There must be one SQLite or CSV binding and no project-configured destination.
Row-dependent fields/transforms, joins, extra reducers, multiple sinks and other
set graphs remain unsupported with a structured configuration failure.

The existing small/preview path is unchanged. Explicit file publication retains
its normal approval, source-identity and exact-staged-artifact validation gates.

## Execution and identity

A bounded input probe selects large execution. The private fact-spool SQLite
connection also stores the group directory and source rows. Keys use the same
length-prefixed ordinal identity as in-memory `GroupBy`; separators cannot merge
otherwise distinct composite keys. Groups retain first-seen order; member rows
retain input order even when groups are interleaved. No managed dictionary of all
group keys is kept.

Every field retained by `CollectRows.fields` is serialized with its original
presence, decimal value/lexeme and cell source. This includes unused fields that
still need decimal-domain validation. Original SQLite input digests are computed
from unprojected rows. CSV file identity checks remain unchanged.

One complete group at a time is passed through the existing execution plan.
Consequently missing members prevent partial sums, all-nil groups remain nil,
nil members do not change numeric totals, zero remains present, and normal
FactConstruct validation still applies. Contributor paths retain source occurrence
identity and the CollectRows, GroupBy and FactConstruct node IDs. No claim is made
that the run's overall provenance status is complete.

Bounded node-output sample counts span the whole execution, but collection sample
order follows group processing and per-group item labels restart. These samples
are not global source-row coordinates; fact source locators remain authoritative.

## Limits and failure handling

- `MaxGroupCount` and output `MaxCollectionItems` bound the total group count.
- Each group must fit `MaxCollectionItems` member rows and `MaxBatchBytes` input
  bytes. Input bytes use at least the full original-row estimate, not only the
  serialized retained fields. A skewed oversized group fails with MAP014.
- Existing `MaxGroupWorkingBytes` bounds the engine's estimated key/provenance
  state for the group being evaluated. It is not a peak-process-memory bound.
- Serialized source rows and facts obey `MaxRowBytes` and `MaxFactBytes`.
- Group rows, group keys, source identity rows, facts and their indexes share
  `MaxSpoolBytes`. Logical payload checks occur before insertion; physical SQLite
  size checks occur at transaction commits and finalization, so a transient disk
  overshoot is possible. Report bytes retain the separate `MaxReportBytes` cap.
- Cancellation is polled while reading, partitioning, loading and executing groups.
  Failure or cancellation disposes the private spool, returns no large artifact
  and skips report validation/publication. A later run starts from verified inputs.

There is no within-group spill, disk-backed join, resumable checkpoint, automatic
orphan-spool cleanup after process death, retention policy or power-loss recovery
qualification. SQLite cache size is bounded, but allocator peaks, native working
set, disk-space exhaustion and Arelle process-tree memory remain separate gates.

## Verification

`PartitionedGroupExecutionTests.cs` covers SQLite/CSV byte-for-byte report parity,
original source-digest parity, first-seen order, exact decimal serialization,
missing/nil/zero outcomes, field-specific source paths, delimiter-safe composite
keys, unused-field validation, budget refusal, cancellation/cleanup/restart,
unsupported graph refusal and exact staged-publication acceptance/rejection.

The isolated `GroupHundredThousandRows_PartitionsWithBoundedMemoryAndExactTotals`
fixture partitions 100,000 interleaved SQLite rows into 10,000 groups, compares all
10,000 totals and entity identities to a separately authored arithmetic oracle,
checks contributor counts and enforces less than 64 MiB retained managed-heap
variation across partitioning and group execution samples. CI runs it in a fresh
process on Windows and Linux alongside the existing isolated CSV/SQLite cases.
This is synthetic component evidence, not real IFRS/Qatar or installed-product
acceptance.
