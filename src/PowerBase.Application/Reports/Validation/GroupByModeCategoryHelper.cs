using PowerBase.Domain.Constants;

namespace PowerBase.Application.Reports.Validation;

/// <summary>
/// Which GroupByMode ("Combine") values a field's TypeCode supports, for Table Sort+Group
/// levels, Summary "Rows", Crosstab "Columns", and Chart category/series grouping. Hardcoded
/// switch, mirroring the existing precedent <see cref="FieldTypeCategoryHelper"/> (itself
/// modeled on PipelineFilterEvaluator.GetTypeCategory) rather than adding a new
/// core.FieldType/AppField capability flag — consistent with this codebase's convention for
/// this kind of rule.
///
/// Scope note (confirmed with product owner): Formula_* fields (including the generic
/// "Formula" TypeCode), Lookup/Summary/Reference (relationship fields), File, ReportLink, and
/// every ActionButton_* variant are deliberately NoGrouping for now — grouping by a computed
/// or relationship value needs the value resolved before/at GROUP BY time, which
/// SummarizeAsync doesn't do today. RichText/DateRange/NumericRange/anything unrecognized
/// fall back to Unclassified (the original 3-mode EqualValues/FirstWord/FirstLetter set), so
/// behavior for types outside this rule set is unchanged.
/// </summary>
public static class GroupByModeCategoryHelper
{
    public enum GroupByFamily
    {
        TextRich,
        TextSimple,
        Numeric,
        DateFamily,
        TimeFamily,
        DurationFamily,
        Boolean,
        User,
        MultiUserFamily,
        NoGrouping,
        Unclassified,
    }

    public static readonly string[] TextRichModes = ["EqualValues", "FirstWord", "FirstLetter"];
    public static readonly string[] TextSimpleModes = ["EqualValues"];
    public static readonly string[] NumericModes = ["EqualValues", "Increment1", "Increment10", "Increment100", "Increment1000", "Increment10000"];
    public static readonly string[] DateModes = ["EqualValues", "Day", "Week", "Month", "Quarter", "Year", "Decade"];
    public static readonly string[] TimeModes = ["EqualValues"];
    public static readonly string[] DurationModes = ["EqualValues", "Minute", "Hour", "Day", "Week"];
    public static readonly string[] BooleanModes = ["EqualValues"];
    public static readonly string[] UserModes = ["EqualValues", "FirstLetter"];
    public static readonly string[] MultiUserModes = ["EqualValues"];
    /// <summary>NoGrouping fields (File, ReportLink, Lookup, Formula_*, etc.) still accept the
    /// default "EqualValues" — it's the universal harmless raw-column fallback used everywhere
    /// (see RecordRepository.BuildGroupByExpr's default arm) and is what a Table Sort+Group
    /// level defaults to before a user ever touches its Combine picker. What NoGrouping means
    /// is: no family-specific extra modes (First Word, Month, Increment10, ...) — the frontend
    /// separately hides the Combine picker entirely for these types rather than showing a
    /// single-option dropdown.</summary>
    public static readonly string[] NoGroupingModes = ["EqualValues"];
    /// <summary>Original 3-mode set — the fallback for any TypeCode not explicitly classified
    /// below, so behavior for types outside this rule set (RichText, DateRange, NumericRange,
    /// future/unknown codes) is unchanged from before this helper existed.</summary>
    public static readonly string[] UnclassifiedModes = TextRichModes;

    private static readonly HashSet<string> TextRichTypeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "TextMultiLine", "SingleSelect",
    };

    private static readonly HashSet<string> TextSimpleTypeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "MultiSelect", "Address", "Phone", "Email", "Url",
    };

    private static readonly HashSet<string> NumericTypeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Number", "Currency", "Percent", "Rating",
    };

    private static readonly HashSet<string> DateTypeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Date", "DateTime",
    };

    /// <summary>Relationship/computed/action TypeCodes that are NoGrouping regardless of the
    /// Formula_* check below (those are also NoGrouping, handled separately since they're a
    /// prefix match, not a fixed set).</summary>
    private static readonly HashSet<string> NoGroupingTypeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "File", "ReportLink", "Lookup", "Summary", "Reference",
        "ActionButton_File", "ActionButton_Signature", "ActionButton_Prompt", "ActionButton_Data",
    };

    /// <param name="typeCode">The field's AppField.TypeCode.</param>
    public static GroupByFamily GetFamily(string typeCode)
    {
        if (TextRichTypeCodes.Contains(typeCode)) return GroupByFamily.TextRich;
        if (TextSimpleTypeCodes.Contains(typeCode)) return GroupByFamily.TextSimple;
        if (NumericTypeCodes.Contains(typeCode)) return GroupByFamily.Numeric;
        if (DateTypeCodes.Contains(typeCode)) return GroupByFamily.DateFamily;
        if (string.Equals(typeCode, "Time", StringComparison.OrdinalIgnoreCase)) return GroupByFamily.TimeFamily;
        if (string.Equals(typeCode, "Duration", StringComparison.OrdinalIgnoreCase)) return GroupByFamily.DurationFamily;
        if (string.Equals(typeCode, "Boolean", StringComparison.OrdinalIgnoreCase)) return GroupByFamily.Boolean;
        if (string.Equals(typeCode, "User", StringComparison.OrdinalIgnoreCase)) return GroupByFamily.User;
        if (string.Equals(typeCode, "MultiUser", StringComparison.OrdinalIgnoreCase)) return GroupByFamily.MultiUserFamily;

        if (NoGroupingTypeCodes.Contains(typeCode)
            || string.Equals(typeCode, "Formula", StringComparison.OrdinalIgnoreCase)
            || PhysicalNaming.IsFormulaVariantTypeCode(typeCode)
            || PhysicalNaming.IsActionButtonTypeCode(typeCode))
        {
            return GroupByFamily.NoGrouping;
        }

        return GroupByFamily.Unclassified;
    }

    public static IReadOnlyList<string> GetAllowedGroupByModes(string typeCode) => GetFamily(typeCode) switch
    {
        GroupByFamily.TextRich => TextRichModes,
        GroupByFamily.TextSimple => TextSimpleModes,
        GroupByFamily.Numeric => NumericModes,
        GroupByFamily.DateFamily => DateModes,
        GroupByFamily.TimeFamily => TimeModes,
        GroupByFamily.DurationFamily => DurationModes,
        GroupByFamily.Boolean => BooleanModes,
        GroupByFamily.User => UserModes,
        GroupByFamily.MultiUserFamily => MultiUserModes,
        GroupByFamily.NoGrouping => NoGroupingModes,
        _ => UnclassifiedModes,
    };

    public static bool SupportsGrouping(string typeCode) => GetFamily(typeCode) != GroupByFamily.NoGrouping;
}
