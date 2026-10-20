using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports.DeleteJobs;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Commands.DeleteReportRecords;

/// <summary>
/// "Delete these records" on a Table report. There is no record limit: this handler only validates and
/// <b>queues</b> a job (meta.RecordDeleteJob); <see cref="RecordDeleteJobProcessor"/> run by the background
/// worker then deletes the matching records chunk by chunk and finally marks the job Completed.
/// The matching set is always resolved by running the report itself (<see cref="RunReportQueryHandler"/>),
/// so saved filters, role view filters, dynamic filters, quick search, the runtime filter tree and
/// ask-the-user answers are applied exactly as the grid applies them.
/// </summary>
public class DeleteReportRecordsCommandHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly RunReportQueryHandler _runHandler;
    private readonly IRecordDeleteJobRepository _jobRepo;
    private readonly IQueryContext _queryContext;

    public DeleteReportRecordsCommandHandler(
        IReportRepository reportRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRolePermissionEnforcer enforcer,
        RunReportQueryHandler runHandler,
        IRecordDeleteJobRepository jobRepo,
        IQueryContext queryContext)
    {
        _reportRepo = reportRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _enforcer = enforcer;
        _runHandler = runHandler;
        _jobRepo = jobRepo;
        _queryContext = queryContext;
    }

    public async Task<DeleteReportRecordsResult> HandleAsync(DeleteReportRecordsCommand command, CancellationToken ct = default)
    {
        var reportId = command.Query.ReportPublicId;
        var report = await _reportRepo.GetVisibleReportAsync(reportId, ct)
            ?? throw new NotFoundException("Report", reportId);

        if (report.ReportType != "Table")
            throw new BadRequestException("Records can only be deleted from a Table report.");

        if (report.IsDefault)
            throw new BadRequestException("Records can't be deleted this way from the table's default report. Use a custom report instead.");

        var definition = JsonSerializer.Deserialize<ReportDefinition>(report.Definition) ?? new ReportDefinition();
        if (definition.Options?.DisableBulkDelete ?? false)
            throw new UnauthorizedActionException("Deleting records is disabled for this report.");

        var table = await _tableRepo.GetByIdAsync(report.AppTableId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.Unrestricted && !access.CanDelete)
            throw new UnauthorizedActionException("You do not have permission to delete records from this table.");

        // Only the total is needed here — one record per page, no id enumeration.
        var probe = await _runHandler.HandleAsync(
            command.Query with { Page = 1, PageSize = 1, ClearGrouping = true, RuntimeGroupByFieldId = null }, ct);
        var totalCount = probe.TotalCount;

        if (!command.Confirm)
            return new DeleteReportRecordsResult(totalCount, null);

        if (command.ExpectedCount is null)
            throw new BadRequestException("The record count you confirmed is required.");

        if (command.ExpectedCount.Value != totalCount)
            throw new ConflictException(
                $"The matching records changed since you opened this dialog (you confirmed {command.ExpectedCount.Value:N0}, now {totalCount:N0}). Nothing was deleted — please review and try again.");

        if (totalCount == 0)
            return new DeleteReportRecordsResult(0, null);

        if (await _jobRepo.HasActiveForTableAsync(_queryContext.TenantId, table.PublicId, ct))
            throw new ConflictException("A delete is already running for this table. Wait for it to finish (or cancel it) before starting another.");

        var jobId = await _jobRepo.CreateAsync(new RecordDeleteJob
        {
            TenantId = _queryContext.TenantId,
            ReportPublicId = reportId,
            TablePublicId = table.PublicId,
            TableName = table.Name,
            RequestedByUserId = _queryContext.UserId,
            IsSuperAdmin = _queryContext.IsSuperAdmin,
            TenantRole = _queryContext.TenantRole,
            FilterJson = RecordDeleteJobFilter.FromQuery(command.Query).ToJson(),
            ExpectedCount = totalCount,
        }, ct);

        return new DeleteReportRecordsResult(totalCount, jobId);
    }

    public async Task<DeleteJobStatusResult> GetJobAsync(Guid reportPublicId, Guid jobId, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(reportPublicId, jobId, ct);
        return new DeleteJobStatusResult(job.PublicId, job.Status, job.ExpectedCount, job.DeletedCount, job.LastError, job.CreatedOn, job.StartedOn, job.CompletedOn);
    }

    public async Task<DeleteJobStatusResult> CancelJobAsync(Guid reportPublicId, Guid jobId, CancellationToken ct = default)
    {
        var job = await LoadJobAsync(reportPublicId, jobId, ct);
        if (job.RequestedByUserId != _queryContext.UserId && !_queryContext.IsSuperAdmin && !_queryContext.IsTenantAdmin)
            throw new UnauthorizedActionException("Only the person who started this delete can cancel it.");

        await _jobRepo.RequestCancelAsync(jobId, _queryContext.TenantId, ct);
        return await GetJobAsync(reportPublicId, jobId, ct);
    }

    private async Task<RecordDeleteJob> LoadJobAsync(Guid reportPublicId, Guid jobId, CancellationToken ct)
    {
        var job = await _jobRepo.GetByPublicIdAsync(jobId, _queryContext.TenantId, ct);
        if (job is null || job.ReportPublicId != reportPublicId)
            throw new NotFoundException("Delete job", jobId);
        return job;
    }
}
