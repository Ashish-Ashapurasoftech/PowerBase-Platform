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

        var rows = await _recordRepo.ListAsync(table, selectedFields, page, pageSize, ct);
        var total = await _recordRepo.CountAsync(table, ct);

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
}
