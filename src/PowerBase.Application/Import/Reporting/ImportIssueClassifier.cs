using System.Text.RegularExpressions;

namespace PowerBase.Application.Import.Reporting;

/// <summary>Turns a Code (when the source provided one, e.g. "QBL_EMPTY_FORMULA") and/or a free-text
/// reason into a severity and a recommended action, by keyword — without needing a hand-written entry
/// for every one of the ~90 codes the QBL/PBL importer can raise. Codes/phrases that are purely
/// cosmetic (a setting wasn't carried over, but the element itself was still created) are downgraded
/// from Warning to Information; everything else keeps the severity the source already assigned.</summary>
public static class ImportIssueClassifier
{
    /// <summary>Codes (or free-text reasons, matched by the same keyword rules below applied to Code
    /// first) that are cosmetic notes rather than something skipped — the element was created, only
    /// one Quickbase-specific setting could not be carried over exactly.</summary>
    private static readonly HashSet<string> InformationalCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "QBL_APP_SEARCHABLE_NOT_MAPPED",
        "QBL_FIELD_SETTING_NOT_CARRIED",
        "QBL_ROLE_MODIFY_CRITERIA_DIFFERS",
        "QBL_UNSUPPORTED_ROLE_ACCESS_WHEN",
        "QBL_UNSUPPORTED_CHART_GROUPING",
        "QBL_UNSUPPORTED_GAUGE_COUNT",
        "QBL_DEFAULT_COLUMN_UNAVAILABLE",
        "QBL_DEFAULT_DYNAMIC_FILTER_UNAVAILABLE",
        // A form element / report column / sort that points at a field Quickbase itself no longer has (a dangling reference in the file):
        // there is nothing to create, so it is recorded for information, not as something PowerBase failed to import.
        "QBL_FORM_ELEMENT_FIELD_UNAVAILABLE",
        "QBL_REPORT_FIELD_UNAVAILABLE",
        // Represented by PowerBase's own field / kept as a note on the field.
        "QBL_REFERENCE_PROXY_NOT_SEPARATE",
        "IMPORT_FIELD_TEXT_SHORTENED",
        "FORMULA_ADJUSTED",
        // A Quickbase report/chart setting PowerBase has no equivalent for: the report itself was created.
        "QBL_REPORT_SETTING_NOT_CARRIED",
        "QBL_REPORT_FORMULA_COLUMN_NOT_CARRIED",
        "QBL_ROLE_SETTING_NOT_CARRIED",
        "QBL_ROLE_ORDER_NOT_CARRIED",
    };

    public static (ImportIssueSeverity Severity, string? RecommendedAction) Classify(
        string? code, string message, ImportIssueSeverity sourceSeverity)
    {
        if (sourceSeverity == ImportIssueSeverity.Error)
            return (ImportIssueSeverity.Error, RecommendedActionFor(code, message));

        var severity = !string.IsNullOrEmpty(code) && InformationalCodes.Contains(code)
            ? ImportIssueSeverity.Information
            : ImportIssueSeverity.Warning;

        return (severity, RecommendedActionFor(code, message));
    }

    private static string? RecommendedActionFor(string? code, string message)
    {
        var text = $"{code} {message}";

        if (Contains(text, "FORMULA") && (Contains(text, "UNTRANSLATABLE") || Contains(text, "EMPTY") || Contains(text, "MANUAL REVIEW") || Contains(text, "MANUALREVIEW")))
            return "Open the field's formula in PowerBase and write or review the expression by hand.";

        if (Contains(text, "UNRESOLVED") || Contains(text, "UNAVAILABLE") || Contains(text, "MISSING") ||
            Contains(text, "UNKNOWN") || Contains(text, "DELETED") || Contains(text, "NOT CREATED") || Contains(text, "NOT RESOLVED"))
            return "Check that the referenced field, table or relationship still exists in the source and is spelled the same, then re-import; or add/fix it manually in PowerBase.";

        if (Contains(text, "DUPLICATE"))
            return "Rename the duplicate (in the source file, or in PowerBase after import) so each name is unique.";

        if (Contains(text, "UNSUPPORTED"))
            return "This Quickbase feature isn't supported by this import yet; recreate the equivalent behavior manually in PowerBase if you need it.";

        if (Contains(text, "NOT APPLIED") || Contains(text, "NOT CARRIED") || Contains(text, "NOT MAPPED") || Contains(text, "LEFT AT THE DEFAULT"))
            return null; // cosmetic — no action needed

        return "Review this item after import; no automatic fix is available for it.";
    }

    private static bool Contains(string haystack, string needle) =>
        haystack.Contains(needle, StringComparison.OrdinalIgnoreCase);

    /// <summary>Best-effort display name pulled out of a free-text message that has no element
    /// reference of its own (most <c>ImportSkippedItem</c> reasons) — the first single-quoted
    /// segment, e.g. "Formula field 'Activity Backup' has no expression…" → "Activity Backup".</summary>
    public static string? ExtractQuotedName(string message)
    {
        var match = SingleQuoted.Match(message);
        return match.Success ? match.Groups[1].Value : null;
    }

    private static readonly Regex SingleQuoted = new("'([^']+)'", RegexOptions.Compiled);
}
