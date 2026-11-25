# UAT deployment: stored-column build speed (worker + migration 100)

Target: Container App `c-powerbase-compute` (rev 0000018, image tag `36b2b8d1e7e89d4e8c9dedeb88a9d8f457fac18a`, 4 CPU / 8 Gi),
tenant database `Powerbase_17` on `powerbase-server-uat`. Nothing below has been run against Azure.

## Rules
* The five modified `appsettings*.json` files hold LOCAL values (laptop connection string, `Compute:Enabled=true`, real keys). **Never stage them.**
* Pushing to `feature/PB-stored-computed-columns` runs BOTH workflows (`c-powerbase-api.yml` redeploys the C API App Service,
  `c-powerbase-worker.yml` pushes the worker image). The worker workflow only pushes the image; the Container App is switched by hand.
* Rollback tag (current image): `36b2b8d1e7e89d4e8c9dedeb88a9d8f457fac18a`. Always switch by the commit SHA tag, never `latest`.

## 0. Pre-flight (already done on the dev machine)
| Check | Result |
|---|---|
| Unit tests (all 13,466) | 13,463 pass, 2 skipped, 1 fail: `AppImportJobTests.Policy_OnlyFilesOverThreeMegabytesRunInBackground`, which fails identically on a clean copy of HEAD (not ours) |
| Compute unit tests (incl. 11 new) | 281 / 281 pass |
| Compute integration tests on LocalDB (incl. sliced builds, checkpoint resume, nightly check) | 37 / 37 pass |
| `dotnet publish` of the worker (the exact Dockerfile command), API publish, Migrator build, Release | 0 errors |
| Migration 100 | idempotent (re-run = no-op), additive, new table only; also packaged inside the API (`database/migrations/tenant`) |
| Docker image build | NOT run: Docker is not installed on this machine. The Dockerfile is unchanged; the CI job builds it. |
| Local values check | 17 stored columns identical (row count + checksum) between full-read and narrow-read builds |

## 1. Commit (your decision)
```powershell
cd C:\Ashapurasoftech\PowerBase-Platform-ParentDeleteGuard_Fix
git add src/PowerBase.Application/Compute/ComputeOptions.cs src/PowerBase.Infrastructure/Compute tests/PowerBase.IntegrationTests/Compute tests/PowerBase.UnitTests/Compute/BuildSpeedTests.cs database/migrations/tenant/100_create_compute_build_checkpoint.sql docs/compute-build-speed.md docs/compute-uat-deployment.md infra/scale-compute-worker.ps1
git status --short   # the 5 appsettings*.json files must still be listed as " M" (not staged)
```

## 2. Apply migration 100 to Powerbase_17 (BEFORE the new image runs; the old image ignores the table)
Do not use the `db-migrations.yml` "all tenants" run (it uses `AZURE_SQL_CONNECTION_STRING`, whose target is not known to be UAT, and tenants 14/15 have no database).
Run the Migrator for ONE tenant from a machine that can reach the UAT SQL server (add your IP to the server firewall if needed):
```powershell
$env:ConnectionStrings__DefaultConnection = '<UAT control DB (Powerbase) connection string>'
dotnet run --project src/PowerBase.Migrator -c Release -- migrate tenant --id 17
```
Expected last line: `[run] 100_create_compute_build_checkpoint.sql ... done` (only that script runs). Verify on Powerbase_17:
```sql
SELECT OBJECT_ID('meta.ComputeBuildCheckpoint');            -- not NULL
SELECT COUNT(*) FROM meta.ComputeBuildCheckpoint;           -- 0
```

## 3. Build the image
Push the branch (or run workflow "C - Build and deploy ComputeWorker to c-powerbase-compute" by hand). Note the pushed commit SHA
(the image tag is the full 40-character SHA). Confirm in the ACR: repository `powerbase-computeworker`, tag = that SHA.

## 4. Stage 1: new image, SAME environment variables (no concurrency change)
Portal > c-powerbase-compute > Containers > Properties > Image tag = `<new SHA>` > Save as a new revision.
Current env (unchanged in this stage): `Compute__Enabled=true`, `PauseBetweenBatchesMs=0`, `MaxSessionsPerDatabase=16`,
`BackgroundSessionsPerDatabase=15`, `BuildsPerReplica=8`, `SlicesPerField=4`, `SummaryBatchSize=5000`, `LookupBatchSize=5000`,
`GuardSlowPercent=80`, `GuardStopPercent=95`, `PriorityTables=17:243,17:244`.
Behaviour that is NEW under these same values: narrow reads, range splitting also for set-based lookups/summaries (tables >= 100,000 rows,
4 sessions per field, still capped by the 15 background slots), nightly check skipped while a backlog exists, checkpoints.

Watch for 30–60 minutes (Log Analytics `C-powerbase-logs`):
```kusto
ContainerAppConsoleLogs_CL
| where ContainerAppName_s == "c-powerbase-compute" and TimeGenerated > ago(1h)
| where Log_s has " built: " or Log_s has "resumes its build" or Log_s has "failed" or Log_s has "Compute reconciliation"
| project TimeGenerated, RevisionName_s, Log_s
| order by TimeGenerated desc
```
Pass criteria: `built:` lines appear with the plan type and session counts; no new `building field ... failed`; no `Invalid object name`;
fields Ready per hour clearly above 10–35; database CPU / IO in Query Performance Insight still well under 70 %.
Measure the same two numbers as the baseline: rows/second per field (baseline about 119) and fields Ready per hour.

Resume check (optional but recommended): in the Portal create another revision with the same image (or restart the active one) while a big
build runs; the next round must log `resumes its build: N of M range(s) left` and finish faster than a restart from zero.

Values check: with a backlog under about 100 fields, set `Compute__ReconcileWhileBuilding=true` for one revision: the nightly check then
re-computes fields built by the OLD code with the NEW narrow reads. `N difference(s)` must be 0. Any difference means a narrow-read
mismatch: set `Compute__NarrowReads=false` (see rollback) and tell me which field.

## 5. Stage 2 (only after Stage 1 passes): more throughput
The database was idle (CPU 5–25 %, IO about 0 %), the limit was the worker. Check the Container App metric CPU usage first:
* sustained near 4 cores: add REPLICAS (min replicas 2), not sessions (one replica is capped at 4 vCPU / 8 Gi);
* well below 4 cores: raise `Compute__MaxSessionsPerDatabase=32`, `Compute__BackgroundSessionsPerDatabase=31`, `Compute__BuildsPerReplica=12`.
Optionally `Compute__ProjectedBatchSize=1000`. Change one thing per revision and re-measure.
`infra/scale-compute-worker.ps1 -WhatIf` prints the autoscale commands (CPU rule + backlog rule that needs a `tenant-sql-conn` secret).
Review it before running; it also overwrites `BuildsPerReplica` (6) and `SliceMinRows` (50,000).

## 6. Rollback (cheapest first, none needs a database change)
| Symptom | Action |
|---|---|
| Wrong values / suspicion of narrow reads | `Compute__NarrowReads=false` (new revision). Fields built wrongly: `UPDATE meta.AppField SET MaterializeState = 0 WHERE Id IN (...) AND MaterializeMode = 1` is NOT enough (the version must change); use Migrator `materialize --id 17 off --field ID` then `on --field ID` |
| Too much load / deadlocks / splitting suspected | `Compute__SlicesPerField=1` (one range per field, checkpoint still works) and/or lower `MaxSessionsPerDatabase` / `BuildsPerReplica` |
| Nightly check wanted again during the backlog | `Compute__ReconcileWhileBuilding=true`, `Compute__ReconcileMinAgeHours=0` |
| Anything else wrong with the image | Portal > Containers > Image tag = `36b2b8d1e7e89d4e8c9dedeb88a9d8f457fac18a` > Save as a new revision. The old image ignores `meta.ComputeBuildCheckpoint`: half-built fields simply restart from row 0 (as before). |
| Fields stuck Failed after rollback | `materialize --id 17 reset --field ID` (Migrator) |
| Leftover progress rows | `DELETE FROM meta.ComputeBuildCheckpoint;` (safe at any time; builds then restart from row 0) |
| Remove migration 100 | not needed (additive, empty after builds finish). If you insist: `DROP TABLE meta.ComputeBuildCheckpoint;` only while no worker runs the new image (the new code tolerates a missing table). |
| API redeployed by the push and misbehaves | Redeploy the previous API artifact (workflow `c-powerbase-api` on the previous commit `36b2b8d1`). The API change is limited to shared Compute classes, no API behaviour changed. |
