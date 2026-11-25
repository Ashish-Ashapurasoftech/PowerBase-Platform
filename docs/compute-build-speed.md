# Stored columns: first-build speed (2026-11-25)

## What was measured on UAT (Powerbase_17, last 24 h)

| Finding | Evidence |
|---|---|
| Builds on the 1.14 M-row table 244 are very slow | Worker log `built:` lines: 64 builds, 39.2 M rows written, 328,637 s of build time; average 5,135 s per field, longest 34,115 s (9.5 h). About 119 rows/s per field. |
| The database is NOT the limit | Query Performance Insight (Powerbase_17): database CPU 5–25 %, data IO and log IO about 0 %. The hot statement (read of 500 rows by id) ran ~98 k times per busy hour at about 12 ms each. Top queries together used 15–38 min of query time per hour, i.e. well under one query running at any moment while 8–16 sessions were building. |
| So the time goes on the worker side | Each batch maps every column of every row (the table has ~400 fields) in C#, then computes. The database waits for the worker. |
| Restarts threw hours of work away | A build interrupted by a restart / new revision started again from row 0 (three revisions were switched on 2026-11-25). |
| The nightly check competed with the builds | `Compute reconciliation` re-read whole tables of Ready fields on every worker start and held the same background slots as the first builds. |
| A deadlock victim failed a long build | Reproduced locally: 2 of 17 fields ended Failed on SQL error 1205 in a read (the batch UPDATE was retried, the reads were not). |

## What changed (all in `PowerBase.Infrastructure/Compute` + one migration)

1. **Narrow reads** (`Compute:NarrowReads`, default true). A formula reads only the columns it uses; a read-code field reads only the
   inputs of its own table that the dependency graph lists (followed through the calculated fields of that table); unknown reads fall
   back to all columns. Values are identical: all 17 stored columns of the local benchmark have the same row count and checksum with
   narrow and full reads. Micro-benchmark (this laptop, localhost, 400-column table, 500-row batches): 70 ms per batch with all columns,
   15.5 ms with 3 columns (4.3–4.7×). Over the network to Azure the gap is larger.
2. **Checkpoint / resume** (migration `100_create_compute_build_checkpoint.sql`). Every build is a list of Id ranges; each range reports
   its last written Id every 4 s to `meta.ComputeBuildCheckpoint`. A stopped build continues from there, with the watermark of its first
   start. Without the table (migration not applied yet) builds behave as before. Integration test: finished ranges are not redone.
3. **Id-range splitting for every plan kind** (lookups and summaries too, not only formulas), with `SlicesPerField × 4` small ranges
   so sessions that finish early take the next one. Bounds come from MIN/MAX of Id and the partition-statistics row count (no
   full-table scan).
4. **Deadlock retry on every read** of a batch (ids page and the value reads), not only on the UPDATE.
5. **Nightly check no longer fights the builds**: skipped for a tenant while fields wait for their first build
   (`Compute:ReconcileWhileBuilding`, default false), and a Ready field built or checked in the last `Compute:ReconcileMinAgeHours`
   (default 20) is not re-read. A finished check stamps `MaterializedOn`.
6. **Guard** reads `sys.dm_db_resource_stats` once per database per 4 s per process instead of once per batch per session.
7. **Ceilings raised**: sessions per database up to 64 (63 background), builds per replica up to 63, slices per field up to 16.
8. **Autoscale + settings script**: `infra/scale-compute-worker.ps1` (template, not run from here): replicas 1..N, KEDA `mssql`
   backlog rule (one replica per 25 fields still to build) + CPU rule; scale-in back to 1 by itself after the cool-down.

9. **Fast lane** (`Compute:FastLaneBuilds`, default 2; `Compute:FastLaneMaxRows`, default 100,000). Seen on UAT 2026-11-25: 1,198
   fields still to build, about 870 of them on small tables (about 1 s each), all waiting because the eight build slots were held by
   8–9 h builds of Sessions (table 244). Now each round splits the list into small-table fields and the rest (same priority order inside
   each); `FastLaneBuilds` of the replica's builds — and the same number of the database's background slots — are kept for the small
   ones, which may also use any other free build. A big build never takes them; at least one build always stays for big fields. A
   "busy" outcome stops only its own lane. A summary on a small parent over a big child counts as big. 0 = one lane, as before.
   Tests: 13 new unit tests in `BuildSpeedTests` (lanes, order, slot reservation, config).

## Why the 8–9 h builds were slow (UAT, 2026-11-25 evening)

| Fact | Source |
|---|---|
| 9 of the 10 slowest builds are formulas on Sessions; field 6691: 2,279 batches × 500 rows, 33,661 s = ~14.8 s per batch, 0 rows written | Worker log `built:` lines |
| Some are trivial: `DSSID = [Related DSS]`, `ActivityTest = ToText([Activity])` | `meta.AppField` on Powerbase_17 (read-only) |
| They read a Reference, RichText or text Lookup field, so the native formula plan refuses them and they run as a **ProjectedPlan** (the API read code: every column of the ~400-column table read and decrypted per batch on image fc3ab089) | `MaterializeRules.EvaluateFormula` / `EvaluateProjected` |
| They all ran side by side 11-24 ~19:40 → 11-25 05:08 UTC (rev 0000016). Three projected formulas built afterwards (7188, 7197, 7219) took ~800 s | Log timestamps minus duration |
| During that window the worker's CPU averaged **30–45 %** (peaks ~70 %) of 4 vCPU; the database 5–25 % | Container App metric "CPU Usage Percentage", 30-min average; Query Performance Insight |

So neither the worker CPU nor the database was full: the builds **wait** (round trips per batch, or something serialising the parallel
builds). Not measured yet. Narrow reads remove most of the per-batch columns; whether the wait is a lock has to be profiled locally
(eight projected builds side by side on a wide, encrypted table).

## Local before / after (17 fields, tenant 45 non-encrypted app, 125 k–531 k rows, 8 slots, laptop)

| Run | Wall clock | Fields Ready | Failed |
|---|---|---|---|
| Baseline (HEAD 36b2b8d1, same settings) | 88.5 s | 15 | 2 (SQL deadlock in a read) |
| Checkpoint + ranges + retry | 115.2 s | 17 | 0 |
| + narrow reads | 109.5 s | 17 | 0 |

The laptop does NOT show a speed-up: its tables are ~20 columns wide, the machine is shared with SQL Server and Visual Studio, and the
baseline never built its two hardest fields. What the laptop proves is correctness (identical values, no failures, resume works).
**The speed-up has to be measured on UAT** (see below).

## Deploy and measure on UAT

1. Apply migration 100 to Powerbase_17: `dotnet run --project src/PowerBase.Migrator -- migrate tenant --id 17` (with the UAT connection).
2. Push the worker image (workflow), then switch the Container App to the new tag by hand (the workflow does not run `az containerapp update`).
3. Set the env vars of `infra/scale-compute-worker.ps1` (or run it with `-WhatIf` first). Keep `Compute__MaxSessionsPerDatabase` high only
   because the database is idle: watch CPU / IO in Query Performance Insight and lower it if either passes ~70 %.
4. Compare with the baseline above using the same query on `ContainerAppConsoleLogs_CL` (`built:` lines): rows per second per field
   (baseline about 119) and fields Ready per hour (baseline 10–35).
