namespace PowerBase.API.Models.Fields;

/// <summary>One note an import attached to a field (GET tables/{tableId}/fields/{fieldId}/import-notes). Separate from the field's
/// own description and help text: it is about the import, and goes away when someone resolves it.</summary>
public class FieldImportNoteResponse
{
    public Guid PublicId { get; init; }

    /// <summary>Information, Warning or Error.</summary>
    public string Severity { get; init; } = string.Empty;
    public string Code { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string? RecommendedAction { get; init; }
    public string? SourceType { get; init; }
    public int? SourceFieldId { get; init; }

    /// <summary>True when the field is a stand-in for a source field that could not be created as itself.</summary>
    public bool IsPlaceholder { get; init; }

    /// <summary>The complete original formula or definition, never truncated.</summary>
    public string? OriginalDefinition { get; init; }

    /// <summary>The source field's properties as JSON, never truncated.</summary>
    public string? OriginalMetadata { get; init; }
    public DateTime CreatedOn { get; init; }
    public DateTime? ResolvedOn { get; init; }
}

/// <summary>Which fields of a table carry unresolved import notes (GET tables/{tableId}/fields/import-notes), for the fields grid.</summary>
public class FieldImportNoteSummaryResponse
{
    public Guid FieldPublicId { get; init; }
    public int ActiveCount { get; init; }
    public int WarningCount { get; init; }
    public int ErrorCount { get; init; }
    public bool HasPlaceholder { get; init; }

    /// <summary>The worst severity among the unresolved notes: Information, Warning or Error.</summary>
    public string Severity { get; init; } = string.Empty;
}
