namespace PowerBase.API.Models.Fields;

/// <summary>One row of the Audit History grid. ChangedPropertiesSummary is a short, comma-joined
/// list of property names (e.g. "IsRequired, IsSearchable") — the full before/after per property
/// is only fetched on demand via GET .../versions/{version}.</summary>
public class FieldVersionListItemResponse
{
    public int Version { get; init; }
    public string ChangeType { get; init; } = string.Empty;
    public int? RestoredFromVersion { get; init; }
    public string CommitMessage { get; init; } = string.Empty;
    public string ChangedByName { get; init; } = string.Empty;
    public DateTime ChangedOn { get; init; }
    public string ChangedPropertiesSummary { get; init; } = string.Empty;
    /// <summary>True for the field's current (highest) version — the frontend disables Restore
    /// for this row ("do not allow restoring the currently active version").</summary>
    public bool IsCurrent { get; init; }
}
