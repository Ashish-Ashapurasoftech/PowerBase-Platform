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
    public DatabaseQueueOptions DatabaseQueue { get; set; } = new();
}

public class DatabaseQueueOptions
{
    public int RelayPollingIntervalSeconds { get; set; } = 5;
    public int QueuePollingIntervalSeconds { get; set; } = 2;
    public int RelayBatchSize { get; set; } = 50;
    public int ExecutionBatchSize { get; set; } = 20;
    public int LeaseSeconds { get; set; } = 120;
    public int HeartbeatSeconds { get; set; } = 30;
    public int MaxAttempts { get; set; } = 5;
    public int BaseRetryDelaySeconds { get; set; } = 10;
    public int RetentionDays { get; set; } = 14;
    public int CleanupBatchSize { get; set; } = 500;
    public int CleanupMaxBatchesPerRun { get; set; } = 10;
    public int CleanupIntervalHours { get; set; } = 24;
}
