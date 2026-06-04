using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Reports.Queries.RunReport;

public class ReportColumnInfo
{
    public long FieldId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string TypeCode { get; init; } = string.Empty;
}

public class PagedReportRunResult
{
    public IReadOnlyList<RecordResult> Items { get; init; } = [];
    public IReadOnlyList<ReportColumnInfo> Columns { get; init; } = [];
    public int TotalCount { get; init; }
    public int Page { get; init; }
    public int PageSize { get; init; }
}

public class RunReportQueryHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;

    public RunReportQueryHandler(
        IReportRepository reportRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer)
    {
        _reportRepo = reportRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
    }

    public async Task<PagedReportRunResult> HandleAsync(RunReportQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var report = await _reportRepo.GetVisibleReportAsync(query.ReportPublicId, ct)
            ?? throw new NotFoundException("Report", query.ReportPublicId);
        var table = await _tableRepo.GetByIdAsync(report.AppTableId, ct);
        var allFields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        var access = await _enforcer.GetTableAccessAsync(table, allFields, ct);
        if (!access.CanView)
            return new PagedReportRunResult { Page = page, PageSize = pageSize };

        var definition = JsonSerializer.Deserialize<ReportDefinition>(report.Definition) ?? new ReportDefinition();

        // Resolve filter tree — support legacy flat Filters list
        var filterTree = definition.FilterTree;
        if (filterTree == null && definition.Filters.Count > 0)
        {
            filterTree = new FilterGroup
            {
                Logic = "and",
                Nodes = definition.Filters.Select(f => new FilterNode
                {
                    Condition = new FilterCondition { FieldId = f.FieldId, Operator = f.Operator, Value = f.Value }
                }).ToList()
            };
        }

        // Resolve sort fields — support legacy SortFieldId/SortDesc
        IReadOnlyList<SortSpec> sortFields = definition.SortFields.Count > 0
            ? definition.SortFields
            : (definition.SortFieldId.HasValue
                ? [new SortSpec { FieldId = definition.SortFieldId.Value, Desc = definition.SortDesc }]
                : []);

        // For grouped Table reports: prepend group field as primary sort key so records
        // of the same group are contiguous — the frontend groups the flat result visually.
        if (report.ReportType != "Summary" && definition.GroupByFieldId.HasValue)
        {
            var gfId = definition.GroupByFieldId.Value;
            var list = sortFields.ToList();
            if (list.Count == 0 || list[0].FieldId != gfId)
            {
                var without = list.Where(s => s.FieldId != gfId).ToList();
                sortFields = new[] { new SortSpec { FieldId = gfId, Desc = definition.GroupByDescending } }
                    .Concat(without)
                    .ToArray();
            }
        }

        if (report.ReportType == "Summary")
            return await RunSummaryAsync(table, allFields, access, definition, page, pageSize, ct);

        return await RunTableAsync(table, allFields, access, definition, page, pageSize, filterTree, sortFields, query.RuntimeFilters, ct);
    }

    private async Task<PagedReportRunResult> RunTableAsync(
        AppTable table,
        IReadOnlyList<AppField> allFields,
        TableAccessContext access,
        ReportDefinition definition,
        int page, int pageSize,
        FilterGroup? filterTree,
        IReadOnlyList<SortSpec> sortFields,
        IReadOnlyList<(long FieldId, string Value)>? runtimeFilters,
        CancellationToken ct)
    {
        // Intersect report columns with fields the role can see (drop None-access fields)
        var visibleFieldIds = access.VisibleFields.Select(f => f.Id).ToHashSet();
        IReadOnlyList<AppField> selectedFields;
        if (definition.Columns.Count > 0)
        {
            var fieldMap = allFields.ToDictionary(f => f.Id);
            selectedFields = definition.Columns
                .Where(id => fieldMap.ContainsKey(id) && visibleFieldIds.Contains(id))
                .Select(id => fieldMap[id])
                .ToList();
        }
        else
        {
            selectedFields = allFields.Where(f => f.IsReportable && visibleFieldIds.Contains(f.Id)).ToList();
        }

        // Merge role record filter into the report's filter tree
        if (access.ViewFilter != null)
        {
            filterTree = filterTree == null
                ? access.ViewFilter
                : new FilterGroup
                {
                    Logic = "and",
                    Nodes = [new FilterNode { Group = filterTree }, new FilterNode { Group = access.ViewFilter }]
                };
        }

        // Merge runtime filters (dynamic/quick-search) into the filter tree
        if (runtimeFilters?.Count > 0)
        {
            var runtimeNodes = runtimeFilters.Select(rf => new FilterNode
            {
                Condition = new FilterCondition { FieldId = rf.FieldId, Operator = "contains", Value = rf.Value }
            }).ToList();

            filterTree = filterTree == null
                ? new FilterGroup { Logic = "and", Nodes = runtimeNodes }
                : new FilterGroup
                {
                    Logic = "and",
                    Nodes = [new FilterNode { Group = filterTree }, .. runtimeNodes]
                };
        }

        var rows = await _recordRepo.ListAsync(table, selectedFields, page, pageSize, filterTree, sortFields,
            restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);
        var total = await _recordRepo.CountAsync(table, filterTree, restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);

        var items = rows.Select(row => RecordResult.FromRow(row, selectedFields)).ToList();
        var columns = selectedFields.Select(f => new ReportColumnInfo
        {
            FieldId = f.Id,
            Name = f.Name,
            TypeCode = f.TypeCode,
        }).ToList();

        return new PagedReportRunResult
        {
            Items = items,
            Columns = columns,
            TotalCount = total,
            Page = page,
            PageSize = pageSize,
        };
    }

    private async Task<PagedReportRunResult> RunSummaryAsync(
        AppTable table,
        IReadOnlyList<AppField> allFields,
        TableAccessContext access,
        ReportDefinition definition,
        int page, int pageSize,
        CancellationToken ct)
    {
        if (!definition.GroupByFieldId.HasValue)
        {
            // No group-by configured — return empty result
            return new PagedReportRunResult { Page = page, PageSize = pageSize };
        }

        var visibleFieldIds = access.VisibleFields.Select(f => f.Id).ToHashSet();
        var fieldMap = allFields.ToDictionary(f => f.Id);

        // If the group-by field is hidden, cannot produce a meaningful summary
        if (!fieldMap.TryGetValue(definition.GroupByFieldId.Value, out var groupByField) || !visibleFieldIds.Contains(groupByField.Id))
        {
            return new PagedReportRunResult { Page = page, PageSize = pageSize };
        }

        // Only aggregate visible fields
        var visibleAggregations = definition.Aggregations
            .Where(a => visibleFieldIds.Contains(a.FieldId))
            .ToList();

        var rows = await _recordRepo.SummarizeAsync(
            table, groupByField, visibleAggregations, allFields, definition.GroupByMode,
            filterTree: access.ViewFilter, restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);

        // Build alias→fieldId map. Identify columns displayed as percent-of-total and compute their totals.
        var aggAliasToFieldId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var percentAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columnTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var agg in visibleAggregations)
        {
            if (!fieldMap.TryGetValue(agg.FieldId, out var aggField)) continue;
            var alias = $"{agg.Function}_{aggField.Name.Replace(" ", "_")}";
            aggAliasToFieldId[alias] = agg.FieldId.ToString();
            if (agg.DisplayAs == "PercentOfColumnTotal")
            {
                percentAliases.Add(alias);
                columnTotals[alias] = rows.Sum(row =>
                    row.TryGetValue(alias, out var v) ? Convert.ToDouble(v ?? 0) : 0.0);
            }
        }

        // Remap SQL alias keys to field-ID string keys; apply percent transform where configured
        var items = rows.Select(row =>
        {
            var fields = new Dictionary<string, object?>();
            fields[groupByField.Id.ToString()] = row.TryGetValue("GroupValue", out var gv) ? gv : null;
            fields["0"] = row.TryGetValue("Count", out var cnt) ? cnt : null;
            foreach (var (alias, fieldId) in aggAliasToFieldId)
            {
                if (!row.TryGetValue(alias, out var val)) continue;
                if (percentAliases.Contains(alias) && columnTotals.TryGetValue(alias, out var total) && total != 0)
                    fields[fieldId] = Math.Round(Convert.ToDouble(val ?? 0) / total * 100, 2);
                else
                    fields[fieldId] = val;
            }
            return new RecordResult { Id = Guid.Empty, CreatedOn = DateTime.UtcNow, Fields = fields };
        }).ToList();

        // Synthetic columns: group-by field + Count + one per visible aggregation
        var columns = new List<ReportColumnInfo>
        {
            new() { FieldId = groupByField.Id, Name = groupByField.Name, TypeCode = groupByField.TypeCode },
            new() { FieldId = 0, Name = "Count", TypeCode = "Number" },
        };
        foreach (var agg in visibleAggregations)
        {
            if (fieldMap.TryGetValue(agg.FieldId, out var aggField))
            {
                var label = agg.DisplayAs == "PercentOfColumnTotal"
                    ? $"{agg.Function} of {aggField.Name} (%)"
                    : $"{agg.Function} of {aggField.Name}";
                columns.Add(new ReportColumnInfo { FieldId = aggField.Id, Name = label, TypeCode = "Number" });
            }
        }

        return new PagedReportRunResult
        {
            Items = items,
            Columns = columns,
            TotalCount = rows.Count,
            Page = 1,
            PageSize = rows.Count > 0 ? rows.Count : pageSize,
        };
    }
}
