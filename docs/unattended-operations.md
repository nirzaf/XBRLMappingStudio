# Unattended operation (OPS08)

## Prepare a service identity

Use a dedicated least-privilege OS account with a persistent home/local application-data directory. Install and enable the required filing profile in that account's catalog, pin the project to its identity/digest, and approve the saved semantic revision before scheduling. Revalidation does not grant publication approval. Synthetic profiles require `--allow-synthetic-profile` explicitly and are only development fixtures, not regulator filing authority. Never add that flag to bypass a missing production profile.

Use absolute paths for the executable, project, inputs, workspace, destination and manifest. Grant read access to approved projects, profiles and frozen input snapshots, and write access only to the workspace, output and operational state. Ensure the scheduler account sees the same files and installed Python/Arelle environment as your acceptance run. Do not put credentials in command-line parameters or logs.

Run `validate` and a supervised `preview` first. Verify publication with the exact scheduler account and saved approval before enabling a schedule. Preview success is not authorization to publish.

## Exit and diagnostic contract

| Code | Meaning | Operator response |
| --- | --- | --- |
| 0 | Success | Retain manifest/receipts; no retry. |
| 1 | Success with warnings | Treat as completed; review warnings, do not automatically retry. |
| 2 | Invalid mapping | Correct and reapprove relevant changes. |
| 3 | Data error | Correct input or reconcile an incomplete publication. |
| 4 | Incomplete configuration/profile or invalid CLI argument | Fix arguments, installed profile or worker setup. |
| 5 | Authorization failure | Restore authorized access or obtain project approval. |
| 6 | Cancelled | Inspect publication evidence before retry. |
| 7 | Operational failure | Investigate storage/worker/state and reconcile before retry. |

Standard output is for command results, including JSON. Standard error contains diagnostics. Capture them separately and preserve the original exit code. Unhandled argument failures now emit `CLI_INVALID_ARGUMENT`; unexpected failures emit `CLI_OPERATIONAL_FAILURE` without a raw runtime crash/stack dump. Existing domain diagnostics remain authoritative. Error messages, paths, manifests and support bundles may contain sensitive business context: restrict access and apply your retention policy. The support-bundle command reads at most the last 256 KiB of a supplied log, retains at most 200 lines and 64 KiB of log text, and replaces any individual line over 8 KiB with an omission marker before emitting a bundle. The bundle builder also consumes at most 4,096 caller-supplied log lines and adds an omission marker when that input bound is reached. Journal lock tokens are redacted from the exported receipt summary.

`execute --manifest <path>` writes the completed run manifest when a job result is available. Early configuration failures and forced termination may leave no manifest. Use a new manifest/log name per invocation; do not mistake a previous manifest for the current run's result. Capture stderr even when requesting a manifest. A nonzero code or missing manifest does **not** prove no output committed.

## Linux systemd timer

Example `/etc/systemd/system/xbrl-revenue.service` (adjust all paths and account):

```ini
[Unit]
Description=Publish approved XBRL revenue mapping

[Service]
Type=oneshot
User=xbrl-runner
WorkingDirectory=/srv/xbrl
ExecStart=/opt/xbrl/xbrl-studio execute /srv/xbrl/revenue.xmap --output /srv/xbrl/out/revenue.json --workspace-root /srv/xbrl/work --manifest /srv/xbrl/logs/latest-manifest.json
SuccessExitStatus=1
Restart=no
TimeoutStartSec=30min
TimeoutStopSec=2min
KillSignal=SIGTERM
KillMode=mixed
StandardOutput=journal
StandardError=journal
```

`KillMode=mixed` sends the graceful stop to the CLI first, allowing it to cancel and dispose its worker children before systemd kills the entire group at the deadline. Size deadlines from measured workloads. This example overwrites `latest-manifest.json`: archive or remove the previous file before each invocation in a controlled wrapper, or generate unique paths there. Journal logs identify individual service invocations.

```ini
# /etc/systemd/system/xbrl-revenue.timer
[Unit]
Description=Daily approved revenue mapping
[Timer]
OnCalendar=*-*-* 02:00:00
Persistent=true
Unit=xbrl-revenue.service
[Install]
WantedBy=timers.target
```

Enable with `systemctl enable --now xbrl-revenue.timer` after acceptance. Check `systemctl status xbrl-revenue.service` and `journalctl -u xbrl-revenue.service`. A timer does not start another instance while the same service is active. Do not schedule a second service against the same output. `Persistent=true` catches up missed runs after downtime; disable it if a late run would be inappropriate. Do not configure automatic restart loops for publication failures.

## Windows Task Scheduler

Publish/install the CLI on the host and configure an action using its absolute executable path, quoted absolute project/output/workspace paths, and a writable “Start in” directory. Select the dedicated account, “Run whether user is logged on or not” only after confirming its profile/catalog and network permissions, and “Do not start a new instance” for overlap policy. Network mapped drives from an interactive login are not reliable for a service account; use authorized absolute/UNC paths.

A PowerShell wrapper can capture results and make unique manifests:

```powershell
$stamp = [DateTime]::UtcNow.ToString('yyyyMMddTHHmmssfff') + '-' + [Guid]::NewGuid().ToString('N')
$base = "C:\Xbrl\Logs\$stamp"
& 'C:\Xbrl\Bin\xbrl-studio.exe' execute 'C:\Xbrl\revenue.xmap' `
  --output 'C:\Xbrl\Out\revenue.json' --workspace-root 'C:\Xbrl\Work' `
  --manifest "$base.manifest.json" 1> "$base.stdout.log" 2> "$base.stderr.log"
$code = $LASTEXITCODE
exit $code
```

Create the log directory first. Invoke the wrapper with the approved local PowerShell executable and `-NoProfile -File <absolute-script-path>`. Do not weaken execution policy to make it work. Task Scheduler reports code 1 for successful jobs with warnings; document this for monitoring rather than retrying it. Windows console Ctrl+C is cooperative; Task Scheduler “End”/forced process termination is not guaranteed to deliver Ctrl+C. Treat forced stops as crash recovery.

## Cancellation and recovery

Ctrl+C and Unix SIGTERM request cooperative cancellation. The CLI emits `CLI_STOP_REQUESTED`, passes cancellation to execution/publication and worker operations, then allows cleanup. A cancellation exception produces code 6 and `CLI_CANCELLED`. A completed commit can still return its actual success outcome if a stop arrived too late. Synchronous work can take time to reach cancellation; SIGKILL, machine failure and Windows forced termination cannot be handled cooperatively.

There is no general “resume execution from the last row” command. Publication journals/receipts support evidence-based recovery, not automatic replay of arbitrary inputs.

1. Stop overlapping schedules; preserve inputs, saved project revision, approval/profile identity, logs, manifest, destination and operational state. Record the run ID and, for combined file + SQLite publication, the mixed operation key from the manifest/diagnostics.
2. Under the **same OS identity**, run `xbrl-studio inspect-run-result <runId>` to inspect the file-publication journal. Code 0 here means the entry was found, not that publication succeeded: inspect its state/digests. The current JSON encodes `State` numerically: 0 Staged, 1 Verified, 2 Replaced, 3 Finalized, 4 Aborted, 5 Validated. Only finalized evidence confirms the journal completed; compare actual destination evidence when recovering. Missing entries return 3 and do not prove that nothing committed. To adjudicate interrupted atomic JSON/XML report writes, explicitly run `xbrl-studio recover-file-publications`; it only finalizes matching durable evidence, never republishes, and returns 7 if any entry remains uncertain. It scans unresolved entries in the current user's local application database and does not run automatically at startup.
3. For mixed file + SQLite publication, run `xbrl-studio reconcile-publication <mixed-operation-key>`. This checks file digest and SQLite receipt evidence and updates local reconciliation state. It does not rerun the mapping or publish missing outputs. Code 0 means both destinations confirmed; 7 means unresolved/pending/unknown, and 3 means absent/rejected/conflicting or otherwise incomplete evidence. Read the per-destination outcomes.
4. Investigate unknown/conflicting outcomes before a retry. Restore availability rather than deleting receipts, staging files or lock files to force progress. Combined destinations are not an atomic distributed transaction; one can commit while the other fails. Reconcile before any manual repair.
5. Retry only after confirming the intended operation and preserving replayable inputs/parameters. The existing mixed-publication identity and receipt checks protect matching retries; changing inputs, project semantics, output path or parameters may create a different intent and must not be assumed idempotent. Non-replayable inputs require operator review.
6. Collect `support-bundle <project.xmap> <runId> --out <bundle-path> --log <stderr-log>` for diagnosis when needed. Review/redact before sharing externally.

Default journal, receipts and mixed publication state live in `XBRLMappingStudio/state.db` under the account's .NET LocalApplicationData folder. Retain that directory with the account's installed profile catalog. Back up SQLite with a consistent SQLite-aware backup, or stop every application using it before copying database files (including any WAL/SHM sidecars). Do not copy only a live main database or edit/delete receipt rows. A transient container home loses recovery evidence; mount persistent state and preserve the service identity.
