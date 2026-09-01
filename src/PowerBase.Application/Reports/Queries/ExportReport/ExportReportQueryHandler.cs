using System.Text;
using System.Text.Json;
using ClosedXML.Excel;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Records;
using PowerBase.Application.Reports;
using RunReport = PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports.Queries.ExportReport;

public class ExportReportQueryHandler
{
    private readonly IReportRepository _reportRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IRecordRepository _recordRepo;
    private readonly IRolePermissionEnforcer _enforcer;
    private readonly IUserRepository _userRepo;
    private readonly IFormulaProjector _formulaProjector;
    private readonly Relationships.IRelationalProjector _relationalProjector;
    private readonly IAzureSearchService _searchService;
    private readonly IQueryContext _queryContext;

    public ExportReportQueryHandler(
        IReportRepository reportRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        IRolePermissionEnforcer enforcer,
        IUserRepository userRepo,
        IFormulaProjector formulaProjector,
        Relationships.IRelationalProjector relationalProjector,
        IAzureSearchService searchService,
        IQueryContext queryContext)
    {
        _reportRepo = reportRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _recordRepo = recordRepo;
        _enforcer = enforcer;
        _userRepo = userRepo;
        _formulaProjector = formulaProjector;
        _relationalProjector = relationalProjector;
        _searchService = searchService;
        _queryContext = queryContext;
    }

    public async Task<ExportResult> HandleAsync(ExportReportQuery query, CancellationToken ct = default)
    {
        var report = await _reportRepo.GetByPublicIdAsync(query.ReportPublicId, ct);
        var table = await _tableRepo.GetByIdAsync(report.AppTableId, ct);
        var allFields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        var access = await _enforcer.GetTableAccessAsync(table, allFields, ct);
        if (!access.CanView)
            return BuildExport([], [], "export", query.Format);

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

        if (report.ReportType is "Summary" or "Chart")
            return await ExportSummaryAsync(table, allFields, access, definition, safeName, query.Format, ct);

        return await ExportTableAsync(table, allFields, access, definition, safeName, query.Format, filterTree, sortFields, ct);
    }

    private async Task<ExportResult> ExportTableAsync(
        AppTable table,
        IReadOnlyList<AppField> allFields,
        TableAccessContext access,
        ReportDefinition definition,
        string safeName,
        string format,
        FilterGroup? filterTree,
        IReadOnlyList<SortSpec> sortFields,
        CancellationToken ct)
    {
        var visibleFieldIds = access.VisibleFields.Where(f => f.Fid.HasValue).Select(f => (long)f.Fid!.Value).ToHashSet();
        IReadOnlyList<AppField> selectedFields;
        if (definition.Columns.Count > 0)
        {
            var fieldMap = allFields
                .Where(f => f.Fid.HasValue)
                .GroupBy(f => (long)f.Fid!.Value)
                .ToDictionary(g => g.Key, g => g.First());
            selectedFields = definition.Columns
                .Where(id => fieldMap.ContainsKey(id) && visibleFieldIds.Contains(id))
                .Select(id => fieldMap[id])
                .ToList();
        }
        else
        {
            selectedFields = allFields.Where(f => f.Fid.HasValue && f.IsReportable && visibleFieldIds.Contains((long)f.Fid!.Value)).ToList();
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

        if (filterTree != null && allFields.Any(f => f.IsSearchable || f.IsFilterable))
        {
            var odata = RunReport.OData.ODataFilterBuilder.Build(filterTree, allFields);
            if (!string.IsNullOrWhiteSpace(odata))
            {
                var aiMatches = await _searchService.SearchRecordsByFilterAsync(_queryContext.TenantId, table.Id, odata, ct);
                if (aiMatches.Count == 0)
                {
                    return BuildExport([], [], "export", format);
                }

                var matchedIds = await _recordRepo.GetIdsByPublicIdsAsync(table, aiMatches, ct);
                if (matchedIds.Count == 0)
                {
                    return BuildExport([], [], "export", format);
                }

                var aiSearchGroup = new FilterGroup
                {
                    Logic = "or",
                    Nodes = matchedIds.Select(id => new FilterNode 
                    {
                        Condition = new FilterCondition { FieldId = 3, Operator = "eq", Value = id.ToString() }
                    }).ToList()
                };
                
                filterTree = new FilterGroup { Logic = "and", Nodes = [new FilterNode { Group = filterTree }, new FilterNode { Group = aiSearchGroup }] };
            }
        }

        // Formula fields have no physical column — strip them from the SQL filter/sort and
        // apply them in-memory after projection (same approach as RunReportQueryHandler).
        var formulaFids = allFields
            .Where(f => (f.Fid.HasValue && FormulaTypeMap.IsComputedField(f.TypeCode, f.Settings)) || 
                        (f.IsEncrypted && f.Fid.HasValue))
            .Select(f => (long)f.Fid!.Value)
            .ToHashSet();

        var (physicalFilterTree, formulaConditions) = FormulaFilterSorter.SplitFilterTree(filterTree, formulaFids);
        var hasFormulaSorts = sortFields.Any(s => formulaFids.Contains(s.FieldId));
        IReadOnlyList<SortSpec> sqlSorts = hasFormulaSorts ? [] : sortFields;

        var rows = await _recordRepo.ListAsync(table, allFields, 1, 50_000, physicalFilterTree, sqlSorts,
            restrictToCreatedBy: access.RestrictToCreatedBy, ct: ct);
        var relational = await _relationalProjector.ProjectAsync(table, allFields, rows, ct);
        var computed = _formulaProjector.Project(allFields, rows, relational, table);
        var pairs = rows.Zip(computed, (r, c) => (Row: r, Computed: c)).ToList();

        if (formulaConditions != null)
            pairs = FormulaFilterSorter.ApplyFormulaFilters(pairs, formulaConditions, allFields);
        if (hasFormulaSorts)
            pairs = FormulaFilterSorter.ApplySort(pairs, sortFields, allFields);

        var userNames = await RunReport.RunReportQueryHandler.ResolveUserNamesAsync(pairs.Select(p => p.Row), allFields, _userRepo, ct);
        var items = pairs.Select(p => RecordResult.FromRow(p.Row, selectedFields, userNames, p.Computed)).ToList();

        var columns = selectedFields.Select(f => new ColumnInfo((f.Fid ?? f.Id).ToString(), string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label, f.TypeCode)).ToList();
        return BuildExport(columns, items.Select(r => r.Fields).ToList(), safeName, format);
    }

    private async Task<ExportResult> ExportSummaryAsync(
        AppTable table,
        IReadOnlyList<AppField> allFields,
        TableAccessContext access,
        ReportDefinition definition,
        string safeName,
        string format,
        CancellationToken ct)
    {
        if (!definition.GroupByFieldId.HasValue)
            return BuildExport([], [], safeName, format);

        var visibleFieldIds = access.VisibleFields.Where(f => f.Fid.HasValue).Select(f => (long)f.Fid!.Value).ToHashSet();
        var fieldMap = allFields
            .Where(f => f.Fid.HasValue)
            .GroupBy(f => (long)f.Fid!.Value)
            .ToDictionary(g => g.Key, g => g.First());
        if (!fieldMap.TryGetValue(definition.GroupByFieldId.Value, out var groupByField) || !visibleFieldIds.Contains(definition.GroupByFieldId.Value))
            return BuildExport([], [], safeName, format);

        var visibleAggregations = definition.Aggregations
            .Where(a => visibleFieldIds.Contains(a.FieldId))
            .ToList();

        AppField? seriesField = null;
        if (definition.Chart?.SeriesFieldId is { } seriesFieldId
            && fieldMap.TryGetValue(seriesFieldId, out var resolvedSeriesField)
            && visibleFieldIds.Contains(seriesFieldId))
        {
            seriesField = resolvedSeriesField;
        }

        var rows = await _recordRepo.SummarizeAsync(
            table, groupByField, visibleAggregations, allFields, definition.GroupByMode,
            filterTree: access.ViewFilter, restrictToCreatedBy: access.RestrictToCreatedBy,
            seriesField: seriesField, seriesMode: definition.Chart?.SeriesMode ?? "EqualValues", ct: ct);

        // Build alias→unique-key map and percent set (same logic as RunSummaryAsync — see its
        // comment: a user can aggregate the SAME field with several different functions, e.g.
        // Sum and Avg of Amount, so keying by FieldId alone collapses those columns together).
        var aggAliasToKey = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var percentAliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var columnTotals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);

        for (var i = 0; i < visibleAggregations.Count; i++)
        {
            var agg = visibleAggregations[i];
            if (!fieldMap.TryGetValue(agg.FieldId, out var aggField)) continue;
            var alias = $"{agg.Function}_{aggField.Name.Replace(" ", "_")}";
            aggAliasToKey[alias] = $"agg{i}_{agg.FieldId}";
            if (agg.DisplayAs == "PercentOfColumnTotal")
            {
                percentAliases.Add(alias);
                columnTotals[alias] = rows.Sum(row =>
                    row.TryGetValue(alias, out var v) ? Convert.ToDouble(v ?? 0) : 0.0);
            }
        }

        var groupKey = (groupByField.Fid ?? groupByField.Id).ToString();
        var seriesKey = seriesField is not null ? (seriesField.Fid ?? seriesField.Id).ToString() : null;
        var columns = new List<ColumnInfo>
        {
            new(groupKey, string.IsNullOrWhiteSpace(groupByField.Label) ? groupByField.Name : groupByField.Label),
            new("0", "Count"),
        };
        if (seriesKey is not null)
        {
            columns.Add(new ColumnInfo(seriesKey, string.IsNullOrWhiteSpace(seriesField!.Label) ? seriesField.Name : seriesField.Label));
        }
        for (var i = 0; i < visibleAggregations.Count; i++)
        {
            var agg = visibleAggregations[i];
            if (fieldMap.TryGetValue(agg.FieldId, out var aggField))
            {
                var fieldName = string.IsNullOrWhiteSpace(aggField.Label) ? aggField.Name : aggField.Label;
                var label = agg.DisplayAs == "PercentOfColumnTotal"
                    ? $"{agg.Function} of {fieldName} (%)"
                    : $"{agg.Function} of {fieldName}";
                columns.Add(new ColumnInfo($"agg{i}_{agg.FieldId}", label));
            }
        }

        var dataRows = rows.Select(row =>
        {
            var fields = new Dictionary<string, object?>();
            fields[groupKey] = row.TryGetValue("GroupValue", out var gv) ? gv : null;
            fields["0"] = row.TryGetValue("Count", out var cnt) ? cnt : null;
            if (seriesKey is not null)
                fields[seriesKey] = row.TryGetValue("SeriesValue", out var sv) ? sv : null;
            foreach (var (alias, key) in aggAliasToKey)
            {
                if (!row.TryGetValue(alias, out var val)) continue;
                if (percentAliases.Contains(alias) && columnTotals.TryGetValue(alias, out var total) && total != 0)
                    fields[key] = Math.Round(Convert.ToDouble(val ?? 0) / total * 100, 2);
                else
                    fields[key] = val;
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
                EscapeCsvField(FormatFieldValue(row.TryGetValue(c.Key, out var v) ? v : null, c.TypeCode)))));
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
                var formatted = FormatFieldValue(raw, columns[ci].TypeCode);
                ws.Cell(ri + 2, ci + 1).Value = formatted is null ? XLCellValue.FromObject(string.Empty) : XLCellValue.FromObject(formatted);
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

    private static string? FormatFieldValue(object? value, string typeCode)
    {
        if (value is null) return null;

        if (typeCode is "DateRange" or "NumericRange")
        {
            string? start = null, end = null;
            if (value is System.Collections.IDictionary dict)
            {
                start = dict["start"]?.ToString();
                end   = dict["end"]?.ToString();
            }
            else if (value is string s)
            {
                try
                {
                    var je = JsonSerializer.Deserialize<JsonElement>(s);
                    start = je.TryGetProperty("start", out var sv) ? sv.GetString() : null;
                    end   = je.TryGetProperty("end",   out var ev) ? ev.GetString() : null;
                }
                catch { }
            }
            if (string.IsNullOrEmpty(start) && string.IsNullOrEmpty(end)) return null;
            // Strip time component from date strings
            if (typeCode == "DateRange")
            {
                start = start?.Split('T')[0];
                end   = end?.Split('T')[0];
            }
            return $"{start ?? "…"} – {end ?? "…"}";
        }

        return value.ToString();
    }

    private record ColumnInfo(string Key, string Name, string TypeCode = "");
}
