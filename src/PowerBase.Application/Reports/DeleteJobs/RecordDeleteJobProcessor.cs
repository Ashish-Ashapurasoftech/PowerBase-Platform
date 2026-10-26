using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records.Commands.BulkDeleteRecords;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Reports.DeleteJobs;

public sealed record RecordDeleteChunkResult(int Deleted, Guid? FirstId);

/// <summary>A job resolved once into plain SQL: the filter, the table's fields and the per-job delete plan. Immutable, so
/// the worker keeps it across chunks (each chunk still runs in its own DI scope).</summary>
public sealed record RecordDeleteFastPlan(ReportDeleteScope Scope, BulkDeleteJobPlan DeletePlan);

public sealed record RecordDeleteFastChunkResult(int Deleted, long LastId);

/// <summary>
/// Deletes one chunk of a <see cref="RecordDeleteJob"/>. The worker calls it in a loop, each time in a fresh
/// DI scope with the requesting user's identity, until a chunk deletes nothing. Every chunk goes through
/// <see cref="BulkDeleteRecordsCommandHandler"/>, so permissions, relationship guards, pipeline triggers
/// and search-index updates are identical to a normal bulk delete (each chunk in its own short transaction).
/// </summary>
public class RecordDeleteJobProcessor
{
    /// <summary>RunReportQueryHandler clamps page size to 200, so a chunk is at most 200 records.</summary>
    public const int ChunkSize = 200;

    private readonly RunReportQueryHandler _runHandler;
    private readonly BulkDeleteRecordsCommandHandler _bulkDeleteHandler;
    private readonly IRecordRepository _recordRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAuditRepository _auditRepo;

    public RecordDeleteJobProcessor(
        RunReportQueryHandler runHandler,
        BulkDeleteRecordsCommandHandler bulkDeleteHandler,
        IRecordRepository recordRepo,
        IAppTableRepository tableRepo,
        IAuditRepository auditRepo)
    {
        _runHandler = runHandler;
        _bulkDeleteHandler = bulkDeleteHandler;
        _recordRepo = recordRepo;
        _tableRepo = tableRepo;
        _auditRepo = auditRepo;
    }

    /// <summary>Records per fast chunk: one keyset query, one UPDATE, one transaction. Kept under SQL Server's
    /// ~5,000-lock escalation threshold so a delete never escalates to a table lock.</summary>
    public const int FastChunkSize = 4000;

    /// <summary>Resolves the job into a fast plan, or null when its report can't be walked by plain SQL (computed or
    /// encrypted fields in the filter, quick search, non-Table report) — the caller then uses
    /// <see cref="DeleteNextChunkAsync"/>.</summary>
    public async Task<RecordDeleteFastPlan?> PrepareFastPlanAsync(RecordDeleteJob job, CancellationToken ct)
    {
        var query = RecordDeleteJobFilter.FromJson(job.FilterJson).ToQuery(job.ReportPublicId, ChunkSize);
        var scope = await _runHandler.ResolveDeleteScopeAsync(query, ct);
        if (scope is null) return null;
        var deletePlan = await _bulkDeleteHandler.PrepareJobAsync(scope.Table, scope.Fields, scope.Access, ct);
        return new RecordDeleteFastPlan(scope, deletePlan);
    }

    /// <param name="afterId">Internal Id of the last record the previous chunk handled (0 for the first).</param>
    /// <param name="remaining">How many more records the job may delete (confirmed count − already deleted).</param>
    public async Task<RecordDeleteFastChunkResult> DeleteNextFastChunkAsync(
        RecordDeleteFastPlan plan, long afterId, int remaining, CancellationToken ct)
    {
        if (remaining <= 0) return new RecordDeleteFastChunkResult(0, afterId);

        var scope = plan.Scope;
        var rows = await _recordRepo.ListIdsAfterIdAsync(
            scope.Table, scope.Fields, afterId, Math.Min(FastChunkSize, remaining), scope.FilterTree, scope.RestrictToCreatedBy, ct);
        if (rows.Count == 0) return new RecordDeleteFastChunkResult(0, afterId);

        await _bulkDeleteHandler.DeleteJobChunkAsync(plan.DeletePlan, rows, ct);
        return new RecordDeleteFastChunkResult(rows.Count, rows[^1].Id);
    }

    /// <param name="remaining">How many more records the job may delete (confirmed count − already deleted).
    /// Records that started matching after the user confirmed are therefore never touched.</param>
    public async Task<RecordDeleteChunkResult> DeleteNextChunkAsync(RecordDeleteJob job, int remaining, CancellationToken ct)
    {
        if (remaining <= 0) return new RecordDeleteChunkResult(0, null);

        var query = RecordDeleteJobFilter.FromJson(job.FilterJson).ToQuery(job.ReportPublicId, ChunkSize);
        // Records deleted by earlier chunks no longer match, so page 1 is always the next chunk.
        var page = await _runHandler.HandleAsync(query, ct);

        var ids = page.Items.Select(i => i.Id).Distinct().Take(remaining).ToList();
        if (ids.Count == 0) return new RecordDeleteChunkResult(0, null);

        await _bulkDeleteHandler.HandleAsync(
            new BulkDeleteRecordsCommand(job.TablePublicId, ids, ids.Count, SkipAudit: true), ct);
        return new RecordDeleteChunkResult(ids.Count, ids[0]);
    }

    /// <summary>One audit entry for the whole job instead of one per chunk.</summary>
    public async Task LogSummaryAsync(RecordDeleteJob job, int deleted, string status, CancellationToken ct)
    {
        var table = await _tableRepo.GetByPublicIdAsync(job.TablePublicId, ct);
        await _auditRepo.LogActivityAsync(
            AuditActions.Deleted,
            AuditEntityTypes.Record,
            job.TablePublicId.ToString(),
            $"{deleted:N0} record(s) deleted from {table.Name} via \"Delete these records\" ({status})",
            appId: table.AppId,
            ct: ct);
    }
}
