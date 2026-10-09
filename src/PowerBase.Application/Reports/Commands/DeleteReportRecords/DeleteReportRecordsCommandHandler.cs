using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records.Commands.BulkDeleteRecords;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Commands.DeleteReportRecords;

/// <summary>
/// "Delete these records" on a Table report: deletes EVERY record the report currently matches (not
/// just the visible page). The matching set is resolved by running the report itself
/// (<see cref="RunReportQueryHandler"/>) — so saved filters, role view filters, dynamic filters, quick
/// search, runtime filter tree and ask-the-user answers are applied exactly as the grid applies them —
/// and the actual deletion is delegated to <see cref="BulkDeleteRecordsCommandHandler"/> so permission
/// checks, relationship guards, pipeline triggers, audit and search-index updates are identical to a
/// normal bulk delete, in one transaction.
/// </summary>
public class DeleteReportRecordsCommandHandler
{
    /// <summary>Hard ceiling for one call. Beyond this the user must narrow the report's filters first.
    /// Kept well under SQL Server's 2,100-parameter limit: the delete (and the relationship guard before it)
    /// expand every id into its own parameter via Dapper's "IN @ids".</summary>
    public const int MaxRecords = 1000;
    private const int RunPageSize = 200; // RunReportQueryHandler clamps page size to 200

    private readonly IReportRepository _reportRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly RunReportQueryHandler _runHandler;
    private readonly BulkDeleteRecordsCommandHandler _bulkDeleteHandler;

    public DeleteReportRecordsCommandHandler(
        IReportRepository reportRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRolePermissionEnforcer enforcer,
        RunReportQueryHandler runHandler,
        BulkDeleteRecordsCommandHandler bulkDeleteHandler)
    {
        _reportRepo = reportRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _enforcer = enforcer;
        _runHandler = runHandler;
        _bulkDeleteHandler = bulkDeleteHandler;
    }

    public async Task<DeleteReportRecordsResult> HandleAsync(DeleteReportRecordsCommand command, CancellationToken ct = default)
    {
        var reportId = command.Query.ReportPublicId;
        var report = await _reportRepo.GetVisibleReportAsync(reportId, ct)
            ?? throw new NotFoundException("Report", reportId);

        if (report.ReportType != "Table")
            throw new BadRequestException("Records can only be deleted from a Table report.");

        var definition = JsonSerializer.Deserialize<ReportDefinition>(report.Definition) ?? new ReportDefinition();
        if (definition.Options?.DisableBulkDelete ?? false)
            throw new UnauthorizedActionException("Deleting records is disabled for this report.");

        var table = await _tableRepo.GetByIdAsync(report.AppTableId, ct);
        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        var access = await _enforcer.GetTableAccessAsync(table, fields, ct);
        if (!access.Unrestricted && !access.CanDelete)
            throw new UnauthorizedActionException("You do not have permission to delete records from this table.");

        var (ids, totalCount) = await CollectMatchingIdsAsync(command.Query, ct);

        if (!command.Confirm)
            return new DeleteReportRecordsResult(totalCount, MaxRecords, totalCount > MaxRecords, false, 0);

        if (totalCount > MaxRecords)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["records"] = [$"{totalCount:N0} records match, which is more than the {MaxRecords:N0} that can be deleted at once. Narrow the report's filters and try again."]
            });

        if (command.ExpectedCount is null)
            throw new BadRequestException("The record count you confirmed is required.");

        if (command.ExpectedCount.Value != totalCount || ids.Count != totalCount)
            throw new ConflictException(
                $"The matching records changed since you opened this dialog (you confirmed {command.ExpectedCount.Value:N0}, now {totalCount:N0}). Nothing was deleted — please review and try again.");

        if (ids.Count == 0)
            return new DeleteReportRecordsResult(0, MaxRecords, false, false, 0);

        await _bulkDeleteHandler.HandleAsync(new BulkDeleteRecordsCommand(table.PublicId, ids, MaxRecords), ct);
        return new DeleteReportRecordsResult(totalCount, MaxRecords, false, true, ids.Count);
    }

    /// <summary>Walks the report page by page. Stops early once more than <see cref="MaxRecords"/> are known
    /// to match (the caller only needs to know it's over the limit, not to enumerate 100k ids).</summary>
    private async Task<(List<Guid> Ids, int TotalCount)> CollectMatchingIdsAsync(RunReportQuery query, CancellationToken ct)
    {
        var ids = new List<Guid>();
        var seen = new HashSet<Guid>();
        var totalCount = 0;

        for (var page = 1; ; page++)
        {
            var result = await _runHandler.HandleAsync(
                query with { Page = page, PageSize = RunPageSize, ClearGrouping = true, RuntimeGroupByFieldId = null }, ct);

            if (page == 1)
            {
                totalCount = result.TotalCount;
                if (totalCount > MaxRecords) return (ids, totalCount);
            }

            if (result.Items.Count == 0) break;
            foreach (var item in result.Items)
                if (seen.Add(item.Id)) ids.Add(item.Id);

            if (ids.Count >= totalCount) break;
        }

        return (ids, totalCount);
    }
}
