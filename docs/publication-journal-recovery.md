# Atomic publication journal recovery

This increment hardens the shared `RunJournal.RecoverUnfinalized` API used by the
atomic report publisher. It adjudicates interrupted writes; it does not authorize,
retry, or roll back a publication.

## Confirmation contract

- Acquire the destination's advisory lock without waiting. A busy destination
  produces `RetryNeeded`; unrelated entries can still be checked.
- Reload the journal after acquisition. A newer terminal entry is retained, and
  a removed or repointed entry is not confirmed using an old snapshot.
- A `Validated` entry needs both the original publication lock token and a
  validated digest equal to its staged digest. Validation recorded before lock
  acquisition is insufficient, even when an older destination has identical bytes.
- A `Replaced` entry is already post-replacement evidence. Legacy rows without a
  validated digest remain supported, but any present validated or final digest
  must agree with the staged digest.
- The destination must exist and independently hash to the staged digest. Missing,
  changed, unreadable, or inconsistent evidence remains unconfirmed.
- Serialize finalization and backup cleanup with cooperating `RunJournal.Save`
  callers using a per-run writer guard. Confirm the exact journal row has not
  changed; SQLite updates the row and receipt in one conditional transaction.
- Persist final journal state and its receipt before deleting the prior-file
  backup. Preserve the original publication token; a recovery lock does not
  retrospectively authorize a write.
- Treat persisted run IDs as data during backup cleanup. Recovery never deletes
  staging files, which a retry may be preparing before it obtains the destination
  lock.

Malformed or unreadable individual entries do not prevent recovery of independent
entries. Failure to enumerate/open the journal store itself still surfaces to the
caller. Acquisition failures release any opened lock handle.

The normal publisher also retains the previous accepted file if final receipt
persistence fails after replacement. The destination may already contain the new
bytes in this case; the caller must not interpret an exception as proof that no
publication occurred.

## Evidence

`JournalCrashRecoveryTests` terminates actual child processes at three boundaries:
waiting for the destination lock, after locked validation but before replacement,
and immediately after atomic replacement but before its journal update. Each
boundary is tested with JSON and XML payloads and an absent, different, or identical
prior destination. Fresh recovery objects check destination bytes, journal state,
historical tokens, backup retention, receipt counts, and repeated-call idempotence.

`JournalRecoverySafetyTests` exercises both SQLite and legacy JSON journals,
including invalid evidence, active writers, unreadable files, malformed rows,
changed journal snapshots, receipt persistence failures, and safe backup paths.
A database-trigger fault also checks normal publication's receipt-before-cleanup
ordering. Existing atomic and mixed-publication process-loss tests remain required.

## Remaining qualification

- No startup or CLI recovery command is added. The shared recovery API has no
  production entry point in this increment; its callers remain explicit.
- CSV, XLSX, record-JSON, and record-XML file adapters use a separate compensating
  publication path. Durable adapter journals, prior-run backup discovery, and
  exhaustive recovery across all destinations remain open under P1-13.
- These JSON/XML payloads are synthetic byte-publication fixtures, not real IFRS
  validation, regulator acceptance, or full adapter qualification.
- Already-finalized entries are skipped. A crash after the receipt commits but
  before backup deletion can retain a backup; retention cleanup is not implemented.
- The advisory lock coordinates cooperating publishers, not arbitrary external
  file writers. Process termination tests do not establish filesystem power-loss
  durability, installed Windows recovery, or deployment-specific recovery policy.

No approval, rights, profile, installer, or release gate is relaxed. Exact-commit
Windows/Linux and Arelle CI remain required before integration is considered green.
