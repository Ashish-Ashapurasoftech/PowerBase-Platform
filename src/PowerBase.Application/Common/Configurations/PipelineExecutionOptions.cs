using System;

namespace PowerBase.Application.Common.Configurations;

public class PipelineExecutionOptions
{
    // Existing values (kept for compatibility)
    public int PrefetchCount { get; set; } = 5;
    public int MaxRetryCount { get; set; } = 3;
    public int TenantConcurrencyLimit { get; set; } = 5;
    public int IdempotencyCacheDurationMinutes { get; set; } = 5;
    public int WorkerShutdownWaitSeconds { get; set; } = 10;
    public int SqlDeadlockMaxRetries { get; set; } = 3;

    // Final Database-only options
    public int PerInstanceTenantConcurrencyLimit { get; set; } = 5;
    public int BulkEventPageSize { get; set; } = 500;
    public int SearchRecordsPageSize { get; set; } = 500;
    /// <summary>Seconds a single Search Records SQL command may run. It was unlimited (0), so one bad scan over millions of
    /// rows left the run "Running" forever; a timeout now fails the attempt and lets the queue retry it.</summary>
    public int SearchCommandTimeoutSeconds { get; set; } = 600;
    /// <summary>A Pause of at most this many seconds is waited out inside the running job instead of releasing it to the queue
    /// and replaying the whole run when it is due. Releasing costs a queue poll (seconds) plus a replay of every step already
    /// done, so a loop of N short pauses took ~2.5 s per item and replayed O(N²) steps. Longer pauses still release the job. 0 disables.</summary>
    public int PauseInlineMaxSeconds { get; set; } = 30;
    /// <summary>Total seconds one execution attempt may spend waiting out pauses inline, so a long loop of short pauses cannot hold a
    /// worker slot for hours; once spent, the next pause releases the job to the queue as before.</summary>
    public int PauseInlineBudgetSeconds { get; set; } = 120;
    /// <summary>NOT a limit. A Search Records result larger than this that is consumed by exactly one Loop is streamed
    /// (paged, staged, checkpointed) instead of being held in memory; every other search simply returns all of its
    /// records, however many there are. It only chooses the strategy — nothing is ever refused for being too big.</summary>
    public int StreamSearchAboveRecords { get; set; } = 10000;
    /// <summary>How far behind SQL the Azure AI Search index may be. A search answered by AI Search (Search Records, Copy Records)
    /// also reads, from SQL, every record created or modified within this many seconds before it started, so a record the
    /// index has not caught up with yet is still found. 0 turns that off. It does not cover an index update that was lost.</summary>
    public int AiSearchIndexLagSeconds { get; set; } = 600;
    /// <summary>Answer contains / starts with / ends with / wildcard conditions from Azure AI Search too (regular expressions over the
    /// index's words, always confirmed in SQL). On by default: it was checked against the real search service (a scratch index loaded
    /// with awkward text, hundreds of random conditions: nothing SQL matches was left out). Set false to read these conditions from SQL again.</summary>
    public bool AiSearchPatternOperators { get; set; } = true;
    /// <summary>A table with fewer records than this is searched in SQL: a scan is quick there, and the index (and its lag) is not needed. 0 = always use the index when it can answer.</summary>
    public int AiSearchMinTableRows { get; set; } = 20000;
    /// <summary>Before answering from the index, compare how many records it holds with how many the table has (allowing for the
    /// records changed within <see cref="AiSearchIndexLagSeconds"/>). An index that holds fewer is missing records, so the search is read from SQL.</summary>
    public bool AiSearchCountGuard { get; set; } = true;
    /// <summary>A search that uses a field changed within this many hours (a field just switched on for search is still being copied into
    /// the index) is read from SQL. 0 turns that off.</summary>
    public int AiSearchFieldSettleHours { get; set; } = 24;
    /// <summary>With a MaxResults, the index is used only when it finds at most this many candidates: they are all read, sorted by
    /// record id and cut to MaxResults, so the same records come back as from SQL. More candidates are read from SQL, which stops at MaxResults.</summary>
    public int AiSearchLimitedReadMaxMatches { get; set; } = 20000;
    /// <summary>How long the table and index counts of the guards above are reused (seconds). 0 = never.</summary>
    public int AiSearchGuardCacheSeconds { get; set; } = 30;
    /// <summary>Optional cap on the size of a Search Records output. 0 (the default) means unlimited.</summary>
    public int MaxStepOutputBytes { get; set; }
    public bool EnableLoopBatches { get; set; }
    public int LoopBatchSize { get; set; } = 100;
    /// <summary>A loop whose only step is Create Record writes each batch in one transaction — one multi-row INSERT, one
    /// receipt write, one trigger write and one history write for the whole batch — instead of a transaction and some twenty
    /// round trips per record. Same rules, same records, same history; a batch it cannot write that way (a record a rule
    /// rejects, a duplicate within the batch, any write error) is written record by record exactly as before.</summary>
    public bool BulkCreateRecordsInLoops { get; set; }
    // In automatic mode the two concurrency values below are ceilings, not fixed counts.
    public bool AutoScaleLoopWorkers { get; set; }
    public int LoopConcurrency { get; set; } = 4;
    public int PerInstanceLoopConcurrency { get; set; } = 16;
    public DatabaseQueueOptions DatabaseQueue { get; set; } = new();
}

public class DatabaseQueueOptions
{
    public int RelayPollingIntervalSeconds { get; set; } = 5;
    public int QueuePollingIntervalSeconds { get; set; } = 2;
    public int RelayTenantConcurrency { get; set; } = 4;
    public int RelayBatchSize { get; set; } = 50;
    public int ExecutionBatchSize { get; set; } = 20;
    public int GlobalConcurrencyLimit { get; set; } = 50;
    public int TenantConcurrencyLimit { get; set; } = 10;
    public int PipelineConcurrencyLimit { get; set; } = 3;
    public int LeaseSeconds { get; set; } = 120;
    public int HeartbeatSeconds { get; set; } = 30;
    public int MaxAttempts { get; set; } = 5;
    public int BaseRetryDelaySeconds { get; set; } = 10;
    public int RetentionDays { get; set; } = 14;
    public int CleanupBatchSize { get; set; } = 500;
    public int CleanupMaxBatchesPerRun { get; set; } = 10;
    public int CleanupIntervalHours { get; set; } = 24;
}
