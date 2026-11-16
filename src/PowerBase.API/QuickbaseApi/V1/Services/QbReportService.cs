using System.Text.RegularExpressions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Models;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.GetReport;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.API.QuickbaseApi.V1.Services;

/// <summary>Quickbase getReport / runReport on top of our existing report handlers (called, never modified).</summary>
public sealed class QbReportService
{
    /// <summary>Our run handler pages by at most 200 rows; larger Quickbase "top" values are served by several calls.</summary>
    public const int HANDLER_PAGE_SIZE = 200;
    /// <summary>Rows returned when the caller sends no "top" (Quickbase: "intelligent pagination").</summary>
    public const int DEFAULT_TOP = 1000;
    /// <summary>Hard ceiling per request so a response stays well under the 10 MB PowerFlows limit.</summary>
    public const int MAX_TOP = 10000;

    private readonly IReportRepository _reports;
    private readonly IAppTableRepository _tables;
    private readonly IAppFieldRepository _fields;
    private readonly IUserRepository _users;
    private readonly IAppAccessService _access;
    private readonly GetReportQueryHandler _getReport;
    private readonly RunReportQueryHandler _runReport;
    private readonly QbUserValueResolver _userValues;
    private readonly IAppRepository _apps;
    private readonly QbReferenceTypes _referenceTypes;

    public QbReportService(IReportRepository reports, IAppTableRepository tables, IAppFieldRepository fields, IUserRepository users,
        IAppAccessService access, GetReportQueryHandler getReport, RunReportQueryHandler runReport, QbUserValueResolver userValues, IAppRepository apps, QbReferenceTypes referenceTypes)
    {
        _referenceTypes = referenceTypes;
        _apps = apps;
        _userValues = userValues;
        _reports = reports;
        _tables = tables;
        _fields = fields;
        _users = users;
        _access = access;
        _getReport = getReport;
        _runReport = runReport;
    }

    private async Task<(Report Report, AppTable Table)> LoadAsync(Guid reportId, Guid tableId, string permission, CancellationToken ct)
    {
        if (permission == PermissionCodes.ReportsRead)
            await _access.RequirePermissionByReportPublicIdAsync(reportId, permission, ct);
        else
            await _access.RequireMembershipByReportPublicIdAsync(reportId, ct);
        var report = await _reports.GetVisibleReportAsync(reportId, ct)
            ?? throw QbApiException.NotFound($"Report with Id \"{reportId}\" not found.");
        var table = await _tables.GetByIdAsync(report.AppTableId, ct);
        if (table.PublicId != tableId)
            throw QbApiException.NotFound($"Report with Id \"{reportId}\" not found.");
        return (report, table);
    }

    public async Task<QbReportResponse> GetReportAsync(Guid reportId, Guid tableId, CancellationToken ct)
    {
        var (report, table) = await LoadAsync(reportId, tableId, PermissionCodes.ReportsRead, ct);
        var detail = await _getReport.HandleAsync(new GetReportQuery(reportId), ct);
        var def = detail.Definition;
        var fields = await _fields.ListByTableAsync(table.Id, ct);
        var byFid = fields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());

        string? ownerId = null;
        if (string.Equals(report.Visibility, "Personal", StringComparison.OrdinalIgnoreCase) && report.OwnerId > 0)
        {
            var ids = await _users.GetPublicIdsByIdsAsync(new[] { report.OwnerId }, ct);
            ownerId = ids.TryGetValue(report.OwnerId, out var g) ? QbIds.Format(g) : null;
        }

        var type = QbReportType(report.ReportType, def);
        return new QbReportResponse
        {
            Id = QbIds.Format(report.PublicId),
            Name = report.Name,
            Type = type,
            Description = report.Description ?? "",
            Incomplete = false,
            OwnerId = ownerId,
            Query = new QbReportQuery
            {
                TableId = QbIds.Format(table.PublicId),
                Filter = QbQuerySerializer.Serialize(def.FilterTree,
                    fid => byFid.TryGetValue(fid, out var f) && QbFieldPropertiesBuilder.TypeOf(f).FieldType is "date" or "timestamp") ?? "",
                FormulaFields = def.ReportFormulas.Select(f => new QbFormulaField(
                    f.Id, f.Name, QbFieldTypeMap.Map(f.TypeCode).FieldType, QbFieldPropertiesBuilder.QbFormula(f.Expression) ?? "", null)).ToList(),
                Fields = type != "table" ? null
                    : def.Columns.Count > 0 ? def.Columns.ToList()
                    // "Default" columns: the report shows every field the caller can see, so list what a run returns.
                    : (await _runReport.HandleAsync(new RunReportQuery(reportId, 1, 1), ct)).Columns.Select(c => c.FieldId).ToList(),
                SortBy = type != "table" ? null : SortBy(report.ReportType, def),
                GroupBy = type == "chart" ? null : GroupBy(report.ReportType, def),
            },
            Properties = Properties(type, def),
            UsedLast = null,
            UsedCount = null,
        };
    }

    public static string QbReportType(string reportType, ReportDefinition def) => reportType switch
    {
        "Table" => "table",
        "Summary" => "summary",
        "Chart" => "chart", // Quickbase report types: table, summary, chart, calendar, timeline, map, gedit
        var other => Kebab(other),
    };

    private static IReadOnlyList<QbSortBy> SortBy(string reportType, ReportDefinition def)
    {
        if (reportType == "Table" && def.TableSortGroup.Count > 0)
            return def.TableSortGroup.Select(l => new QbSortBy(l.FieldId, l.Desc ? "DESC" : "ASC")).ToList();
        if (def.SortFields.Count > 0)
            return def.SortFields.Select(s => new QbSortBy(s.FieldId, s.Desc ? "DESC" : "ASC")).ToList();
        if (def.SortFieldId is { } sid)
            return new[] { new QbSortBy(sid, def.SortDesc ? "DESC" : "ASC") };
        return Array.Empty<QbSortBy>();
    }

    private static IReadOnlyList<QbGroupBy> GroupBy(string reportType, ReportDefinition def)
    {
        if (reportType == "Table" && def.TableSortGroup.Any(l => l.IsGroup))
            return def.TableSortGroup.Where(l => l.IsGroup).Select(l => new QbGroupBy(l.FieldId, Grouping(l.GroupByMode))).ToList();
        if (def.RowGroupLevels.Count > 0)
            return def.RowGroupLevels.Select(l => new QbGroupBy(l.FieldId, Grouping(l.GroupByMode))).ToList();
        if (def.GroupByFieldId is { } gid)
            return new[] { new QbGroupBy(gid, Grouping(def.GroupByMode)) };
        return Array.Empty<QbGroupBy>();
    }

    /// <summary>Quickbase groupBy "grouping" values: equal-values, first-word, first-letter, day, week, month, quarter, year,
    /// decade, hour, minute, and numeric ranges written as the range size ("1", "10", "100", "1000", ...).</summary>
    public static string Grouping(string? mode) => mode switch
    {
        null or "" => "equal-values",
        var m when m.StartsWith("Increment", StringComparison.OrdinalIgnoreCase) => m["Increment".Length..],
        var m => Kebab(m),
    };

    private static IDictionary<string, object?> Properties(string type, ReportDefinition def)
    {
        if (type == "table")
            return new Dictionary<string, object?>
            {
                ["displayOnlyNewOrChangedRecords"] = false,
                ["showRowNumbers"] = true,
            };
        if (type == "summary")
        {
            var summary = new Dictionary<string, object?>
            {
                ["summarize"] = def.Aggregations.Select(a => (object)(a.Function.Equals("Count", StringComparison.OrdinalIgnoreCase) && a.FieldId == 0
                    ? new Dictionary<string, object?> { ["type"] = "number-of-records", ["showAs"] = 0 }
                    : new Dictionary<string, object?> { ["type"] = "field-value", ["fieldId"] = a.FieldId, ["showAs"] = 0,
                        ["aggregation"] = QbFieldPropertiesBuilder.SummaryFunction(a.Function) })).ToList(),
                ["summaryVariables"] = def.SummaryVariables.Select(v => new Dictionary<string, object?>
                    { ["fieldId"] = v.FieldId, ["label"] = v.Name, ["aggregation"] = QbFieldPropertiesBuilder.SummaryFunction(v.Function) }).ToList(),
                ["summaryFormulas"] = def.SummaryFormulas.Select(f => new Dictionary<string, object?>
                    { ["id"] = f.Id, ["label"] = f.Name, ["fieldType"] = QbFieldTypeMap.Map(f.TypeCode).FieldType, ["formula"] = QbFieldPropertiesBuilder.QbFormula(f.Expression) }).ToList(),

            };
            // Quickbase lists the summary sort only when the report has one; crosstabs only when one is set (we have none).
            if (def.SummarySortFields.Count > 0)
                summary["sortBy"] = def.SummarySortFields.Select(s => new Dictionary<string, object?>
                {
                    ["order"] = s.Desc ? "DESC" : "ASC",
                    ["summarizationElementIndex"] = s.LevelIndex,
                    ["by"] = s.Target.Equals("RowLevel", StringComparison.OrdinalIgnoreCase) ? "groups" : "summarization",
                }).ToList();
            return summary;
        }
        if (type == "chart") return ChartProperties(def);
        return new Dictionary<string, object?>();
    }

    /// <summary>Chart report properties in Quickbase's shape: chartType, categories, dataLabel, dataSources, sortBy.</summary>
    private static IDictionary<string, object?> ChartProperties(ReportDefinition def)
    {
        var category = def.RowGroupLevels.Count > 0 ? def.RowGroupLevels[0].FieldId : def.GroupByFieldId;
        var mode = def.RowGroupLevels.Count > 0 ? def.RowGroupLevels[0].GroupByMode : def.GroupByMode;
        var p = new Dictionary<string, object?>
        {
            ["chartType"] = Kebab(def.Chart?.ChartType ?? "bar"),
            ["dataLabel"] = "",
            ["dataSources"] = def.Aggregations.Select(a => (object)(a.FieldId == 0 && a.Function.Equals("Count", StringComparison.OrdinalIgnoreCase)
                ? new Dictionary<string, object?> { ["type"] = "number-of-records" }
                : new Dictionary<string, object?> { ["aggregation"] = QbFieldPropertiesBuilder.SummaryFunction(a.Function), ["fieldId"] = a.FieldId, ["type"] = "field-value" })).ToList(),
            ["sortBy"] = new List<object> { new Dictionary<string, object?> {
                ["by"] = (def.Chart?.SortBy ?? "Labels").Equals("Values", StringComparison.OrdinalIgnoreCase) ? "values" : "categories",
                ["order"] = (def.Chart?.SortDirection ?? "Asc").Equals("Desc", StringComparison.OrdinalIgnoreCase) ? "DESC" : "ASC" } },
        };
        if (category is { } cat)
            p["categories"] = new Dictionary<string, object?> { ["fieldId"] = cat, ["grouping"] = Grouping(mode), ["label"] = def.Chart?.AxisLabelX ?? "" };
        return p;
    }

    /// <summary>runReport. Quickbase skip/top are mapped onto our page-based handler.</summary>
    public async Task<QbRunReportResponse> RunReportAsync(Guid reportId, Guid tableId, int? skip, int? top, CancellationToken ct)
    {
        var (report, table) = await LoadAsync(reportId, tableId, "member", ct);
        var start = skip ?? 0;
        var take = Math.Min(top ?? DEFAULT_TOP, MAX_TOP);

        var fields = await _fields.ListByTableAsync(table.Id, ct);
        var byFid = fields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());

        var page = start / HANDLER_PAGE_SIZE + 1;
        var offset = start % HANDLER_PAGE_SIZE;
        PagedReportRunResult? first = null;
        var rows = new List<Application.Records.RecordResult>();
        while (rows.Count < take)
        {
            var result = await _runReport.HandleAsync(new RunReportQuery(reportId, page, HANDLER_PAGE_SIZE), ct);
            first ??= result;
            var items = result.Items.Skip(offset).ToList();
            offset = 0;
            rows.AddRange(items.Take(take - rows.Count));
            if (result.Items.Count < HANDLER_PAGE_SIZE || page * HANDLER_PAGE_SIZE >= result.TotalCount) break;
            page++;
        }
        first ??= await _runReport.HandleAsync(new RunReportQuery(reportId, 1, 1), ct);

        var isTable = report.ReportType == "Table";
        var columns = first.Columns;
        if (!isTable)
        {
            // The automatic record-count column is only shown when the report asks for it (as in Quickbase).
            var definition = (await _getReport.HandleAsync(new GetReportQuery(reportId), ct)).Definition;
            var wantsCount = definition.Aggregations.Any(a => a.FieldId == 0 && a.Function.Equals("Count", StringComparison.OrdinalIgnoreCase));
            columns = columns.Where(c => c.FieldId != 0 || wantsCount).ToList();
        }
        var refTypes = await _referenceTypes.ForAsync(fields, ct);
        var qbFields = columns.Select((c, i) =>
        {
            if (!isTable) return SummaryField(c.Name, i, c.FieldId);
            var type = byFid.TryGetValue(c.FieldId, out var f) ? QbFieldPropertiesBuilder.TypeOf(f, refTypes).FieldType : SummaryColumnType(c.TypeCode);
            return new QbReportField(c.FieldId, c.Name, type, null);
        }).ToList();

        // Full user objects (email / id / name) for stored user columns — the run handler only gives display names.
        var userFields = isTable
            ? columns.Select(c => byFid.TryGetValue(c.FieldId, out var f) ? f : null)
                .Where(f => f is not null && QbUserValueResolver.IsStoredUserField(f)).Select(f => f!).DistinctBy(f => f.Id).ToList()
            : new List<AppField>();
        var users = await _userValues.ResolveAsync(table, userFields, rows.Select(r => r.Id).ToList(), ct);
        var userFids = userFields.Select(f => (long)f.Fid!.Value).ToHashSet();

        var fileBase = $"/apps/{await _apps.GetPublicIdByIdAsync(table.AppId, ct)}/files/download";
        var uniqueFieldIds = columns.GroupBy(c => c.FieldId).Where(g => g.Count() == 1).Select(g => g.Key).ToHashSet();
        var data = rows.Select(r =>
        {
            IDictionary<string, QbCell> cells = new Dictionary<string, QbCell>();
            users.TryGetValue(r.Id, out var rowUsers);
            for (var i = 0; i < columns.Count; i++)
            {
                var col = columns[i];
                // Quickbase keys each cell by the column's field id (= fields[].id). A summary can show the same field twice
                // (e.g. Sum and Average of one field); only then is our unique column key used for the second column.
                var key = isTable ? col.FieldId.ToString() : qbFields[i].Id.ToString();
                if (isTable && userFids.Contains(col.FieldId) && rowUsers is not null && rowUsers.TryGetValue(col.FieldId, out var user))
                {
                    cells[key] = new QbCell(user);
                    continue;
                }
                r.Fields.TryGetValue(col.Key, out var raw);
                // Quickbase's Date Modified is never empty: a record never edited was last modified when it was created.
                if (raw is null && isTable && col.FieldId == QbIds.DATE_MODIFIED_FID) raw = r.CreatedOn;
                cells[key] = new QbCell(isTable ? QbValueFormatter.Format(raw, qbFields[i].Type, fileBase)
                    : qbFields[i].Type == "numeric" ? QbValueFormatter.Format(raw, "numeric") : GroupText(raw));
            }
            return cells;
        }).ToList();

        return new QbRunReportResponse
        {
            Data = data,
            Fields = qbFields,
            Metadata = new QbRunMetadata
            {
                TotalRecords = first.TotalCount,
                NumRecords = data.Count,
                NumFields = qbFields.Count,
                Skip = start,
                Top = top,
            },
        };
    }

    private static string SummaryColumnType(string? typeCode) => QbFieldTypeMap.Map(typeCode).FieldType;

    private static readonly System.Text.RegularExpressions.Regex AGG_LABEL =
        new(@"^(Sum|Avg|Average|Max|Min|Count|DistinctCount|StdDev) of (.+)$", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>A summary / chart output column the way Quickbase lists it: id = position + 2, aggregates "Label (tot)" numeric,
    /// group columns text (measured on a live realm).</summary>
    public static QbReportField SummaryField(string name, int index, long fieldId = 0)
    {
        var m = AGG_LABEL.Match(name);
        // Summary formulas (ids 2000000101+) are numbers; any other plain column is a group (text).
        if (!m.Success) return new QbReportField(index + 2, name, fieldId >= 2000000101 ? "numeric" : "text", null);
        var abbr = m.Groups[1].Value switch
        {
            "Sum" => "tot", "Avg" or "Average" => "avg", "Max" => "max", "Min" => "min", "Count" => "count",
            "DistinctCount" => "distinct count", "StdDev" => "std dev", var other => other.ToLowerInvariant(),
        };
        return new QbReportField(index + 2, $"{m.Groups[2].Value} ({abbr})", "numeric", null);
    }

    /// <summary>A summary group value as Quickbase shows it: text, "" when empty.</summary>
    private static string GroupText(object? raw) => raw switch
    {
        null or DBNull => "",
        bool b => b ? "yes" : "no",                 // live Quickbase writes checkbox groups in lower case
        System.Text.Json.JsonElement je => je.ValueKind is System.Text.Json.JsonValueKind.Null ? "" : je.ToString(),
        DateTime d => d.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => raw.ToString() ?? "",
    };

    public static string Kebab(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        return Regex.Replace(value.Trim(), "(?<=[a-z0-9])([A-Z])", "-$1").Replace('_', '-').Replace(' ', '-').ToLowerInvariant();
    }
}
