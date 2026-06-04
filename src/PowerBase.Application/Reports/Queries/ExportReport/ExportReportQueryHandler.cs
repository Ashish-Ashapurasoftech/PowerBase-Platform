using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports.Queries.ExportReport;

public class ExportReportQueryHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;

    public ExportReportQueryHandler(
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

    public async Task<ExportResult> HandleAsync(ExportReportQuery query, CancellationToken ct = default)
    {
        var report = await _reportRepo.GetByPublicIdAsync(query.ReportPublicId, ct);
        var table = await _tableRepo.GetByIdAsync(report.AppTableId, ct);
        var allFields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        var definition = JsonSerializer.Deserialize<ReportDefinition>(report.Definition) ?? new ReportDefinition();

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

        IReadOnlyList<SortSpec> sortFields = definition.SortFields.Count > 0
            ? definition.SortFields
            : (definition.SortFieldId.HasValue
                ? [new SortSpec { FieldId = definition.SortFieldId.Value, Desc = definition.SortDesc }]
                : []);

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

        var safeName = string.Concat(report.Name.Split(Path.GetInvalidFileNameChars()));

        if (report.ReportType == "Summary")
            return await ExportSummaryAsync(table, allFields, definition, safeName, query.Format, ct);

        return await ExportTableAsync(table, allFields, definition, safeName, query.Format, filterTree, sortFields, ct);
    }

    private async Task<ExportResult> ExportTableAsync(
        AppTable table,
        IReadOnlyList<AppField> allFields,
        ReportDefinition definition,
        string safeName,
        string format,
        FilterGroup? filterTree,
        IReadOnlyList<SortSpec> sortFields,
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

        var rows = await _recordRepo.ListAsync(table, selectedFields, 1, 50_000, filterTree, sortFields, ct: ct);
        var items = rows.Select(row => RecordResult.FromRow(row, selectedFields)).ToList();

        var columns = selectedFields.Select(f => new ColumnInfo(f.Id, f.Name)).ToList();
        return BuildExport(columns, items.Select(r => r.Fields).ToList(), safeName, format);
    }

    private async Task<ExportResult> ExportSummaryAsync(
        AppTable table,
        IReadOnlyList<AppField> allFields,
        ReportDefinition definition,
        string safeName,
        string format,
        CancellationToken ct)
    {
        if (!definition.GroupByFieldId.HasValue)
            return BuildExport([], [], safeName, format);

        var fieldMap = allFields.ToDictionary(f => f.Id);
        if (!fieldMap.TryGetValue(definition.GroupByFieldId.Value, out var groupByField))
            return BuildExport([], [], safeName, format);

        var rows = await _recordRepo.SummarizeAsync(
            table, groupByField, definition.Aggregations, allFields, definition.GroupByMode, ct);

        // Build alias→fieldId map and percent set (same logic as RunSummaryAsync)
        var aggAliasToFieldId = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var percentAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columnTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        foreach (var agg in definition.Aggregations)
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

        var columns = new List<ColumnInfo>
        {
            new(groupByField.Id, groupByField.Name),
            new(0, "Count"),
        };
        foreach (var agg in definition.Aggregations)
        {
            if (fieldMap.TryGetValue(agg.FieldId, out var aggField))
            {
                var label = agg.DisplayAs == "PercentOfColumnTotal"
                    ? $"{agg.Function} of {aggField.Name} (%)"
                    : $"{agg.Function} of {aggField.Name}";
                columns.Add(new ColumnInfo(aggField.Id, label));
            }
        }

        var dataRows = rows.Select(row =>
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
            return fields;
        }).ToList();

        return BuildExport(columns, dataRows, safeName, format);
    }

    private static ExportResult BuildExport(
        List<ColumnInfo> columns,
        List<Dictionary<string, object?>> rows,
        string safeName,
        string format)
    {
        return format == "xlsx"
            ? BuildXlsx(columns, rows, safeName)
            : BuildCsv(columns, rows, safeName);
    }

    private static ExportResult BuildCsv(
        List<ColumnInfo> columns,
        List<Dictionary<string, object?>> rows,
        string safeName)
    {
        var sb = new StringBuilder();
        sb.AppendLine(string.Join(",", columns.Select(c => EscapeCsvField(c.Name))));
        foreach (var row in rows)
        {
            sb.AppendLine(string.Join(",", columns.Select(c =>
                EscapeCsvField(row.TryGetValue(c.Key, out var v) ? v?.ToString() : null))));
        }
        return new ExportResult
        {
            Content = Encoding.UTF8.GetBytes(sb.ToString()),
            ContentType = "text/csv",
            FileName = $"{safeName}.csv",
        };
    }

    private static ExportResult BuildXlsx(
        List<ColumnInfo> columns,
        List<Dictionary<string, object?>> rows,
        string safeName)
    {
        using var wb = new XLWorkbook();
        var ws = wb.Worksheets.Add("Report");

        for (var ci = 0; ci < columns.Count; ci++)
        {
            var cell = ws.Cell(1, ci + 1);
            cell.Value = columns[ci].Name;
            cell.Style.Font.Bold = true;
        }

        for (var ri = 0; ri < rows.Count; ri++)
        {
            for (var ci = 0; ci < columns.Count; ci++)
            {
                var raw = rows[ri].TryGetValue(columns[ci].Key, out var v) ? v : null;
                ws.Cell(ri + 2, ci + 1).Value = raw is null ? XLCellValue.FromObject(string.Empty) : XLCellValue.FromObject(raw);
            }
        }

        ws.Columns().AdjustToContents();

        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return new ExportResult
        {
            Content = ms.ToArray(),
            ContentType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            FileName = $"{safeName}.xlsx",
        };
    }

    private static string EscapeCsvField(string? value)
    {
        if (value is null) return string.Empty;
        if (value.Contains(',') || value.Contains('"') || value.Contains('\n'))
            return $"\"{value.Replace("\"", "\"\"")}\"";
        return value;
    }

    private record ColumnInfo(long Id, string Name)
    {
        public string Key => Id.ToString();
    }
}
