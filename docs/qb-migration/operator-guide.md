# QuickBase → PowerBase migration: operator commands

Run on the migration worker VM (`vm-pbqbmig-worker`, resource group `rg-pb-qbmigration`), in `~/qbm`, after `source ~/qbm.env`
(it only points the worker at the migration Key Vault; every secret comes from the vault). Every command takes `--tenant <id>`.

```bash
cd ~/qbm && source ~/qbm.env
W=./PowerBase.QbMigration.Worker
```

## Start, pause, resume, stop

| What | Command |
|---|---|
| Start a new app | `$W migrate --tenant 17 --qb-app <dbid> --pb-app <id> --run-as <userId>` |
| Continue a run (after stop, failure or fix) | `$W migrate --tenant 17 --run <id>` |
| Pause | `$W pause --tenant 17 --run <id>` |
| Resume | `$W resume --tenant 17 --run <id>` |
| Stop | `$W stop --tenant 17 --run <id>` |
| Progress | `$W status --tenant 17 --run <id>` |

- `migrate` runs every step in order: map (new run only) → plan → load → finalize → validate → report. Running it again
  continues the same run where it ended: finished chunks are skipped.
- Pause and stop take effect between chunks: the chunks being written finish first (each chunk is one transaction, so
  nothing is half-written). A paused worker waits; a stopped worker exits, and its report is still written.
- Only one worker can work on a run. Starting the same run twice is refused with "already being worked on".
- `migrate` for an app that already has a run continues that run; add `--new` to start a fresh run instead.
- Long runs: start with `nohup $W migrate ... > ~/migrate-<app>.log 2>&1 &` so closing the session doesn't stop it.

Exit codes: 0 all gates passed · 2 finished with problems (failed chunks or gates; see the report) · 4 stopped by an operator ·
1 wrong arguments or the run is busy · 3 cancelled (Ctrl+C / service stop).

## Dead letters (chunks the queue gave up on)

| What | Command |
|---|---|
| List them with their errors | `$W retry-dlq --tenant 17 --run <id> --list` |
| Retry them and finish the run | `$W retry-dlq --tenant 17 --run <id>` |
| Retry only some chunks | `$W retry-dlq --tenant 17 --run <id> --chunk 176,180` |
| Only put them back (load later) | `$W retry-dlq --tenant 17 --run <id> --no-continue` |

Fix the cause first (the list shows the error): a chunk that fails for the same reason is dead-lettered again. A message
for a chunk that loaded since is a leftover; `--no-continue` removes it.

## Report

`$W report --tenant 17 --run <id> [--out <folder or file.xlsx>]` (default folder: `./reports`). `migrate` writes it
automatically at the end, also after a stop. It covers every run of that app. Sheets: Summary, Tables, Not Migrated,
Broken Links, Fields, Chunks, Issues, Users, Dead Letters.

Every report is also uploaded to Azure: **Portal → rg-pb-qbmigration → storage account `stpbqbmig…` → Containers →
`reports` → click the file → Download** (no SSH needed). The upload uses the vault secret
`Migration--ReportStorage--ConnectionString`; without it the report stays on the VM only and the log says so. A copy
always stays on the VM as well (`scp -i ~/.ssh/qbmig_ed25519 qbmadmin@<vm-ip>:reports/<file>.xlsx .`).

The report contains user emails: keep it out of git and share it only inside the team.
