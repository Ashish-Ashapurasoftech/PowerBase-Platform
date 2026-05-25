using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

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

    public RunReportQueryHandler(
        IReportRepository reportRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo)
    {
        _reportRepo = reportRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
    }

    public async Task<PagedReportRunResult> HandleAsync(RunReportQuery query, CancellationToken ct = default)
    {
        var page = Math.Max(1, query.Page);
        var pageSize = Math.Clamp(query.PageSize, 1, 200);

        var report = await _reportRepo.GetByPublicIdAsync(query.ReportPublicId, ct);
        var table = await _tableRepo.GetByIdAsync(report.AppTableId, ct);
        var allFields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        var definition = JsonSerializer.Deserialize<ReportDefinition>(report.Definition) ?? new ReportDefinition();

        if (report.ReportType == "Summary")
            return await RunSummaryAsync(table, allFields, definition, page, pageSize, ct);

        return await RunTableAsync(table, allFields, definition, page, pageSize, query.RuntimeFilters, ct);
    }

    private async Task<PagedReportRunResult> RunTableAsync(
        AppTable table,
        IReadOnlyList<AppField> allFields,
        ReportDefinition definition,
        int page, int pageSize,
        IReadOnlyList<(long FieldId, string Value)>? runtimeFilters,
        CancellationToken ct)
    {
        IReadOnlyList<AppField> selectedFields;
        if (definition.Columns.Count > 0)
        {
            var fieldMap = allFields.ToDictionary(f => f.Id);
            selectedFields = definition.Columns
                .Where(id => fieldMap.ContainsKey(id))
                .Select(id => fieldMap[id])
                .ToList();
        }
        else
        {
            selectedFields = allFields.Where(f => f.IsReportable).ToList();
        }

        var filters = definition.Filters.ToList();
        if (runtimeFilters?.Count > 0)
        {
            filters.AddRange(runtimeFilters.Select(rf => new ReportFilter
            {
                FieldId = rf.FieldId,
                Operator = "contains",
                Value = rf.Value,
            }));
        }
        var filterList = filters.Count > 0 ? filters : null;

        var rows = await _recordRepo.ListAsync(table, selectedFields, page, pageSize, filterList, ct);
        var total = await _recordRepo.CountAsync(table, filterList, ct);

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
        ReportDefinition definition,
        int page, int pageSize,
        CancellationToken ct)
    {
        if (!definition.GroupByFieldId.HasValue)
        {
            // No group-by configured — return empty result
            return new PagedReportRunResult { Page = page, PageSize = pageSize };
        }

        var fieldMap = allFields.ToDictionary(f => f.Id);
        if (!fieldMap.TryGetValue(definition.GroupByFieldId.Value, out var groupByField))
        {
            return new PagedReportRunResult { Page = page, PageSize = pageSize };
        }

        var rows = await _recordRepo.SummarizeAsync(table, groupByField, definition.Aggregations, allFields, ct);

        // Treat summary rows as record-like dictionaries; map them to RecordResult
        var items = rows.Select(row => new RecordResult
        {
            Id = Guid.Empty,
            CreatedOn = DateTime.UtcNow,
            Fields = row.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
        }).ToList();

        // Build synthetic columns: GroupValue + one per aggregation
        var columns = new List<ReportColumnInfo>
        {
            new() { FieldId = groupByField.Id, Name = groupByField.Name + " (Group)", TypeCode = groupByField.TypeCode },
            new() { FieldId = 0, Name = "Count", TypeCode = "Number" },
        };
        foreach (var agg in definition.Aggregations)
        {
            if (fieldMap.TryGetValue(agg.FieldId, out var aggField))
            {
                columns.Add(new ReportColumnInfo
                {
                    FieldId = aggField.Id,
                    Name = $"{agg.Function} of {aggField.Name}",
                    TypeCode = "Number",
                });
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
