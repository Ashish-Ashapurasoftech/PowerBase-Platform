namespace PowerBase.Application.Compute;

/// <summary>
/// Settings of the stored-columns feature (config section "Compute"). Every default keeps today's behaviour: nothing is written and
/// nothing runs until <see cref="Enabled"/> is switched on AND the tenant's own ComputeEnabled flag (control meta.Tenant) is set.
/// Defaults live here, not in appsettings, so a deployment without the section behaves exactly as before.
/// </summary>
public sealed class ComputeOptions
{
    public const string SectionName = "Compute";

    /// <summary>Global kill switch. False = no ComputeOutbox row is written for any tenant and the worker does nothing.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>How long the per-tenant flag is cached in each process.</summary>
    public int SettingsCacheSeconds { get; set; } = 15;

    /// <summary>The Service Bus queue (sessions enabled) the compute worker listens on.</summary>
    public string QueueName { get; set; } = "compute-queue";

    public int RelayIntervalSeconds { get; set; } = 10;
    /// <summary>A change younger than this is left to the normal path (the message published right after the write).</summary>
    public int GraceSeconds { get; set; } = 60;
    /// <summary>Minimum time between two re-publishes of the same change.</summary>
    public int RetryAfterSeconds { get; set; } = 120;
    /// <summary>After this many re-publishes a row is parked until the daily sweep re-drives it.</summary>
    public int MaxAttempts { get; set; } = 10;
    public int BatchSize { get; set; } = 500;
    public int TenantConcurrency { get; set; } = 4;

    /// <summary>
    /// Phase 2 ships with this off: the worker only counts and logs, nothing consumes the rows, so re-publishing them would only fill
    /// the queue. Phase 3 switches it on together with the real worker.
    /// </summary>
    public bool RepublishEnabled { get; set; } = true;

    /// <summary>While nothing consumes the rows (Republish off), rows older than this are deleted so staging does not grow without limit.</summary>
    public int ObserveRetentionDays { get; set; } = 14;

    // ── Phase 3: the worker writes c_ columns ─────────────────────────────────────────────────────────────

    /// <summary>Parents recomputed per Summary statement (2,000–5,000 keeps each statement short and under lock escalation).</summary>
    public int SummaryBatchSize { get; set; } = 2000;
    /// <summary>Children recomputed per Lookup statement.</summary>
    public int LookupBatchSize { get; set; } = 5000;
    /// <summary>Outbox rows drained per round for one table.</summary>
    public int DrainBatchSize { get; set; } = 500;
    /// <summary>Worker sessions allowed in one tenant database at a time, across every worker replica (sp_getapplock slots).</summary>
    public int MaxSessionsPerDatabase { get; set; } = 2;
    /// <summary>Pause after every batch so API queries run in between.</summary>
    public int PauseBetweenBatchesMs { get; set; } = 150;
    /// <summary>Above this CPU / data IO / log write percent (sys.dm_db_resource_stats) the worker waits a little before the next batch.</summary>
    public int GuardSlowPercent { get; set; } = 60;
    /// <summary>Above this it stops and tries again later.</summary>
    public int GuardStopPercent { get; set; } = 80;
    /// <summary>Off-peak window (UTC hours, start inclusive, end exclusive) for backfills and the nightly check. -1 = any time.</summary>
    public int OffPeakStartHourUtc { get; set; } = -1;
    public int OffPeakEndHourUtc { get; set; } = -1;
    /// <summary>How often the backfill runner looks for fields to build.</summary>
    public int BackfillPollSeconds { get; set; } = 60;
    /// <summary>Hour (UTC) of the nightly reconciliation sweep.</summary>
    public int ReconcileHourUtc { get; set; } = 1;
    /// <summary>
    /// Decision Q3 (2026-11-20): store every Lookup / Summary / Formula field. The worker switches on every calculated field that is not
    /// decided yet (MaterializeMode 0); the rules still refuse the ones that cannot be stored, and a field an admin excluded stays off.
    /// False = only fields switched on by hand are stored.
    /// </summary>
    public bool StoreAllCalculatedFields { get; set; } = true;
    /// <summary>Build / drop the sort-filter indexes of stored columns (IX_t_X_c_N) to match what saved reports and role rules use.</summary>
    public bool IndexesEnabled { get; set; } = true;
    /// <summary>How often each tenant's indexes are compared with its reports (also right after a field becomes Ready).</summary>
    public int IndexSyncMinutes { get; set; } = 10;
}

/// <summary>Settings of the Azure SQL read replica routing (config section "ReadReplica"). Off by default: every read uses the primary.</summary>
public sealed class ReadReplicaOptions
{
    public const string SectionName = "ReadReplica";

    /// <summary>Global kill switch. False = <c>CreateReadOnlyAsync</c> always returns a primary connection.</summary>
    public bool Enabled { get; set; } = false;

    /// <summary>A replica whose heartbeat is older than this is "behind": the read goes to the primary.</summary>
    public int MaxLagSeconds { get; set; } = 5;
    /// <summary>The same for background jobs (backfill, reconciliation, indexing), which can tolerate more.</summary>
    public int BackgroundMaxLagSeconds { get; set; } = 60;
    /// <summary>The lag check result is reused for this long.</summary>
    public int LagCacheSeconds { get; set; } = 5;
    /// <summary>How often the compute worker writes meta.ReplicaHeartbeat on the primary.</summary>
    public int HeartbeatIntervalSeconds { get; set; } = 5;
}
