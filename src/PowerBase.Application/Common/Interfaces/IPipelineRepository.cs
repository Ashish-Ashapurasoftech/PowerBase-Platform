using System;
using System.Collections.Generic;
using System.Data;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public class PipelineListItemDetail
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public bool IsActive { get; set; }
    public DateTime CreatedOn { get; set; }
    public string? FirstStepType { get; set; }
    public string? FirstStepSubtype { get; set; }
}

public interface IPipelineRepository
{
    Task<Pipeline> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<Pipeline?> GetByIdAsync(long id, CancellationToken ct = default);
    Task<long> GetIdByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyList<PipelineListItemDetail>> ListByUserPagedAsync(long userId, int page, int pageSize, string? search, string sortBy, bool sortDesc, bool? isActive, CancellationToken ct = default);
    Task<int> CountByUserAsync(long userId, string? search, bool? isActive, CancellationToken ct = default);
    Task<IReadOnlyList<Pipeline>> ListAllActiveAsync(CancellationToken ct = default);
    Task<(Guid PublicId, long Id)> CreateAsync(Pipeline pipeline, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<int> UpdateAsync(Pipeline pipeline, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task DeleteAsync(Guid publicId, CancellationToken ct = default);
    Task SoftDeleteManyAsync(IEnumerable<Guid> publicIds, CancellationToken ct = default);

    Task<IReadOnlyList<PipelineStep>> GetStepsByPipelineIdAsync(long pipelineId, CancellationToken ct = default);
    Task SaveStepsAsync(long pipelineId, IEnumerable<PipelineStep> steps, byte[] rowVersion, bool deactivate = false, IDbTransaction? transaction = null, CancellationToken ct = default);

    Task<PipelineConnection?> GetConnectionByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyList<PipelineConnection>> GetConnectionsByPipelineIdAsync(long pipelineId, CancellationToken ct = default);
    Task<(Guid PublicId, long Id)> CreateConnectionAsync(PipelineConnection connection, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<int> UpdateConnectionAsync(PipelineConnection connection, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task DeleteConnectionAsync(Guid publicId, CancellationToken ct = default);

    Task<(Guid PublicId, long Id)> CreateRunAsync(PipelineRun run, CancellationToken ct = default);
    Task UpdateRunAsync(PipelineRun run, CancellationToken ct = default);
    Task<PipelineRun?> GetRunByMessageIdAsync(Guid messageId, CancellationToken ct = default);
    Task<long> CreateRunAttemptAsync(PipelineRunAttempt attempt, CancellationToken ct = default);
    Task UpdateRunAttemptAsync(PipelineRunAttempt attempt, CancellationToken ct = default);
    Task<bool> ReclaimStaleRunAsync(Guid messageId, string workerId, CancellationToken ct = default);
    Task<bool> ClaimFailedRunRetryAsync(Guid messageId, string workerId, CancellationToken ct = default);
    Task ExtendRunLeaseAsync(Guid messageId, string workerId, CancellationToken ct = default);
    Task<long> CreateStepRunAsync(PipelineStepRun stepRun, CancellationToken ct = default);
    Task UpdateStepRunAsync(PipelineStepRun stepRun, CancellationToken ct = default);
    Task<IReadOnlyList<PipelineStepRun>> GetStepRunsByRunIdAsync(long runId, CancellationToken ct = default);
    Task<IReadOnlyList<PipelineStepRun>> GetStepRunsByRunIdAsync(long runId, int page, int pageSize, CancellationToken ct = default);
    Task<int> CountStepRunsByRunIdAsync(long runId, CancellationToken ct = default);
    Task<PipelineRun?> GetRunByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyList<PipelineRun>> GetRunsByPipelineIdAsync(long pipelineId, int page, int pageSize, CancellationToken ct = default);
    Task<int> CountRunsByPipelineIdAsync(long pipelineId, CancellationToken ct = default);

    // Staging table operations for On New Bulk Event
    Task InsertBulkEventRecordsAsync(List<PipelineBulkEventRecord> records, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<IReadOnlyList<PipelineBulkEventRecord>> GetBulkEventRecordsPreviewAsync(Guid bulkEventId, int limit, CancellationToken ct = default);
    Task<IReadOnlyList<PipelineBulkEventRecord>> GetPendingBulkEventRecordsPageAsync(Guid bulkEventId, int page, int pageSize, CancellationToken ct = default);
    Task MarkBulkEventRecordsProcessedAsync(List<long> ids, byte processedStatus, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task DeleteExpiredBulkEventRecordsAsync(DateTime createdBefore, CancellationToken ct = default);
    Task<IReadOnlyList<(string PipelineName, string StepLabel)>> GetActivePipelineReferencesForFieldAsync(int fid, CancellationToken ct = default);
    Task<IReadOnlyList<Pipeline>> GetActivePipelinesReferencingFieldAsync(int fid, CancellationToken ct = default);
    Task<IReadOnlyList<string>> GetPipelineNamesForUserAsync(long userId, CancellationToken ct = default);
    Task<bool> NameExistsForUserAsync(long userId, string name, CancellationToken ct = default);
    Task<byte[]> GetRowVersionAsync(long pipelineId, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task InvalidateStepsReferencingFieldAsync(int fid, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<PipelineStep?> GetStepByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<bool> UpdateStepLastTriggeredOnAsync(long stepId, DateTime? oldTime, DateTime newTime, CancellationToken ct = default);
    Task<IReadOnlyList<PipelineStep>> GetActiveScheduleStepsAsync(CancellationToken ct = default);

    Task<PipelineSchedule?> GetScheduleByPipelineIdAsync(long pipelineId, CancellationToken ct = default);
    Task<PipelineSchedule?> GetScheduleByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<(Guid PublicId, long Id)> CreateScheduleAsync(PipelineSchedule schedule, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<int> UpdateScheduleAsync(PipelineSchedule schedule, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task DeleteScheduleAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyList<PipelineSchedule>> GetActivePipelineSchedulesAsync(CancellationToken ct = default);
    Task<bool> UpdateScheduleLastAndNextRunOnAsync(long scheduleId, DateTime? oldLastRun, DateTime newLastRun, DateTime? newNextRun, CancellationToken ct = default);
    
    Task<long> CreateOutboxItemAsync(PipelineOutboxItem item, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<IReadOnlyList<PipelineOutboxItem>> ClaimOutboxItemsAsync(string workerId, CancellationToken ct = default);
    Task UpdateOutboxItemStatusAsync(long id, string workerId, byte status, DateTime? publishedOn = null, DateTime? failedOn = null, string? error = null, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task PruneOutboxItemsAsync(DateTime olderThan, CancellationToken ct = default);
    Task SyncTriggerSubscriptionsAsync(long pipelineId, IDbTransaction? tenantTransaction = null, CancellationToken ct = default);
}

