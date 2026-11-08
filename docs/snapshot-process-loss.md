# Retained SQLite snapshot process-loss qualification

This bounded fixture qualifies abrupt process termination during selected-table
capture. It does not introduce automatic recovery, orphan cleanup, or a retention
policy, and it does not establish power-loss durability.

## Publication boundary

Capture prepares the SQLite image and its metadata in a uniquely named private
staging directory beside the requested bundle. The database is copied and verified,
its bytes are flushed and hashed, and metadata is written and flushed before the
whole directory moves to the final bundle path.

- A process killed before the directory move must not expose a partial bundle at
  the configured final path. Its private staging directory can remain because a
  killed process does not execute managed cleanup.
- A process killed after the move can leave a complete bundle even though its
  caller never received the returned metadata path. Reopening that final bundle
  must verify its bytes, configuration, selected-table scope and adapter contents.
- A retry uses a new staging directory. It must not reuse or delete a prior
  interrupted attempt's directory, and it must refuse to replace an existing final
  bundle.
- If another publisher wins the final path while capture is in progress, the
  loser must preserve the accepted bundle and clean only its own staging files.

A metadata-flushed staging orphan can already contain a complete valid snapshot.
An operator who deliberately opens that exact metadata path may verify and read
it. This is not the same as publishing the configured final bundle, and the tests
do not invent a rule that every orphan must be unreadable.

## Test evidence

`SnapshotProcessLossTests` controls real child processes at seven boundaries:

1. Private staging directory creation.
2. The first copied row inside the target transaction.
3. Completed table copy.
4. Metadata file creation, before its payload is written.
5. Completed metadata flush.
6. Immediately before final directory publication.
7. Immediately after final directory publication, before returning to the caller.

The parent waits for a durable readiness marker, terminates the child, and waits
for its process handles to close. These are process kills rather than managed
exceptions. Internal callbacks observe the ordinary capture implementation;
production callers continue using the unchanged public API.

The matrix covers rollback-journal and WAL-backed live sources. WAL fixtures keep
committed data outside the main database file at capture time. Each fixture has
selected-table indexes, lexical decimals, integer/text/null storage, and an
unrelated confidential table that must not enter a retained bundle. Checks cover
live-source preservation, private orphan directories, restart/retry, exact captured
rows and digests, scope verification, replay without the original source, and
competing publishers.

## Remaining gates

File flushes and directory rename do not qualify power failure or machine/kernel
crashes. This fixture does not prove atomic visibility to arbitrary simultaneous
filesystem readers, resist adversarial filesystem replacement, or qualify every
native SQLite interruption point or filesystem provider. Existing snapshot
integrity, cancellation, resource and publication tests remain required.

Orphan retention and cleanup, encryption, other source providers, installed Windows
acceptance, authorized real IFRS artifacts and regulatory qualification remain
open. Hosted Windows/Linux CI qualifies these synthetic process-termination cases
only; it does not close those deployment and owner decisions.
