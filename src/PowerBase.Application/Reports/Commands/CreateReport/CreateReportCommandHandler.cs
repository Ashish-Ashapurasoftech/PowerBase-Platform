using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Commands.CreateReport;

public class CreateReportCommandHandler
{
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;
    private readonly CreateReportCommandValidator _validator;

    private static readonly HashSet<string> AllowedReportTypes = ["Table", "Summary", "GridEdit"];
    private static readonly HashSet<string> AllowedOperators = ["eq", "ne", "contains", "startsWith", "gt", "gte", "lt", "lte"];
    private static readonly HashSet<string> AllowedFunctions = ["Count", "Sum", "Avg", "Min", "Max"];

    public CreateReportCommandHandler(
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IReportRepository reportRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _reportRepo = reportRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
        _validator = new CreateReportCommandValidator();
    }

    public async Task<ReportDetailResult> HandleAsync(CreateReportCommand command, CancellationToken ct = default)
    {
        var validation = await _validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        if (!AllowedReportTypes.Contains(command.ReportType))
            throw new ValidationException(new Dictionary<string, string[]>
                { ["ReportType"] = [$"Report type must be one of: {string.Join(", ", AllowedReportTypes)}"] });

        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        IReadOnlyList<AppField> tableFields = [];
        var hasFilterTree = command.FilterTree?.Nodes.Count > 0;
        if (command.Columns.Count > 0 || hasFilterTree || command.GroupByFieldId.HasValue || command.Aggregations.Count > 0)
        {
            tableFields = await _fieldRepo.ListByTableAsync(table.Id, ct);
        }

        if (command.Columns.Count > 0)
        {
            var validIds = tableFields.Select(f => f.Id).ToHashSet();
            var invalid = command.Columns.Where(id => !validIds.Contains(id)).ToList();
            if (invalid.Count > 0)
                throw new ValidationException(
                    new Dictionary<string, string[]> { ["columns"] = [$"Unknown field IDs: {string.Join(", ", invalid)}"] });
        }

        // Validate filter tree operators (two-level walk)
        if (command.FilterTree is not null)
            ValidateFilterGroup(command.FilterTree, AllowedOperators);

        // Validate aggregations
        foreach (var agg in command.Aggregations)
        {
            if (!AllowedFunctions.Contains(agg.Function))
                throw new ValidationException(new Dictionary<string, string[]>
                    { ["aggregations"] = [$"Invalid function '{agg.Function}'. Allowed: {string.Join(", ", AllowedFunctions)}"] });
        }

        var definition = new ReportDefinition
        {
            Columns = command.Columns,
            SortFields = command.SortFields ?? [],
            FilterTree = command.FilterTree,
            GroupByFieldId = command.GroupByFieldId,
            GroupByMode = string.IsNullOrWhiteSpace(command.GroupByMode) ? "EqualValues" : command.GroupByMode,
            HideTotals = command.HideTotals,
            GroupDefaultCollapsed = command.GroupDefaultCollapsed,
            GroupByDescending = command.GroupByDescending,
            Aggregations = command.Aggregations.Select(a => new SummaryAggregation
            {
                FieldId = a.FieldId,
                Function = a.Function,
                DisplayAs = string.IsNullOrWhiteSpace(a.DisplayAs) ? "Normal" : a.DisplayAs,
            }).ToList(),
            DynamicFilterType = string.IsNullOrWhiteSpace(command.DynamicFilterType) ? "Default" : command.DynamicFilterType,
            CustomDynamicFilterFields = command.CustomDynamicFilterFields ?? [],
            AllowQuickSearch = command.AllowQuickSearch,
        };

        var report = new Report
        {
            TenantId = _queryContext.TenantId,
            AppTableId = table.Id,
            OwnerId = _queryContext.UserId,
            Name = command.Name,
            Description = command.Description,
            ReportType = command.ReportType,
            Visibility = command.Visibility,
            Definition = JsonSerializer.Serialize(definition),
            IsDefault = false,
            DisplayOrder = 0,
        };

        var (_, publicId) = await _reportRepo.CreateAsync(report, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.Report, publicId.ToString(), appId: table.AppId, ct: ct);

        return new ReportDetailResult
        {
            Id = publicId,
            Name = report.Name,
            Description = report.Description,
            ReportType = report.ReportType,
            Visibility = report.Visibility,
            Definition = definition,
            IsDefault = report.IsDefault,
            DisplayOrder = report.DisplayOrder,
            CreatedOn = DateTime.UtcNow,
        };
    }

    private static void ValidateFilterGroup(FilterGroup group, HashSet<string> allowedOperators)
    {
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } cond && !allowedOperators.Contains(cond.Operator))
                throw new ValidationException(new Dictionary<string, string[]>
                    { ["filterTree"] = [$"Invalid operator '{cond.Operator}'. Allowed: {string.Join(", ", allowedOperators)}"] });

            if (node.Group is { } sub)
                ValidateFilterGroup(sub, allowedOperators);
        }
    }
}
