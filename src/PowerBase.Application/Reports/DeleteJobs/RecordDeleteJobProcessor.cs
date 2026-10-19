using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records.Commands.BulkDeleteRecords;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Reports.DeleteJobs;

/// <param name="Examined">Matching records looked at in this chunk. 0 means nothing is left to delete.</param>
/// <param name="Deleted">Records actually deleted (can be fewer than examined: a user limited to their own records skips the rest).</param>
/// <param name="LastRecordId">Keyset cursor for the next chunk (ids-only mode).</param>
/// <param name="IdsOnly">False when the report had to be evaluated the slow way (it filters on a computed/encrypted field);
/// the caller then keeps asking for page 1 instead of advancing a cursor.</param>
public sealed record RecordDeleteChunkResult(int Examined, int Deleted, Guid? FirstId, long LastRecordId, bool IdsOnly);

/// <summary>
/// Deletes one chunk of a <see cref="RecordDeleteJob"/>. The worker calls it in a loop, each time in a fresh
/// DI scope with the requesting user's identity, until a chunk examines nothing.
///
/// Finding the records is cheap: the report's own filters and the user's permissions are applied exactly as the
/// grid applies them, but only (Id, PublicId) of the next batch after the last Id are read — no sort, no count, no
/// projections — so every chunk is one index seek however far the job has got. The deletion itself goes through
/// <see cref="BulkDeleteRecordsCommandHandler"/> (relationship guard, pipeline triggers, search-index updates), each
/// chunk in its own short transaction.
/// </summary>
public class RecordDeleteJobProcessor
{
    /// <summary>Records per chunk. Stays under SQL Server's 2,100-parameter limit (each id is one parameter in the IN list).</summary>
    public const int ChunkSize = 500;
    /// <summary>The slow path reads whole report pages, which RunReportQueryHandler caps at 200.</summary>
    private const int LegacyPageSize = 200;

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
    /// <param name="afterRecordId">Cursor from the previous chunk (0 for the first).</param>
    /// <param name="slowPath">True once a previous chunk reported <c>IdsOnly = false</c>.</param>
    public async Task<RecordDeleteChunkResult> DeleteNextChunkAsync(
        RecordDeleteJob job, int remaining, long afterRecordId, bool slowPath, CancellationToken ct)
    {
        if (remaining <= 0) return new RecordDeleteChunkResult(0, 0, null, afterRecordId, !slowPath);

        var filter = RecordDeleteJobFilter.FromJson(job.FilterJson);
        var query = slowPath
            ? filter.ToQuery(job.ReportPublicId, LegacyPageSize, idsOnlyAfterId: null)
            : filter.ToQuery(job.ReportPublicId, Math.Min(ChunkSize, remaining), idsOnlyAfterId: afterRecordId);

        // In the slow path, records deleted by earlier chunks no longer match, so page 1 is always the next chunk.
        var page = await _runHandler.HandleAsync(query, ct);

        var idsOnly = !slowPath && page.IdsOnlyHandled;
        var ids = page.Items.Select(i => i.Id).Distinct().Take(remaining).ToList();
        if (ids.Count == 0) return new RecordDeleteChunkResult(0, 0, null, afterRecordId, idsOnly);

        var deleted = await _bulkDeleteHandler.HandleAsync(
            new BulkDeleteRecordsCommand(job.TablePublicId, ids, ids.Count, SkipAudit: true, SkipRecordsNotOwned: true), ct);
        return new RecordDeleteChunkResult(ids.Count, deleted, ids[0], idsOnly ? page.LastRecordId : afterRecordId, idsOnly);
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
