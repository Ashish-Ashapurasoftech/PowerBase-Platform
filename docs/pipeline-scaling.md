# Pipeline worker scaling

## Record-loop batches

The API configurations enable `EnableLoopBatches`. Loops read/process 100 records per batch
(`LoopBatchSize`) for independent, flat create/update/delete actions. `AutoScaleLoopWorkers` is
enabled in API configuration. At worker-pool initialization, CPU allocation and the GC memory
budget determine concurrency. `LoopConcurrency` (16) and `PerInstanceLoopConcurrency` (32)
are ceilings in automatic mode. The memory calculation reserves 1 GiB plus half the remainder
for other work and budgets 256 MiB per worker. One loop uses at most one worker per available
logical processor; the shared host pool allows up to twice that CPU count within its memory ceiling.
For example, 2 CPUs/4 GiB allows 2 workers per loop and 4 across loops; 6 CPUs/8 GiB allows
6 per loop and 12 across loops; 8 CPUs/16 GiB allows 8 per loop and 16 across loops.
Insufficient or unknown memory falls back to one worker. CPU count and memory budget honor
runtime/container limits. This is automatic capacity sizing, not continuous CPU-load or database
latency feedback: restart the API after changing the server allocation. Startup logs report the
resolved limits. Set `AutoScaleLoopWorkers: false` for fixed configured limits.
A singleton pool shares capacity across all loops; the existing queue limits still bound pipeline
runs across replicas. Workers start automatically with pipeline execution. No extra process is needed.
Each worker owns a separate dependency-injection scope, tenant identity, token restrictions and
unit of work. Table/field metadata is cached only within that worker's batch.

Nested containers, external actions, bulk sessions, self/forward references and batches containing
duplicate update/delete destinations remain sequential. A loop configuration can set
`MaxConcurrency: 1` when business ordering matters. Set `EnableLoopBatches: false` to restore the
legacy loop path. Batched execution retains existing per-action transaction and receipt keys.
Durable source rows are checkpointed together after a batch; a retry uses committed action receipts
if execution stopped before checkpointing. All worker tasks are joined before a run exits.

Execution history includes a `loop-batch` entry per batch with record count, successful/failed counts,
worker count and elapsed milliseconds, alongside the individual action entries. The existing builder
history pager exposes all entries (the standalone Activity preview only displays its first page).
Apply tenant history migrations 058/059 to retain the batch labels and subtype snapshots.

Validation: `PipelineLoopBatchTests` exercises 1,000 records, ten batches, worker bounds across
concurrent runs, context propagation, unique audit sequences, record failures and cancellation.
Its timing comparison uses simulated database writes and is not a production throughput guarantee.
The observed original local run for pipeline 20092 took 638,392 ms plus 9,866 ms audit formatting.
A new real-database run is required to confirm the five-minute target on the deployed host.

Pipeline execution is coordinated through `meta.PipelineQueue`. Multiple API or dedicated worker
replicas may consume the same queue. Claims are serialized briefly with a SQL application lock and
enforce global, per-tenant, and per-pipeline limits before changing a pending or expired-lease job to `Processing`. The normal
claim token and lease remain the ownership fence for heartbeat and terminal updates. Tenant polling
rotates between passes, and jobs from a pipeline already at capacity do not consume another pipeline's
tenant ranking slots.

Safe initial settings are 50 global jobs, 10 jobs per tenant, 3 jobs per pipeline, and 5 local jobs
per tenant per replica. Tune these values against Azure SQL connection-pool usage, DTU/vCore load,
queue age, and external-service rate limits. Environment configuration uses the standard .NET form,
for example `PipelineExecution__DatabaseQueue__GlobalConcurrencyLimit=50`.
`DatabaseQueue.MaxAttempts` is shared by the control-queue delivery and tenant-run reclaim paths, so
changing it does not create two different retry ceilings. The value is copied onto each durable queue
job and carried into the engine, keeping its retry policy stable during rolling deployments. Relay fan-out is explicitly configured with
`DatabaseQueue.RelayTenantConcurrency` (default 4).
Each tenant claim honors `DatabaseQueue.RelayBatchSize` instead of using a fixed SQL batch.
Tenant outbox delivery failures increment their retry count immediately, become dead-letter rows after
five failed delivery attempts, and are removed with other terminal pipeline data after the retention period.

The production search service exposes a 500-row keyset paging path. The legacy Search Records array
contract is bounded in SQL and capped at 10,000 records and 5 MiB so a pipeline cannot exhaust worker
memory. An unlimited Search Records step, or one whose explicit `MaxResults` exceeds the materialization
ceiling, automatically creates a durable tenant workset when it feeds exactly one Loop.
Discovery captures a maximum record ID for snapshot semantics and stores normalized rows in 500-row
transactions. The Loop reads 500 pending rows at a time and checkpoints each successful record, so a
retry resumes discovery or processing from durable progress. Discovery checkpoint writes use an
expected-last-record guard so a stale worker cannot append the same page after ownership changes.
Completed replayable steps are resolved from their message/step/execution-path receipt on every retry,
which prevents earlier successful loop iterations from being repeated after a later iteration fails.
Encrypted-field filters retain the bounded
legacy path because those predicates currently require in-memory evaluation.

Run more replicas only after applying control migration 013. Autoscale primarily on pending queue
count and oldest pending age, with database utilization and connection-pool saturation as guards.
Keep at least two replicas for availability. The API hosted worker can remain enabled on each replica;
the database limits prevent replica count from multiplying execution concurrency. Worker polling only
selects tenants that currently have due or expired-lease queue work. If lease heartbeats cannot be
confirmed for the configured safe window, execution is cancelled before the lease can expire. The
tenant `PipelineRun` has a separate ownership lease; that heartbeat is also fail-closed so a stale
tenant run cannot be reclaimed while the prior worker continues producing side effects. If an engine
returns while an existing tenant run is still non-terminal, the queue item is retried instead of being
acknowledged as successful.

Deployment order: apply control migration 013 and tenant migration 060, deploy the compatible application version, start with two
replicas, then raise replica count gradually while watching queue age, claim latency, retries, lease
reclaims, database utilization, and worker memory. Rolling back the application requires no schema
rollback because both migrations are additive.
