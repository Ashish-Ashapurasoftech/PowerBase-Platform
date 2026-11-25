# Handoff: materialized (stored) column build performance — state on 2026-11-25 evening

Read this first in a new session, then [compute-build-speed.md](compute-build-speed.md) (what changed and why) and
[compute-uat-deployment.md](compute-uat-deployment.md) (checklist and rollback).

## The ask
First builds of stored `c_{fid}` columns on UAT would take about 5 days. The user wants maximum build speed with measured before / after
numbers, data accuracy kept. They talk in Gujarati / English.

## Where the work is
* Worktree `C:\Ashapurasoftech\PowerBase-Platform-ParentDeleteGuard_Fix`, branch `feature/PB-stored-computed-columns`, HEAD `36b2b8d1`.
* Everything of this work is **uncommitted**. The user has not yet said to commit. Never commit, push or deploy without their yes.
* Never stage the five modified `appsettings*.json` files (local connection string and real keys).
* Changed: `ComputeOptions.cs`, `src/PowerBase.Infrastructure/Compute/*`, compute tests. New: `database/migrations/tenant/100_create_compute_build_checkpoint.sql`,
  `tests/PowerBase.UnitTests/Compute/BuildSpeedTests.cs`, `infra/scale-compute-worker.ps1`, the three docs.

## Done and verified
Narrow reads, checkpoint / resume, Id-range splitting for every plan kind, deadlock retry on reads, nightly check no longer competes with
the backlog, load-guard cache, higher session ceilings. Details in `compute-build-speed.md`.

| Check | Result |
|---|---|
| Compute unit tests | 281 / 281 |
| Compute integration tests (LocalDB) | 37 / 37 |
| All unit tests | 13,463 pass, 2 skipped, 1 fail that also fails on clean HEAD (`AppImportJobTests...Policy_OnlyFilesOverThreeMegabytes...`) |
| Release publish of worker / API, Migrator build | 0 errors; Docker build not possible here (no Docker) |
| Narrow vs full reads, local | 17 columns identical (count + checksum) |
| 400-column read micro-benchmark | 70 ms vs 15.5 ms per 500 rows |

## NOT proven
* No wall-clock speed-up. Local 17-field run: baseline 88.5 s (15 ready, 2 failed by deadlock), new 115.2 s / 109.5 s (17 ready).
* "UAT is limited by the worker" is an inference from the database side (CPU 5–25 %, IO about 0 %, ~12 ms per 500-row read, little query
  time in flight). The worker's own CPU was never measured. Check the Container App CPU metric before building on it.
* Narrow reads have not run against UAT data.

## UAT state
* `c-powerbase-compute` is **Stopped**. This work's session stopped it from the Portal at 15:22:36 UTC on the user's request. The user said:
  keep it stopped; start only when they say so. Starting it as it is runs the OLD image.
* Revision 0000018, image `36b2b8d1e7e89d4e8c9dedeb88a9d8f457fac18a` (rollback tag), 4 CPU / 8 Gi.
* Env: `Compute__Enabled=true`, `PauseBetweenBatchesMs=0`, `MaxSessionsPerDatabase=16`, `BackgroundSessionsPerDatabase=15`,
  `BuildsPerReplica=8`, `SlicesPerField=4`, `SummaryBatchSize=5000`, `LookupBatchSize=5000`, `GuardSlowPercent=80`,
  `GuardStopPercent=95`, `PriorityTables=17:243,17:244`.
* Migration 100 is NOT applied to `Powerbase_17`.
* Seen later by another session (not verified here): `Powerbase_17` is now GP Gen5 32 vCores (was 14); UAT is used for client demos, so be
  read-only there unless the user says otherwise. Status query: `C:\Ashapurasoftech\uat-materialize-status.sql` (results not yet received).
* Build times over 3 days (same other session): table 244: 161 builds, median 231 s, max 34,116 s, about ten fields took 8–9.5 h;
  table 243: 132 builds, median 17 s, max 4,733 s; other tables about 0–1 s. So the 24 h average (5,135 s, ~119 rows/s) is dominated by a few
  outliers.

## Update 2026-11-25 late evening (second session)
* Fast lane added (item 9 of `compute-build-speed.md`): small-table fields get their own builds and DB slots. 294 compute unit tests pass.
* UAT status: 1,198 fields left (1,162 Waiting, 36 Building half way, 23 Failed); ~870 of them on small tables.
* Root-cause facts for the 8–9 h builds are in `compute-build-speed.md` ("Why the 8–9 h builds were slow"): projected formulas, worker
  CPU only 30–45 %, DB 5–25 %, so the builds wait on something. Next: profile 8 projected builds side by side locally.
* The user gave a SQL login for UAT (use **Powerbase_17 only**, read-only queries). Never write the password into a file.

## Next steps (suggested order)
1. Find what the ~10 slow fields of table 244 are (field type, plan kind, what they read). This decides where the remaining time is.
2. Commit (on the user's yes) → migration 100 on `Powerbase_17` → new image → Stage 1 with unchanged env, following the checklist.
3. Measure: `built:` lines (the new ones show plan type, sessions, compute / write seconds), fields Ready per hour, worker CPU, DB CPU / IO.
4. Next bigger lever, not built: one pass per table that works out all its pending fields from one row read.
5. Autoscale template `infra/scale-compute-worker.ps1` (not run; it also changes `BuildsPerReplica` and `SliceMinRows`).

## Local tooling
* Tests while Visual Studio locks the DLLs: build and test with `-p:BaseOutputPath=<some temp folder>\`; integration tests need
  `POWERBASE_TEST_SQL_CONNECTION='Server=(localdb)\MSSQLLocalDB;Integrated Security=true;TrustServerCertificate=True'`.
* Benchmark kit in `C:\Ashapurasoftech\compute-bench\`: `bench.ps1 -Exe <worker exe> -Label <name>` (resets and builds 17 fields of local
  tenant 45, app "Non Encrypted App 1M"), `checksums.sql`, `restore.sql` (puts the test fields back), `widebench\` (wide-read
  micro-benchmark), earlier result files. Run `restore.sql` after benchmarking (`sqlcmd -I`, database `Powerbase_45`).
