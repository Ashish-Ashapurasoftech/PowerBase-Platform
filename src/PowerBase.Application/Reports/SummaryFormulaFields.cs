using PowerBase.Application.Reports.Validation;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Reports;

/// <summary>
/// Summary formulas: report-only calculated columns of a Summary or Chart report that are computed per output row (per group) from
/// named aggregates — the report's <see cref="SummaryVariable"/>s — after the rows have been grouped and aggregated. A formula
/// reads variables ("[Revenue (tot)] - [Expense (tot)]"), never table fields, and is never stored.
/// A formula is shown by adding an aggregation whose FieldId is the formula's reserved id (Function <see cref="AggregationFunction"/>):
/// as a Summarize value on a Summary report, or as a Y-axis value on a Chart report.
/// </summary>
public static class SummaryFormulaFields
{
    public const int MaxFormulas = 3;
    public const int MaxVariables = 10;

    /// <summary>The Function an aggregation carries when it shows a summary formula instead of an aggregate of a field.</summary>
    public const string AggregationFunction = "Formula";

    /// <summary>Reserved ids for the formulas: far above any real Fid and clear of the report-formula range (2,000,000,001..003).</summary>
    public const long FirstId = 2_000_000_101;

    /// <summary>Reserved ids for the variables when they are exposed to the formula engine as fields.</summary>
    public const long VariableFirstId = 2_000_000_201;

    /// <summary>Return types a summary formula may have on a Summary report.</summary>
    public static readonly string[] AllowedTypeCodes =
    [
        "Formula_Text", "Formula_Number", "Formula_Date", "Formula_DateTime", "Formula_Time",
        "Formula_Duration", "Formula_Bool", "Formula_Phone", "Formula_Email", "Formula_Url",
    ];

    /// <summary>Return types allowed on a Chart report: only what can be plotted — Numeric (incl. Currency/Percent/Rating), Duration, Checkbox.</summary>
    public static readonly string[] ChartTypeCodes = ["Formula_Number", "Formula_Duration", "Formula_Bool"];

    private static readonly string[] AllowedDisplayAs = [NumericDisplayAs.Number, NumericDisplayAs.Currency, NumericDisplayAs.Percent, NumericDisplayAs.Rating];

    public static bool IsSummaryFormulaFid(long fieldId) => fieldId >= FirstId && fieldId < FirstId + MaxFormulas;

    public static bool IsAllowedTypeCode(string? typeCode, string reportType) =>
        (string.Equals(reportType, "Chart", StringComparison.OrdinalIgnoreCase) ? ChartTypeCodes : AllowedTypeCodes)
            .Contains(typeCode ?? string.Empty, StringComparer.Ordinal);

    public static bool IsAllowedDisplayAs(string? displayAs) =>
        string.IsNullOrEmpty(displayAs) || AllowedDisplayAs.Contains(displayAs, StringComparer.Ordinal);

    /// <summary>The key a formula's value has in a summarized row.</summary>
    public static string RowKey(long formulaId) => $"SummaryFormula_{formulaId}";

    /// <summary>The "{Function}_{FieldName}" key an aggregate's value has in a summarized row (same alias SummarizeAsync emits).</summary>
    public static string AggregateAlias(string function, AppField field) => $"{function}_{field.Name.Replace(" ", "_")}";

    /// <summary>The type of value a variable presents to a formula.</summary>
    public static string VariableTypeCode(SummaryVariable variable, AppField source)
    {
        if (variable.Function is "Min" or "Max")
        {
            var category = FieldTypeCategoryHelper.GetSummarizableCategory(source.TypeCode, SummaryReportConfigValidator.GetFormulaResultType(source));
            if (category == FieldTypeCategoryHelper.SummarizableCategory.Date)
                return source.TypeCode is "Date" or "Formula_Date" ? "Date" : "DateTime";
            if (source.TypeCode is "Boolean" or "Formula_Bool") return "Boolean";
            if (source.TypeCode is "Duration" or "Formula_Duration") return "Duration";
        }
        return "Number";
    }

    /// <summary>The variables as in-memory fields (Fid = <see cref="VariableFirstId"/> + position in the list) — what a formula is compiled
    /// and evaluated against. A variable whose field is unknown is left out, so a formula that reads it fails to compile.</summary>
    public static IReadOnlyList<AppField> BuildVariableFields(IReadOnlyList<SummaryVariable>? variables, IReadOnlyList<AppField> tableFields, long tableId)
    {
        if (variables is null || variables.Count == 0) return [];
        var byFid = tableFields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
        var result = new List<AppField>();
        for (var i = 0; i < variables.Count; i++)
        {
            var v = variables[i];
            if (string.IsNullOrWhiteSpace(v.Name) || !byFid.TryGetValue(v.FieldId, out var source)) continue;
            var fid = VariableFirstId + i;
            result.Add(new AppField
            {
                Id = fid, Fid = (int)fid, AppTableId = tableId, TypeCode = VariableTypeCode(v, source),
                Name = v.Name.Trim(), Label = v.Name.Trim(),
                IsReportable = true, IsSortable = false, IsFilterable = false, IsSearchable = false, IsAuditable = false,
            });
        }
        return result;
    }

    /// <summary>The formulas as in-memory fields, so config validation sees an aggregation / sort that names one as a known field.</summary>
    public static IReadOnlyList<AppField> BuildFormulaFields(IEnumerable<SummaryFormula>? formulas, long tableId) =>
        (formulas ?? [])
            .Where(f => IsSummaryFormulaFid(f.Id))
            .GroupBy(f => f.Id).Select(g => g.First())
            .Select(f => new AppField
            {
                Id = f.Id, Fid = (int)f.Id, AppTableId = tableId, TypeCode = f.TypeCode, Name = f.Name, Label = f.Name,
                Settings = ReportFormulaFields.BuildSettings(ToReportFormula(f)),
                IsReportable = true, IsSortable = false, IsFilterable = false, IsSearchable = false, IsAuditable = false,
            })
            .ToList();

    internal static ReportFormula ToReportFormula(SummaryFormula f) =>
        new() { Id = f.Id, Name = f.Name, TypeCode = f.TypeCode, DisplayAs = f.DisplayAs, Expression = f.Expression };

    /// <summary>The TypeCode a formula's output column has, so the existing renderers show it correctly
    /// (Currency/Percent/Rating from the numeric display, text variants by their own code).</summary>
    public static string ColumnTypeCode(SummaryFormula f) => f.TypeCode switch
    {
        "Formula_Number" => f.DisplayAs switch
        {
            NumericDisplayAs.Currency => "Currency",
            NumericDisplayAs.Percent => "Percent",
            NumericDisplayAs.Rating => "Rating",
            _ => "Number",
        },
        "Formula_Date" => "Date",
        "Formula_DateTime" => "DateTime",
        "Formula_Time" => "Time",
        "Formula_Duration" => "Duration",
        "Formula_Bool" => "Boolean",
        "Formula_Phone" => "Phone",
        "Formula_Email" => "Email",
        "Formula_Url" => "Url",
        _ => "Text",
    };
}
