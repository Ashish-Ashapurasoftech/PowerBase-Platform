using PowerBase.Application.Reports.Validation;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Type-based validation for a Summary field's aggregation target, enforced server-side so the
/// API rejects combinations the UI would never offer:
///   • Count / Exists → no target needed.
///   • Any target     → must be a stored field; calculated (Formula/Lookup/Summary) fields have
///                      no physical column to aggregate.
///   • DistinctCount  → any stored field type.
///   • CombinedText   → any stored field type with a text form (see CombinedTextUnsupportedTypes).
///   • Sum / Avg      → numeric fields only.
///   • Min / Max      → numeric and date fields only. Text is rejected — lexicographic ordering
///                      gives wrong results for numeric-looking strings ("10" &lt; "9").
/// Numeric/date classification reuses <see cref="FieldTypeCategoryHelper"/> (same rule as Summary
/// reports), minus Boolean: SQL Server's MIN/MAX don't accept BIT and a summed checkbox is a Count
/// with matching criteria, so Boolean isn't a valid Summary-field target.
/// </summary>
public static class SummaryTargetValidator
{
    /// <summary>Field types Combined Text can't render as text: their stored value is an id
    /// (User/MultiUser → a user, Reference → a record), a file list, HTML, or a range whose end
    /// lives in a second column the summary doesn't read. Mirrored by the add-summary dialog.</summary>
    public static readonly IReadOnlySet<string> CombinedTextUnsupportedTypes = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "User", "MultiUser", "File", "RichText", "Reference", "DateRange", "NumericRange",
    };

    /// <summary>Throws <see cref="ValidationException"/> (keyed by <paramref name="paramName"/>)
    /// when <paramref name="function"/> can't aggregate <paramref name="target"/>.</summary>
    public static void Validate(string function, int? targetFid, AppField? target, string? targetSubField, string paramName = "targetFid")
    {
        if (function is SummaryFunctions.Count or SummaryFunctions.Exists)
            return;

        if (targetFid is null)
            throw Error(paramName, $"{function} requires a field to summarize.");
        if (target is null)
            throw new NotFoundException("Field", targetFid.Value);

        if (FindProblem(function, target, targetSubField) is { } problem)
            throw Error(paramName, problem);
    }

    /// <summary>Why <paramref name="function"/> can't aggregate <paramref name="target"/>, or null
    /// when it can — the rule itself, without throwing, so callers can check several summaries
    /// (e.g. every summary a field type change would affect) and report them together.</summary>
    public static string? FindProblem(string function, AppField target, string? targetSubField)
    {
        if (function is SummaryFunctions.Count or SummaryFunctions.Exists)
            return null;

        var fieldLabel = string.IsNullOrWhiteSpace(target.Label) ? target.Name : target.Label;

        // Formula/Lookup/Summary values are computed at read time and have no physical column to
        // aggregate in SQL — summarizing one would fail with "Invalid column name" on every read.
        if (PhysicalNaming.IsComputedTypeCode(target.TypeCode))
            return $"'{fieldLabel}' is a calculated ({target.TypeCode}) field and can't be summarized; choose a stored field.";
        // System fields live in their own named columns (Id, CreatedOn, …), not the f_{fid} column
        // the summary SQL aggregates.
        if (target.IsSystem)
            return $"'{fieldLabel}' is a system field and can't be summarized.";

        // Combined Text shows each value as text, so it needs a field whose value has a text form of
        // its own — not a user id, file list, HTML, record id or a two-column range.
        if (function == SummaryFunctions.CombinedText
            && CombinedTextUnsupportedTypes.Contains(target.TypeCode) && string.IsNullOrWhiteSpace(targetSubField))
            return $"'Combined Text' can't show {WithArticle(target.TypeCode)} field's values as text; '{fieldLabel}' can be summarized with Distinct Count instead.";

        // Distinct Count and Combined Text work on any other stored field type.
        if (function is SummaryFunctions.DistinctCount or SummaryFunctions.CombinedText)
            return null;

        var category = GetCategory(target, targetSubField);

        return function switch
        {
            SummaryFunctions.Sum or SummaryFunctions.Avg when category != FieldTypeCategoryHelper.SummarizableCategory.Numeric
                => $"'{function}' can only summarize numeric fields; '{fieldLabel}' is {DescribeType(target, targetSubField)}.",
            SummaryFunctions.Min or SummaryFunctions.Max when category == FieldTypeCategoryHelper.SummarizableCategory.DistinctOnly
                => $"'{function}' can only summarize numeric or date fields; '{fieldLabel}' is {DescribeType(target, targetSubField)}.",
            _ => null,
        };
    }

    /// <summary>Validates Combined Text's delimiter / sort options against the child table's
    /// fields and returns them (defaults when none were given). Returns null for any other function —
    /// those settings mean nothing there and aren't stored.</summary>
    public static CombinedTextOptions? ValidateCombinedTextOptions(string function, CombinedTextOptions? options, IReadOnlyList<AppField> childFields)
    {
        if (function != SummaryFunctions.CombinedText) return null;
        if (options is null) return CombinedTextOptions.Default;

        // An empty delimiter is a user mistake — values would run together ("Call clientSend invoice").
        var delimiter = options.Delimiter;
        if (delimiter.Length == 0)
            throw Error("delimiter", "Delimiter can't be empty.");
        if (delimiter.Length > SummaryFunctions.MaxCombinedTextDelimiterLength)
            throw Error("delimiter", $"Delimiter can be at most {SummaryFunctions.MaxCombinedTextDelimiterLength} characters.");

        if (options.SortFid is int sortFid)
        {
            var sortField = childFields.FirstOrDefault(f => f.Fid == sortFid)
                ?? throw new NotFoundException("Field", sortFid);
            // Same storage rule as the target: the sort runs in SQL against the f_{fid} column.
            if (sortField.IsSystem || PhysicalNaming.IsComputedTypeCode(sortField.TypeCode))
                throw Error("sortFid", $"'{sortField.Label ?? sortField.Name}' can't be used for sorting; choose a stored field.");
        }

        return options;
    }

    private static FieldTypeCategoryHelper.SummarizableCategory GetCategory(AppField target, string? targetSubField)
    {
        // An Address sub-field is always a text value, whatever the function.
        if (!string.IsNullOrWhiteSpace(targetSubField))
            return FieldTypeCategoryHelper.SummarizableCategory.DistinctOnly;

        if (string.Equals(target.TypeCode, "Boolean", StringComparison.OrdinalIgnoreCase))
            return FieldTypeCategoryHelper.SummarizableCategory.DistinctOnly;

        return FieldTypeCategoryHelper.GetSummarizableCategory(target.TypeCode);
    }

    private static string DescribeType(AppField target, string? targetSubField) =>
        string.IsNullOrWhiteSpace(targetSubField) ? $"{WithArticle(target.TypeCode)} field" : "an address sub-field (text)";

    /// <summary>"an Email", "an Address" — but "a User", "a Url", "a Number". Field type names
    /// starting with A/E/I/O take "an"; the U-types (User, Url) are pronounced "you-" so keep "a".</summary>
    internal static string WithArticle(string typeName) =>
        (typeName.Length > 0 && "AEIOaeio".Contains(typeName[0]) ? "an " : "a ") + typeName;

    private static ValidationException Error(string paramName, string message) =>
        new(new Dictionary<string, string[]> { [paramName] = [message] });
}
