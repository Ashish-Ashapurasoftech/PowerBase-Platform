using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines.Queries.GetCopyFailedRecords;

public record GetCopyFailedRecordsQuery(Guid RunPublicId, long StepRunId, int Page = 1);

public record CopyFailedRecordsPage(IReadOnlyList<CopyFailedRecord> Items, long Total, int Page, int PageSize);

/// <summary>Reads back the rejected records a Copy Records step kept (see <see cref="CopyFailedRecordStore"/>), a stored page
/// at a time, so the run history can show all of them without the step output ever carrying them.</summary>
public class GetCopyFailedRecordsQueryHandler
{
    private readonly IPipelineRepository _pipelineRepo;
    private readonly IPipelineStepIdempotencyRepository _receipts;

    public GetCopyFailedRecordsQueryHandler(IPipelineRepository pipelineRepo, IPipelineStepIdempotencyRepository receipts)
    {
        _pipelineRepo = pipelineRepo;
        _receipts = receipts;
    }

    private sealed record Location(Guid MessageId, Guid StepPublicId, string Prefix, int Pages, long Total);

    private async Task<Location?> LocateAsync(Guid runPublicId, long stepRunId, CancellationToken ct)
    {
        var run = await _pipelineRepo.GetRunByPublicIdAsync(runPublicId, ct)
            ?? throw new NotFoundException("PowerFlowRun", runPublicId);
        var stepRun = (await _pipelineRepo.GetStepRunsByRunIdAsync(run.Id, ct)).FirstOrDefault(s => s.Id == stepRunId)
            ?? throw new NotFoundException("PowerFlowStepRun", stepRunId.ToString());
        if (run.MessageId is not { } messageId || string.IsNullOrEmpty(stepRun.ExecutionPath)) return null;

        var stepPublicId = stepRun.StepPublicIdSnapshot;
        if (stepPublicId is null || stepPublicId == Guid.Empty)
            stepPublicId = (await _pipelineRepo.GetStepsByPipelineIdAsync(run.PipelineId, ct)).FirstOrDefault(s => s.Id == stepRun.StepId)?.PublicId;
        if (stepPublicId is null) return null;

        var prefix = CopyFailedRecordStore.Prefix(stepRun.ExecutionPath);
        var complete = await _receipts.GetByExecutionKeyAsync(messageId, stepPublicId.Value, CopyFailedRecordStore.Hash(prefix + "/complete"), null, ct);
        if (complete == null) return null;
        using var doc = JsonDocument.Parse(complete);
        var pages = doc.RootElement.TryGetProperty("FailedRecordPages", out var p) && p.TryGetInt32(out var pageCount) ? pageCount : 0;
        var total = doc.RootElement.TryGetProperty("FailedRecordsStored", out var t) && t.TryGetInt64(out var storedCount) ? storedCount : 0;
        return new Location(messageId, stepPublicId.Value, prefix, pages, total);
    }

    private async Task<List<CopyFailedRecord>> ReadPageAsync(Location location, int pageIndex, CancellationToken ct)
    {
        var json = await _receipts.GetByExecutionKeyAsync(location.MessageId, location.StepPublicId,
            CopyFailedRecordStore.Hash(location.Prefix + CopyFailedRecordStore.PageSuffix(pageIndex)), null, ct);
        return string.IsNullOrEmpty(json) ? new List<CopyFailedRecord>() : JsonSerializer.Deserialize<List<CopyFailedRecord>>(json) ?? new List<CopyFailedRecord>();
    }

    public async Task<CopyFailedRecordsPage> HandleAsync(GetCopyFailedRecordsQuery query, CancellationToken ct = default)
    {
        var location = await LocateAsync(query.RunPublicId, query.StepRunId, ct);
        var page = Math.Max(1, query.Page);
        if (location == null || page > location.Pages)
            return new CopyFailedRecordsPage(Array.Empty<CopyFailedRecord>(), location?.Total ?? 0, page, CopyFailedRecordStore.PageSize);
        return new CopyFailedRecordsPage(await ReadPageAsync(location, page - 1, ct), location.Total, page, CopyFailedRecordStore.PageSize);
    }

}
