# Mixed publication receipt revalidation

A mixed publication consists of an independently published xBRL-JSON or xBRL-XML
file and a SQLite transaction. It is not an atomic transaction across both targets.
The local reconciliation journal records each leg separately.

## Historical confirmation is not current evidence

A retry no longer returns success solely because its SQLite leg was previously
recorded as `Confirmed`. Before invoking either publication delegate, the
coordinator reopens the target read-only and checks the durable receipt against
the prepared operation key, intent fingerprint, and any known original row count.
The file digest is still inspected independently, including when the SQLite check
fails.

If a previously confirmed receipt cannot be verified, the SQLite leg becomes
`Unknown` with `PUB006`. The original prepared identifiers and expected row count
are retained. This state remains protected after reopening the local journal and
after subsequent missing-target, missing-receipt, parsing, I/O, or cancellation
failures. A later retry cannot silently downgrade it to “not committed” and invoke
a new write. Both automatic retry checks and the existing explicit
`reconcile-publication` command use the same receipt contract.

A valid matching receipt can restore confirmation without writing to either
target. Legacy local records with an unknown row count can learn that count from
the valid target receipt. A known count is never replaced by a different count
merely because the operation key still exists.

Never-confirmed attempts retain the existing transaction-retry behavior: an
available, intact target without the transaction's receipt can be reported as
`PUB_NOT_COMMITTED`; a missing target remains uncertain. Contradictory or malformed
receipt evidence is protected rather than treated as an absent receipt.

## Receipt parsing contract

Receipt reconciliation uses a single SQLite read snapshot and does not create a
missing target or receipt table. It checks exact operation-key and fingerprint
values, one matching receipt, an actual SQLite INTEGER row count in the range
1 through `Int32.MaxValue`, and a valid text completion timestamp. Text/real/blob
row-count coercions, null fields, overflow, duplicate matching receipts and
malformed receipt schemas cannot produce confirmation.

## Verification and limits

The adapter tests exercise real malformed SQLite receipts and read-only behavior.
The coordinator tests cover both report formats, unchanged confirmed retries,
missing and contradictory targets, sticky failure sequences across restarted
objects, original count preservation, independent file evidence, and recovery
when valid evidence returns. CLI tests use approved synthetic projects and fresh
CLI processes to verify failed retries do not create a new database receipt or
claim successful publication, then recover after the original receipt is restored.

This is durable receipt verification, not an audit of current fact-table contents
or physical database identity. A copied database carrying the exact valid original
receipt is not distinguishable with the current evidence model. External writers
can also change either target after a read check. No new automatic data restoration,
rollback, approval, or publication authorization is introduced.

Caller-requested cancellation during explicit read-only reconciliation propagates
as cancellation and leaves the stored mixed outcome unchanged. A later publication
attempt still revalidates any prior SQLite confirmation before invoking either
destination writer. Operational lookup failures continue to preserve an unknown or
conflicting outcome rather than authorize an automatic retry.

Full file-adapter journaling, exhaustive interrupted/ambiguous publication recovery,
retention policy, installed-platform qualification, real IFRS/profile acceptance,
and other release gates remain open.
