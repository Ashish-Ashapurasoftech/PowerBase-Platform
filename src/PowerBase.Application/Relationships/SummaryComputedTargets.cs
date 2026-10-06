using System.Globalization;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Summaries over a child field that has no column: a Formula, or a Summary the child table has of
/// its own children (Project › Tasks › Time Entries). Such a value can't be aggregated in SQL, so it
/// is computed per child row and aggregated in memory (<see cref="Aggregate"/>). Which functions a
/// field supports depends on the type of value it returns (a child summary: Count/Sum/Avg/Distinct
/// Count → Number, True/False → Checkbox, Combined Text → Text, Min/Max → the summarized type):
///   Checkbox → Distinct Count · Date / DateTime → Min, Max, Distinct Count ·
///   Duration → Sum, Avg, Min, Max · Number → Sum, Avg, Min, Max, Distinct Count ·
///   Text / Email / Phone → Combined Text, Distinct Count.
/// Other formula results (Time, User, Rich Text, URL) can't be summarized.
/// </summary>
public static class SummaryComputedTargets
{
    private static readonly IReadOnlySet<string> CheckboxFunctions = new HashSet<string> { SummaryFunctions.DistinctCount };
    private static readonly IReadOnlySet<string> DateFunctions = new HashSet<string> { SummaryFunctions.Min, SummaryFunctions.Max, SummaryFunctions.DistinctCount };
    private static readonly IReadOnlySet<string> DurationFunctions = new HashSet<string> { SummaryFunctions.Sum, SummaryFunctions.Avg, SummaryFunctions.Min, SummaryFunctions.Max };
    private static readonly IReadOnlySet<string> NumericFunctions = new HashSet<string> { SummaryFunctions.Sum, SummaryFunctions.Avg, SummaryFunctions.Min, SummaryFunctions.Max, SummaryFunctions.DistinctCount };
    private static readonly IReadOnlySet<string> TextFunctions = new HashSet<string> { SummaryFunctions.CombinedText, SummaryFunctions.DistinctCount };

    /// <summary>True for a generic "Formula" field or any Formula_{X} variant.</summary>
    public static bool IsFormula(AppField field) =>
        field.TypeCode == "Formula" || PhysicalNaming.IsFormulaVariantTypeCode(field.TypeCode);

    /// <summary>True for a target with no column whose value is computed per child record: a
    /// Formula, or a Summary of the child's own children (Project › Tasks › Time Entries: Tasks'
    /// "Total Amount" summarized again on Project).</summary>
    public static bool IsComputedTarget(AppField field) => IsFormula(field) || field.TypeCode == "Summary";

    /// <summary>The kind of value a child Summary field produces: Count/Sum/Average/Distinct Count →
    /// Number, True/False → Bool, Combined Text → Text, Min/Max → the summarized field's kind.</summary>
    private static string? SummaryResultKind(AppField field)
    {
        var s = FormulaTypeMap.ParseSummarySettings(field.Settings);
        return SummaryFunctions.Normalize(s?.Function) switch
        {
            SummaryFunctions.Count or SummaryFunctions.Sum or SummaryFunctions.Avg or SummaryFunctions.DistinctCount => "Number",
            SummaryFunctions.Exists => "Bool",
            SummaryFunctions.CombinedText => "Text",
            SummaryFunctions.Min or SummaryFunctions.Max => s?.TargetTypeCode switch
            {
                "Number" or "Currency" or "Percent" or "Rating" or "Formula_Number" => "Number",
                "Duration" or "Formula_Duration" => "Duration",
                "Date" or "Formula_Date" => "Date",
                "DateTime" or "Formula_DateTime" => "DateTime",
                _ => null,
            },
            _ => null,
        };
    }

    /// <summary>The summary functions this formula field supports, or null when its result type
    /// can't be summarized (or it isn't a formula).</summary>
    public static IReadOnlySet<string>? AllowedFunctions(AppField field)
    {
        var kind = ResultKind(field);
        return kind switch
        {
            "Bool" => CheckboxFunctions,
            "Date" or "DateTime" => DateFunctions,
            "Duration" => DurationFunctions,
            "Number" => NumericFunctions,
            "Text" => TextFunctions,
            _ => null,
        };
    }

    /// <summary>The formula's result type: Bool, Date, DateTime, Duration, Number, Text — or null
    /// (Time, User, RichText, Url, or not a formula).</summary>
    public static string? ResultKind(AppField field) => field.TypeCode switch
    {
        "Summary" => SummaryResultKind(field),
        "Formula" => FormulaTypeMap.ParseSettings(field.Settings)?.ResultType switch
        {
            null or "" => "Text",
            var r when r.Equals("Bool", StringComparison.OrdinalIgnoreCase) => "Bool",
            var r when r.Equals("Date", StringComparison.OrdinalIgnoreCase) => "Date",
            var r when r.Equals("DateTime", StringComparison.OrdinalIgnoreCase) => "DateTime",
            var r when r.Equals("Duration", StringComparison.OrdinalIgnoreCase) => "Duration",
            var r when r.Equals("Number", StringComparison.OrdinalIgnoreCase) => "Number",
            var r when r.Equals("Text", StringComparison.OrdinalIgnoreCase) => "Text",
            _ => null,
        },
        "Formula_Bool" => "Bool",
        "Formula_Date" => "Date",
        "Formula_DateTime" => "DateTime",
        "Formula_Duration" => "Duration",
        "Formula_Number" => "Number",
        "Formula_Text" or "Formula_Email" or "Formula_Phone" => "Text",
        _ => null,
    };

    /// <summary>Aggregates one parent's computed formula values (nulls included, in child Id order
    /// or the requested sort order). Returns null when there is nothing to show; Distinct Count
    /// returns 0.</summary>
    public static object? Aggregate(string function, string resultKind, IReadOnlyList<object?> values, CombinedTextOptions? options)
    {
        var present = values.Where(v => v is not null && !(v is string s && string.IsNullOrWhiteSpace(s))).Select(v => v!).ToList();

        switch (function)
        {
            case SummaryFunctions.DistinctCount:
                return present.Select(ToKey).Distinct(StringComparer.OrdinalIgnoreCase).Count();

            case SummaryFunctions.CombinedText:
            {
                var o = options ?? CombinedTextOptions.Default;
                IEnumerable<string> texts = present.Select(v => Convert.ToString(v, CultureInfo.InvariantCulture)!);
                if (o.DistinctValues) texts = texts.Distinct(StringComparer.Ordinal);
                var joined = string.Join(o.Delimiter, texts);
                return joined.Length > 0 ? joined : null;
            }
        }

        if (present.Count == 0) return null;

        if (resultKind is "Date" or "DateTime")
        {
            // A formula gives ISO text (yyyy-MM-dd…), a child summary's Min/Max a DateTime: compare
            // as dates, return the winning value as it came.
            static DateTime ToDate(object v) => v is DateTime d ? d : DateTime.Parse(Convert.ToString(v, CultureInfo.InvariantCulture)!, CultureInfo.InvariantCulture);
            return function == SummaryFunctions.Min
                ? present.MinBy(ToDate)
                : present.MaxBy(ToDate);
        }

        var numbers = present.Select(v => Convert.ToDecimal(v, CultureInfo.InvariantCulture)).ToList();
        return function switch
        {
            SummaryFunctions.Sum => numbers.Sum(),
            SummaryFunctions.Avg => numbers.Average(),
            SummaryFunctions.Min => numbers.Min(),
            SummaryFunctions.Max => numbers.Max(),
            _ => null,
        };
    }

    private static string ToKey(object v) => v switch
    {
        bool b => b ? "true" : "false",
        decimal d => d.ToString("0.############################", CultureInfo.InvariantCulture),
        _ => Convert.ToString(v, CultureInfo.InvariantCulture)!.Trim(),
    };
}
