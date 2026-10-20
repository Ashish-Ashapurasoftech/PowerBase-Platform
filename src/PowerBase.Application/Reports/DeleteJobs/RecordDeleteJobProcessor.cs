using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records.Commands.BulkDeleteRecords;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Reports.DeleteJobs;

public sealed record RecordDeleteChunkResult(int Deleted, Guid? FirstId);

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
    private readonly IAppTableRepository _tableRepo;
    private readonly IAuditRepository _auditRepo;

    public RecordDeleteJobProcessor(
        RunReportQueryHandler runHandler,
        BulkDeleteRecordsCommandHandler bulkDeleteHandler,
        IAppTableRepository tableRepo,
        IAuditRepository auditRepo)
    {
        _runHandler = runHandler;
        _bulkDeleteHandler = bulkDeleteHandler;
        _tableRepo = tableRepo;
        _auditRepo = auditRepo;
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
