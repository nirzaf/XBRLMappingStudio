# Retained SQLite source snapshots and replay

## Qualified scope

P1-06 now has an explicit SQLite capture/replay path in the shared application
service and CLI. Ordinary live SQLite reads are still non-replayable read
transactions. Selecting a retained snapshot is a separate, deliberate operation;
no live source is silently replaced or captured.

A snapshot bundle contains `source.sqlite` and `snapshot.json`. Capture uses
SQLite's incremental online backup API while holding one source read transaction,
so committed WAL contents are included and a concurrent WAL writer cannot change
the captured view. The source is opened read-only. The backup is closed in DELETE
journal mode and must have no WAL, SHM or rollback-journal sidecars. Replay opens
the image with SQLite's immutable, read-only mode so no journal can supply alternate
rows; database file symlinks/reparse points are refused. The selected
table is scanned through the existing exact-numeric, bounded-row adapter before
the bundle becomes visible.

**The backup contains the entire database, including tables outside the selected
binding.** Treat it as confidential source evidence. Do not commit or upload it,
include it in support bundles, or give it to another recipient without the
appropriate authorization. This implementation is not a table-only export.

Capture refuses databases larger than 1 GiB, checks cancellation between 128-page
backup steps and during validation/hashing, and bounds lock retries to five
seconds. Metadata is capped at 64 KiB. Staging uses a new private directory,
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
complete. Full runs in both the small-input and qualified large row-scoped paths
can replay the snapshot. The normal restrictions on large multiple-binding,
set-phase and saved-destination execution still apply.

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

For a full replayed run the manifest records:

- `SnapshotStrategy = sqlite-retained-backup-v1` and `IsReplayable = true`.
- `Digest`: the canonical selected-table content digest, equal to an equivalent
  live run's digest.
- `SnapshotDigest`: SHA-256 of the retained database file.
- `OriginalPath`: the actual retained database used for execution.
- `OriginalSourcePath`: the original live source location.
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

## Verification and remaining work

Storage tests cover WAL consistency under a committing writer, restart/move with
the live database deleted, exact source rows/lineage, cancellation cleanup,
no-overwrite behavior, unsupported binary numerics and changed/missing metadata,
bytes, binding/configuration or sidecars. Integration tests cover identical small
and large report/content digests after live-source deletion, preview restrictions,
mutation during worker validation, protected prior file/database outputs,
keyless retained publication, CLI capture/replay/retry, and approval refusal.

Still open: desktop capture/replay controls; table-only extracts; automated
retention/cleanup/encryption policy; multi-provider snapshots; process/power-loss
capture qualification; and owner-authorized real IFRS/Qatar filing acceptance.
This feature does not establish Q-Disclosure compatibility.

Local verification on 2026-11-08 after integration of the process-memory CI fix:
Release solution build passed with zero warnings/errors. Ordinary solution tests
passed 1,059 cases with six environment-gated Arelle skips; the two 100,000-row
SQLite/CSV memory cases each passed in a separate test process. Enabling the
pinned Arelle runtime passed all 15 Arelle integration cases. The new snapshot
coverage comprises 13 storage and 10 integration/CLI cases. A focused independent
review's symlink/WAL, metadata-overwrite and null-configuration findings were fixed
and covered by these tests. These are local Linux results; exact-commit hosted CI
is the Windows verification gate.
