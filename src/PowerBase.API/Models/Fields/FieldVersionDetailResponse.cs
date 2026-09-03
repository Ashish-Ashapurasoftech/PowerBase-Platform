namespace PowerBase.API.Models.Fields;

public class FieldChangeResponse
{
    public string PropertyName { get; init; } = string.Empty;
    public string? OldValue { get; init; }
    public string? NewValue { get; init; }
}

public class FieldVersionDetailResponse
{
    public int Version { get; init; }
    public string CommitMessage { get; init; } = string.Empty;
    public string ChangedByName { get; init; } = string.Empty;
    public DateTime ChangedOn { get; init; }
    public int? RestoredFromVersion { get; init; }
    public bool IsCurrent { get; init; }
    public int CurrentVersion { get; init; }
    public IReadOnlyList<FieldChangeResponse> Changes { get; init; } = [];
    /// <summary>What would change if this version were restored right now, diffed against the
    /// field's live current settings — powers the restore confirmation's change preview.</summary>
    public IReadOnlyList<FieldChangeResponse> ChangesFromCurrent { get; init; } = [];
}
