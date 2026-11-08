# Retained SQLite source snapshots and replay

## Qualified scope

P1-06 now has an explicit SQLite capture/replay path in the shared application
service and CLI. Ordinary live SQLite reads are still non-replayable read
transactions. Selecting a retained snapshot is a separate, deliberate operation;
no live source is silently replaced or captured.

A new snapshot bundle contains `source.sqlite` and `snapshot.json`. Capture holds
one source read transaction while it copies only the configured table, its table
definition, and indexes attached to that table. Committed WAL contents visible at
the transaction boundary are included; a concurrent WAL writer cannot change the
captured view. The source is opened read-only. Views, triggers, other tables,
unrelated indexes, and database-level state are excluded. Virtual tables and
unsupported table shapes fail closed. The target is page-capped during extraction,
then scanned through the existing exact-numeric, bounded-row adapter before the
bundle becomes visible. The completed image is closed in DELETE journal mode and
must have no WAL, SHM or rollback-journal sidecars. Replay opens it with SQLite's
immutable, read-only mode so no journal can supply alternate rows; database file
symlinks/reparse points are refused.

Version 1 bundles captured before table-only extraction contain the full database
and remain readable for backward compatibility. Treat both legacy bundles and new
selected-table bundles as confidential source evidence. Do not commit or upload
them, include them in support bundles, or give them to another recipient without
the appropriate authorization. New captures no longer copy unselected tables.

Capture refuses selected-table images larger than 1 GiB, checks cancellation
while scanning/copying rows and during validation/hashing, and uses SQLite's
five-second default lock timeout. Metadata is capped at 64 KiB. Staging uses a new private directory,
owner-only Unix permissions and an owner-only Windows directory ACL. Completed
Unix files are owner-read-only. Existing bundle destinations are never replaced.
Files are flushed before a same-parent directory rename publishes the bundle.
Cancellation or a handled failure removes the staging directory. Abrupt process
loss can leave an unpublished `.xms-snapshot-*` directory; automatic cleanup and
power-loss/filesystem durability qualification remain open.

## CLI workflow

From a workspace containing an approved mapping and a configured SQLite source:

```text
xbrl-studio snapshot-source mapping.xmap --binding financial-source --out inputs/period-end.xms-snapshot
xbrl-studio execute mapping.xmap --source-snapshot financial-source=inputs/period-end.xms-snapshot/snapshot.json --output reports/result.json --manifest reports/run.json
```

Use the actual binding ID from the project. The snapshot's parent directory must
already exist. `--workspace-root` is available for both commands; source, snapshot
and output paths remain subject to the workspace boundary. A source binding
cannot combine `--binding id=path` and `--source-snapshot id=path` in the same run.
Other bindings retain their normal sources. The project is not rewritten, and its
approval/profile requirements are unchanged. Synthetic fixtures still require the
explicit development-only `--allow-synthetic-profile` option. Capturing data does
not qualify or approve a filing profile.

`preview` also accepts `--source-snapshot`; its manifest remains a partial,
non-publishable, non-replayable preview even though the retained source itself is
complete. Full runs in the small-input, qualified large row-scoped, bounded
partitioned-group and same-key two-source join paths can replay snapshots. The
normal graph, key/group, source-count and saved-destination limits still apply.

Keep the whole bundle together. It can be moved and reopened in a new process;
the database filename is fixed relative to its metadata, not taken from an
untrusted arbitrary path in the metadata. Replay needs neither the original live
database nor its current contents. Preserve the same project revision, parameters,
engine/function versions and qualified filing profile to reproduce the report;
a source snapshot alone is not a whole execution-environment archive.

## Evidence and fail-closed behavior

Capture metadata binds the version, source ID, adapter configuration digest,
schema fingerprint, original source path, byte length, SHA-256 digest and capture
time. Opening a bundle checks those fields against the selected binding and
rechecks its schema and exact database bytes. The database digest is also checked
before the mapping reads it, after execution/worker validation, and at publication
validation boundaries. Missing bytes, changed bytes/configuration, unsupported
versions, and journal-dependent snapshots fail closed.

New version 2 metadata records `strategy = sqlite-retained-table-v2` and
`scope = selected-table`. Existing version 1 metadata without those fields is
interpreted as `sqlite-retained-backup-v1` with full-database scope. For a full
replayed run the manifest records:

- `SnapshotStrategy = sqlite-retained-table-v2` for new captures and
  `sqlite-retained-backup-v1` for legacy full-database bundles; both set
  `IsReplayable = true`.
- `Digest`: the canonical selected-table content digest, equal to an equivalent
  live run's digest.
- `SnapshotDigest`: SHA-256 of the retained database file.
- `OriginalPath`: the actual retained database used for execution.
- `OriginalSourcePath`: the recorded original live source location. This unsigned
  metadata field is descriptive, not independently authenticated proof of origin.
- `SnapshotMetadataPath`: the capture metadata file, protected from destination overwrite.
- `RowIdentityStrategy`: declared-primary-key identity or positional identity.

Primary-key identities and cell provenance are preserved. A keyless table's
positions are scoped to this exact, unchanged database image, so retained-result
reuse is allowed without claiming stable identities across different captures.
The retained database, its metadata, and original source are protected from being used as
publication targets. The snapshot digest participates in SQLite/mixed-publication
intent identities, without changing existing live-source operation keys.

Integrity is not authenticity: an owner able to replace both metadata and data
can create a different valid bundle. This is not signed evidence, encrypted
storage, WORM storage, an adversarial filesystem boundary, or retention governance.
Do not edit snapshot files or add sidecars. A successful earlier run does not
exempt later publication from rechecking its recorded digest.

## Capture integrity checks

Stored SQLite schema text is untrusted input. Capture admits only one bounded
CREATE TABLE statement for the selected table and one CREATE INDEX statement for
each of its indexes. Quoted delimiters and SQL comments remain supported, but a
second statement or a different target is refused before executing schema text.
Schema-size checks precede managed schema materialization. Existing v2 metadata,
CLI/Desktop workflows and legacy-v1 replay behavior are unchanged.

The target preserves the source database encoding. Capture independently verifies
storage types and raw cell content, then verifies the exact ordered values read by
the adapter. Malformed text, affinity changes, injected rows or a different scan
order cannot silently produce a replayable bundle. Planner statistics are not
copied: if their absence changes the observed row order, capture fails closed.
Hidden SQL rowids are not independently promised; public identities remain declared
primary keys or exact captured scan positions. Validation holds the source read
transaction until these comparisons finish.

Direct retained-connection verification also checks selected-table scope using
immutable reads, including callers that construct a retained reference directly.
Rewriting metadata hashes does not authorize unrelated schema objects. These
checks provide integrity, not authentication of an owner-supplied bundle.

## Abrupt process termination

A [bounded process-loss matrix](snapshot-process-loss.md) terminates real capture
children at staging, row-copy, metadata and directory-publication boundaries for
rollback-journal and WAL sources. It checks final-path absence before publication,
complete replayable bundles after publication, retained private orphans, safe
retries, and preservation of a competing accepted bundle. The public capture API
and retained format are unchanged. Automatic orphan cleanup and power-loss
qualification are not added.

## Verification and remaining work

Storage tests cover WAL consistency under a committing writer, restart/move with
the live database deleted, exact source rows/lineage, cancellation cleanup,
no-overwrite behavior, unsupported binary numerics and changed/missing metadata,
bytes, binding/configuration or sidecars. Integration tests cover identical small
and large report/content digests after live-source deletion, preview restrictions,
mutation during worker validation, protected prior file/database outputs,
keyless retained publication, CLI capture/replay/retry, and approval refusal.
Additional regressions cover stored-DDL statement injection, malformed text,
UTF-16 and planner-dependent order, safe table/index layouts, and direct scope
verification. Grouped/joined replay tests compare complete facts, provenance,
input/report digests and late-publication tamper refusal. The isolated 100,000-row
capture fixture checks every row and locator, excludes an unrelated 8 MiB table,
and requires less than 64 MiB retained managed-heap variation. It does not qualify
peak native or process-tree memory.

Still open: automated retention/cleanup/encryption policy; multi-provider
snapshots; broader process/power-loss capture qualification; and owner-authorized
real-profile and jurisdiction-specific filing acceptance. This feature does not
establish regulator compatibility.

Historical verification of the earlier snapshot increment on 2026-11-08
(before the capture-integrity checks above):
Release solution build passed with zero warnings/errors. Ordinary solution tests
passed 1,059 cases with six environment-gated Arelle skips; the two 100,000-row
SQLite/CSV memory cases each passed in a separate test process. Enabling the
pinned Arelle runtime passed all 15 Arelle integration cases. That increment
comprised 13 storage and 10 integration/CLI cases. A focused independent
review's symlink/WAL, metadata-overwrite and null-configuration findings were fixed
and covered by these tests. These are local Linux results; exact-commit hosted CI
is the Windows verification gate.

Capture-integrity patch verification on 2026-11-08, based on `a4e897c`:
Release build passed with zero warnings/errors; 1,258 ordinary solution tests
passed with six environment-gated skips. The focused storage/intent selection
passed 83 cases, including 66 new integrity regressions. All five 100,000-row
memory workloads passed in separate test hosts, each verified to execute exactly
one test. Enabled Arelle passed 15/15, Python worker tests 82/82, operational tools
15/15, and the taxonomy benchmark self-test passed. The bundled SQLite 3.54 already
rejects malformed appended-schema payloads; explicit admission-parser tests also
exercise the defense independently of that native rejection. Those were cloud
worker results and were independently reviewed before the exact commit
`adc16778cbad862d19f84e8ea83a2be31999e3db` was cherry-picked onto `main` as
`e3c526a06e3838200b0e0f3d7c5b378755eb539c`. Windows Release verification on that
integrated head passed 66 integrity/intent tests, 15 existing snapshot-store tests
and all 3 table-snapshot integration cases, including the isolated 100,000-row
memory case. Exact-head GitHub Actions [42481277509](https://github.com/nirzaf/XBRLMappingStudio/actions/runs/42481277509)
passed all four jobs: repository hygiene, pinned Arelle integration, Ubuntu and
Windows. This is implementation verification, not release qualification. Native
desktop acceptance, broader process/power-loss capture qualification, retention governance,
multi-provider snapshots, and owner-authorized real-profile/regulator acceptance
remain open.
