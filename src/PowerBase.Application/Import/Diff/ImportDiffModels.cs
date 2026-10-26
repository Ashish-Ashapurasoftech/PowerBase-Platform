using System.Text.Json.Serialization;

namespace PowerBase.Application.Import.Diff;

public static class ImportDiffStatus
{
    /// <summary>In the file, not in the app. Can be added.</summary>
    public const string New = "New";
    /// <summary>Matched, and the file's values differ. Needs an explicit Overwrite or Skip.</summary>
    public const string Changed = "Changed";
    /// <summary>Matched, nothing to change.</summary>
    public const string Identical = "Identical";
    /// <summary>Matched, but differs in a way this import never overwrites (e.g. a field type). Skip only.</summary>
    public const string Conflict = "Conflict";
}

public static class ImportMatchedBy
{
    public const string Mapping = "Mapping";
    public const string SourceRef = "SourceRef";
    public const string Name = "Name";
}

public sealed class ImportPropertyChange
{
    public string Property { get; init; } = string.Empty;
    public string? Before { get; init; }
    public string? After { get; init; }
}

public sealed class ImportDiffItem
{
    public string ElementType { get; init; } = string.Empty;
    public string SourceLogicalRef { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;

    /// <summary>Owning table's name for table-scoped elements (fields, reports, forms).</summary>
    public string? TableName { get; init; }
    public string TableRef { get; init; } = string.Empty;

    public string Status { get; init; } = ImportDiffStatus.New;
    public string? MatchedBy { get; init; }
    public Guid? TargetElementPublicId { get; init; }

    /// <summary>Internal id of the matched element — server-side use only, never serialized.</summary>
    [JsonIgnore]
    public long? TargetElementId { get; init; }

    /// <summary>True when "Overwrite" is allowed for this element in this version.</summary>
    public bool CanOverwrite { get; init; }
    public List<ImportPropertyChange> Changes { get; init; } = [];
    public string? Reason { get; init; }
}

public sealed class ImportOnlyInAppItem
{
    public string ElementType { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string? TableName { get; init; }
}

public sealed class ImportDiffResult
{
    public Guid TargetAppPublicId { get; init; }
    public string TargetAppName { get; init; } = string.Empty;
    public List<ImportDiffItem> Items { get; init; } = [];

    /// <summary>Present in the app but not in the file. Always preserved — Update never deletes.</summary>
    public List<ImportOnlyInAppItem> OnlyInApp { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}
