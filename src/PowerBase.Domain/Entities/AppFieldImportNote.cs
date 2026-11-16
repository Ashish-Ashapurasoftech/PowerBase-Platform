namespace PowerBase.Domain.Entities;

/// <summary>
/// A note a QBL/PBL import attached to one field: why the field is only a placeholder, what could not be carried over, or
/// that its formula was rewritten. It keeps the complete original formula/definition and the source field's metadata.
/// Deliberately not the field's <see cref="AppField.Description"/> or <see cref="AppField.HelpText"/> — those belong to
/// the end user; an import note is about the import and goes away when someone resolves it.
/// </summary>
public class AppFieldImportNote
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long AppId { get; set; }
    public long AppTableId { get; set; }
    public long AppFieldId { get; set; }

    /// <summary>One of <see cref="ImportNoteSeverities"/>.</summary>
    public string Severity { get; set; } = ImportNoteSeverities.Warning;

    /// <summary>The importer's stable code for what happened (e.g. "QBL_UNSUPPORTED_FIELD_TYPE"); empty when it had none.</summary>
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string? RecommendedAction { get; set; }

    public string? SourceLogicalRef { get; set; }

    /// <summary>The source field's type, e.g. "QB::Field::Summary".</summary>
    public string? SourceType { get; set; }

    /// <summary>The source field's own number (the Fid the field was given).</summary>
    public int? SourceFieldId { get; set; }

    /// <summary>True when the field was created as a stand-in because the source field could not be created as itself.</summary>
    public bool IsPlaceholder { get; set; }

    /// <summary>The complete original formula or lookup/summary definition — never truncated.</summary>
    public string? OriginalDefinition { get; set; }

    /// <summary>The source field's properties as JSON — never truncated.</summary>
    public string? OriginalMetadata { get; set; }

    public DateTime CreatedOn { get; set; }
    public DateTime? ResolvedOn { get; set; }
    public long? ResolvedBy { get; set; }
}

public static class ImportNoteSeverities
{
    public const string Information = "Information";
    public const string Warning = "Warning";
    public const string Error = "Error";

    public static bool IsValid(string? value) => value is Information or Warning or Error;
}

/// <summary>Per-field roll-up of the unresolved import notes of a table, for the fields grid.</summary>
public sealed record AppFieldImportNoteSummary(long AppFieldId, int ActiveCount, int WarningCount, int ErrorCount, bool HasPlaceholder);
