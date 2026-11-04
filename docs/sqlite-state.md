# Local SQLite state and connector

## Application database

The application creates `${LocalApplicationData}/XBRLMappingStudio/state.db` on first use. The version is stored in `PRAGMA user_version`; migration 1 creates `app_settings`, `project_registry`, `run_journal`, `publication_receipts`, and `suggestion_audit`. Foreign keys are enabled on every connection. WAL mode is enabled for the local single-workspace deployment, with a five-second SQLite busy timeout and connection pooling disabled. Schema changes run in an explicit transaction. A database with a newer schema is refused without changing its contents.

`.xmap` remains the portable source of truth for executable graphs. SQLite stores local operational state and metadata, not graph definitions or secrets. The run journal imports legacy JSON journal records once without deleting the old files. Finalized run entries and publication receipts are committed together. `LocalApplicationDatabase.IntegrityCheck()` exposes SQLite's integrity check. Unix app-data directories are set to owner-only access and database files to owner read/write; Windows inherits the user's application-data ACL.

`BackupTo(path)` uses SQLite's online backup API for a consistent snapshot and refuses to overwrite a prior backup. Keep backups outside the live application directory. To restore, exit the application, preserve the current database and its sidecars, then copy the verified backup into place. No schema migration in version 1 is destructive; future destructive migrations must first create and verify a backup.

## SQLite data source and target

The adapter opens source databases read-only. It discovers user tables, inspects columns, samples rows, and streams rows with cell lineage. Run manifests fingerprint the logical rows consumed, avoiding a raw database-file hash that can be blocked by Windows file sharing or omit active WAL contents. Table names are quoted as identifiers. Numeric values declared as binary floating point are rejected; exact financial decimals should be stored as invariant text and listed in the binding's `decimalColumns` configuration.

`SqliteTargetPublisher` writes a batch in one immediate transaction using prepared values and a configured business key. It requires that key to match a primary key or unique index. The target database contains `xms_idempotency_receipts`; a replay with the same operation key and effective input intent returns the stored receipt, while changed intent returns `PUB006`. Row writes and the receipt commit together. Cancellation, constraint failures, and process exceptions roll back the transaction. This publisher is currently an adapter API; it is not yet wired as a general database destination in the CLI or desktop mapping lifecycle.

## Scope

This is a local SQLite implementation, not qualification of SQL Server, PostgreSQL, MySQL/MariaDB, or Oracle. Taxonomy FTS5 indexing, automated retention/cleanup, cross-process writer stress qualification, and Windows ACL verification remain open. The app database currently uses only the run journal; the other versioned tables reserve persistence for state that is not yet wired into product workflows.
