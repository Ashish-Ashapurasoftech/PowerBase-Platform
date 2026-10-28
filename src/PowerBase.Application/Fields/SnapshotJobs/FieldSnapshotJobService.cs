using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.SnapshotJobs;

public sealed record FieldSnapshotJobStatusResult(
    Guid JobId, string Status, int TotalCount, int ProcessedCount, int ChangedCount, string? LastError,
    DateTime CreatedOn, DateTime? StartedOn, DateTime? CompletedOn);

/// <summary>Queues, reads and cancels "Reinitialize" jobs of a Snapshot field. Queuing only validates and writes a
/// job row — <see cref="FieldSnapshotJobProcessor"/>, run by the background worker, does the work — so the UI is never blocked.</summary>
public class FieldSnapshotJobService
{
    private readonly IFieldSnapshotJobRepository _jobRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IQueryContext _queryContext;

    public FieldSnapshotJobService(
        IFieldSnapshotJobRepository jobRepo, IAppTableRepository tableRepo, IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo, IQueryContext queryContext)
    {
        _jobRepo = jobRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _queryContext = queryContext;
    }

    /// <summary>Queues a Reinitialize of <paramref name="field"/> (which must be a configured Snapshot field of <paramref name="table"/>).</summary>
    public async Task<FieldSnapshotJobStatusResult> StartAsync(AppTable table, AppField field, CancellationToken ct = default)
    {
        if (!field.SnapshotSourceFid.HasValue)
            throw new BadRequestException("Snapshot is not set up for this field.");

        if (await _jobRepo.HasActiveForFieldAsync(_queryContext.TenantId, field.PublicId, ct))
            throw new ConflictException("A Reinitialize is already running for this field. Wait for it to finish (or cancel it) before starting another.");

        var total = await _recordRepo.CountAsync(table, Array.Empty<AppField>(), ct: ct);
        var jobId = await _jobRepo.CreateAsync(new FieldSnapshotJob
        {
            TenantId = _queryContext.TenantId,
            TablePublicId = table.PublicId,
            TableName = table.Name,
            FieldPublicId = field.PublicId,
            FieldLabel = field.Label ?? field.Name,
            RequestedByUserId = _queryContext.UserId,
            IsSuperAdmin = _queryContext.IsSuperAdmin,
            TenantRole = _queryContext.TenantRole,
            TotalCount = total,
        }, ct);
        return await GetAsync(table.PublicId, field.PublicId, jobId, ct);
    }

    public async Task<FieldSnapshotJobStatusResult> StartAsync(Guid tablePublicId, Guid fieldPublicId, CancellationToken ct = default)
    {
        var (table, field) = await LoadAsync(tablePublicId, fieldPublicId, ct);
        return await StartAsync(table, field, ct);
    }

    public async Task<FieldSnapshotJobStatusResult> GetAsync(Guid tablePublicId, Guid fieldPublicId, Guid jobId, CancellationToken ct = default)
        => ToResult(await LoadJobAsync(tablePublicId, fieldPublicId, jobId, ct));

    /// <summary>The field's most recent job, or null when it never ran — lets the UI show a running job after a reload.</summary>
    public async Task<FieldSnapshotJobStatusResult?> GetLatestAsync(Guid tablePublicId, Guid fieldPublicId, CancellationToken ct = default)
    {
        var (_, field) = await LoadAsync(tablePublicId, fieldPublicId, ct);
        var job = await _jobRepo.GetLatestForFieldAsync(_queryContext.TenantId, field.PublicId, ct);
        return job is null ? null : ToResult(job);
    }

    public async Task<FieldSnapshotJobStatusResult> CancelAsync(Guid tablePublicId, Guid fieldPublicId, Guid jobId, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(tablePublicId, fieldPublicId, jobId, ct);
        if (job.RequestedByUserId != _queryContext.UserId && !_queryContext.IsSuperAdmin && !_queryContext.IsTenantAdmin)
            throw new UnauthorizedActionException("Only the person who started this Reinitialize can cancel it.");

        await _jobRepo.RequestCancelAsync(jobId, _queryContext.TenantId, ct);
        return await GetAsync(tablePublicId, fieldPublicId, jobId, ct);
    }

    private async Task<(AppTable Table, AppField Field)> LoadAsync(Guid tablePublicId, Guid fieldPublicId, CancellationToken ct)
    {
        var table = await _tableRepo.GetByPublicIdAsync(tablePublicId, ct);
        var field = await _fieldRepo.GetByPublicIdAsync(fieldPublicId, ct) ?? throw new NotFoundException("Field", fieldPublicId);
        if (field.AppTableId != table.Id) throw new NotFoundException("Field", fieldPublicId);
        return (table, field);
    }

    private async Task<FieldSnapshotJob> LoadJobAsync(Guid tablePublicId, Guid fieldPublicId, Guid jobId, CancellationToken ct)
    {
        var job = await _jobRepo.GetByPublicIdAsync(jobId, _queryContext.TenantId, ct);
        if (job is null || job.TablePublicId != tablePublicId || job.FieldPublicId != fieldPublicId)
            throw new NotFoundException("Snapshot job", jobId);
        return job;
    }

    private static FieldSnapshotJobStatusResult ToResult(FieldSnapshotJob job) => new(
        job.PublicId, job.Status, job.TotalCount, job.ProcessedCount, job.ChangedCount, job.LastError, job.CreatedOn, job.StartedOn, job.CompletedOn);
}
