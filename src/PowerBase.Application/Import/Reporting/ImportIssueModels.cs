using System.Text.Json.Serialization;

namespace PowerBase.Application.Import.Reporting;

/// <summary>How much attention an <see cref="ImportIssue"/> needs. <c>Information</c> is a cosmetic
/// note (the element was created, but one setting couldn't be carried over exactly); <c>Warning</c>
/// means something was skipped or needs manual review; <c>Error</c> blocks the import entirely.
/// Serialized as its name ("Information"/"Warning"/"Error"), not the default int, since there is no
/// global <c>JsonStringEnumConverter</c> registered — the frontend's <c>ImportIssueSeverity</c> type
/// is the matching string union.</summary>
[JsonConverter(typeof(JsonStringEnumConverter))]
public enum ImportIssueSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>One import warning/error, restated for end users: where it happened (app/table/field)
/// and what to do about it, with the original technical detail kept alongside for support/debugging
/// rather than discarded. Built by <see cref="ImportIssueReportBuilder"/> from the existing
/// <c>PblIssue</c>/<c>ImportSkippedItem</c>/<c>FormulaTranslationReportItem</c>/<c>PipelineImportIssueItem</c>
/// sources — this is a presentation layer over them, not a replacement.</summary>
public sealed class ImportIssue
{
    public ImportIssueSeverity Severity { get; init; }

    public string ApplicationName { get; init; } = string.Empty;
    public string? TableName { get; init; }
    public string? FieldName { get; init; }

    /// <summary>What kind of element this is about: Field, Table, Report, Form, FormRule, Relationship,
    /// Role, Variable, Page, Pipeline, Dashboard, or App/File for something not tied to one element.</summary>
    public string ElementType { get; init; } = "General";

    /// <summary>Human-readable explanation — never a raw QBL code or stack trace.</summary>
    public string Description { get; init; } = string.Empty;

    /// <summary>The original formula or configuration value this issue is about, when there is one worth showing.</summary>
    public string? OriginalConfiguration { get; init; }

    /// <summary>What the user can do about it, when there is a concrete next step.</summary>
    public string? RecommendedAction { get; init; }

    /// <summary>Stable machine code for this issue when the source provided one (e.g. "QBL_EMPTY_FORMULA").
    /// Empty when the source was a free-text reason with no code.</summary>
    public string Code { get; init; } = string.Empty;

    /// <summary>The original, unedited technical message (plus its element reference) — preserved for
    /// debugging/audit even though it is not shown to end users as the primary description.</summary>
    public string TechnicalDetail { get; init; } = string.Empty;
}

public sealed class ImportIssueSummary
{
    public int ErrorCount { get; init; }
    public int WarningCount { get; init; }
    public int InformationCount { get; init; }

    /// <summary>Distinct fields (by table+field name) that have at least one Warning or Error.</summary>
    public int AffectedFieldCount { get; init; }
    public int TotalCount => ErrorCount + WarningCount + InformationCount;
}

/// <summary>A structured, user-friendly restatement of an import's issues, grouped implicitly by
/// (ApplicationName, TableName, FieldName) on each <see cref="ImportIssue"/> — the frontend groups
/// the flat list for display rather than the backend nesting it, so the same shape works for the
/// preview screen, the post-import report, and the update report.</summary>
public sealed class ImportIssueReport
{
    public ImportIssueSummary Summary { get; init; } = new();
    public List<ImportIssue> Issues { get; init; } = [];
}
