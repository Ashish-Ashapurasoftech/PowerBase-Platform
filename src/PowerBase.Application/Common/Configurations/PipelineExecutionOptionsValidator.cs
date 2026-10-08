using System;

namespace PowerBase.Application.Common.Configurations;

public static class PipelineExecutionOptionsValidator
{
    public static void Validate(PipelineExecutionOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        // Unconditional Database Queue validation
        if (options.LoopBatchSize is < 1 or > 1000 || options.LoopConcurrency is < 1 or > 32 ||
            options.PerInstanceLoopConcurrency is < 1 or > 64 ||
            options.LoopConcurrency > options.PerInstanceLoopConcurrency)
        {
            throw new InvalidOperationException("PipelineExecution loop batch/concurrency limits are invalid.");
        }

        if (options.PerInstanceTenantConcurrencyLimit <= 0)
        {
            throw new InvalidOperationException("PipelineExecution:PerInstanceTenantConcurrencyLimit must be greater than zero.");
        }

        if (options.WorkerShutdownWaitSeconds <= 0)
        {
            throw new InvalidOperationException("PipelineExecution:WorkerShutdownWaitSeconds must be greater than zero.");
        }

        if (options.SqlDeadlockMaxRetries < 0)
        {
            throw new InvalidOperationException("PipelineExecution:SqlDeadlockMaxRetries cannot be negative.");
        }

        if (options.DatabaseQueue == null)
        {
            throw new InvalidOperationException("PipelineExecution:DatabaseQueue options are missing.");
        }

        if (options.DatabaseQueue.LeaseSeconds <= 0)
        {
            throw new InvalidOperationException("PipelineExecution:DatabaseQueue:LeaseSeconds must be greater than zero.");
        }

        if (options.DatabaseQueue.HeartbeatSeconds <= 0 ||
            options.DatabaseQueue.HeartbeatSeconds >= options.DatabaseQueue.LeaseSeconds)
        {
            throw new InvalidOperationException("PipelineExecution:DatabaseQueue:HeartbeatSeconds must be greater than zero and less than LeaseSeconds.");
        }

        if (options.DatabaseQueue.MaxAttempts <= 0)
        {
            throw new InvalidOperationException("PipelineExecution:DatabaseQueue:MaxAttempts must be greater than zero.");
        }

        if (options.DatabaseQueue.GlobalConcurrencyLimit <= 0 ||
            options.DatabaseQueue.TenantConcurrencyLimit <= 0 ||
            options.DatabaseQueue.PipelineConcurrencyLimit <= 0)
        {
            throw new InvalidOperationException("PipelineExecution database queue concurrency limits must be greater than zero.");
        }

        if (options.DatabaseQueue.QueuePollingIntervalSeconds <= 0 ||
            options.DatabaseQueue.RelayPollingIntervalSeconds <= 0 ||
            options.DatabaseQueue.RelayTenantConcurrency <= 0 ||
            options.DatabaseQueue.RelayBatchSize <= 0 ||
            options.DatabaseQueue.ExecutionBatchSize <= 0 ||
            options.DatabaseQueue.BaseRetryDelaySeconds <= 0 ||
            options.DatabaseQueue.RetentionDays < 0 ||
            options.DatabaseQueue.CleanupBatchSize <= 0 ||
            options.DatabaseQueue.CleanupMaxBatchesPerRun <= 0 ||
            options.DatabaseQueue.CleanupIntervalHours <= 0)
        {
            throw new InvalidOperationException("PipelineExecution database queue polling, batch, retry, and cleanup values are invalid.");
        }

        if (options.SearchRecordsPageSize <= 0 || options.StreamSearchAboveRecords < options.SearchRecordsPageSize ||
            options.MaxStepOutputBytes < 0 || options.BulkEventPageSize <= 0)
        {
            throw new InvalidOperationException("PipelineExecution search paging limits are invalid.");
        }
    }
}
