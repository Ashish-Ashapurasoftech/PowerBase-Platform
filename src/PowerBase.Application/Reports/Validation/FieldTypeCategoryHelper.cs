namespace PowerBase.Application.Reports.Validation;

/// <summary>
/// Which Summarize-By aggregate functions a field's TypeCode supports, for Summary report
/// aggregations. Hardcoded switch, mirroring the existing precedent
/// PowerBase.Application.Pipelines.PipelineFilterEvaluator.GetTypeCategory rather than adding a
/// new core.FieldType/AppField capability flag — consistent with this codebase's convention for
/// this kind of rule.
///
/// Rule (confirmed with product owner):
///   Group A (Number/Currency/Percent/Rating/Duration/Boolean + numeric-result Formula variants):
///     Sum, Avg, Max, Min, StdDev, DistinctCount, Median
///   Group B (Date/DateTime + date-result Formula variants):
///     Max, Min, DistinctCount
///   Group C (everything else, INCLUDING NumericRange — deliberately not in Group A despite
///     sharing the "Numeric" core.FieldType category):
///     DistinctCount only
/// </summary>
public static class FieldTypeCategoryHelper
{
    public enum SummarizableCategory { Numeric, Date, DistinctOnly }

    public static readonly string[] NumericGroupFunctions = ["Sum", "Avg", "Max", "Min", "StdDev", "DistinctCount", "Median"];
    public static readonly string[] DateGroupFunctions = ["Max", "Min", "DistinctCount"];
    public static readonly string[] DistinctOnlyFunctions = ["DistinctCount"];

    private static readonly HashSet<string> NumericFamilyTypeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Number", "Currency", "Percent", "Rating", "Duration", "Boolean",
        "Formula_Number", "Formula_Duration", "Formula_Bool",
    };

    private static readonly HashSet<string> DateFamilyTypeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Date", "DateTime", "Formula_Date", "Formula_DateTime",
    };

    /// <param name="typeCode">The field's AppField.TypeCode.</param>
    /// <param name="formulaResultType">For the generic "Formula" TypeCode only (tenants whose
    /// catalog lacks a dedicated Formula_{X} row) — FormulaSettings.ResultType parsed from
    /// AppField.Settings. Ignored for every other TypeCode.</param>
    public static SummarizableCategory GetSummarizableCategory(string typeCode, string? formulaResultType = null)
    {
        if (string.Equals(typeCode, "Formula", StringComparison.OrdinalIgnoreCase))
        {
            return formulaResultType switch
            {
                "Number" or "Duration" or "Bool" => SummarizableCategory.Numeric,
                "Date" or "DateTime" => SummarizableCategory.Date,
                _ => SummarizableCategory.DistinctOnly,
            };
        }

        if (NumericFamilyTypeCodes.Contains(typeCode))
            return SummarizableCategory.Numeric;

        if (DateFamilyTypeCodes.Contains(typeCode))
            return SummarizableCategory.Date;

        // Includes NumericRange and every other type not explicitly listed above.
        return SummarizableCategory.DistinctOnly;
    }

    public static IReadOnlyList<string> GetAllowedSummarizeByFunctions(string typeCode, string? formulaResultType = null) =>
        GetSummarizableCategory(typeCode, formulaResultType) switch
        {
            SummarizableCategory.Numeric => NumericGroupFunctions,
            SummarizableCategory.Date => DateGroupFunctions,
            _ => DistinctOnlyFunctions,
        };
}
