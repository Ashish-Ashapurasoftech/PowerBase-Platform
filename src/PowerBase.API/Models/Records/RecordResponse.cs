namespace PowerBase.API.Models.Records;

public class RecordResponse
{
    public Guid Id { get; init; }
    public DateTime CreatedOn { get; init; }
    public DateTime? ModifiedOn { get; init; }
    /// <summary>Internal userId of the creator. Exposed so the UI can gate OwnRecords edits per-row.</summary>
    public long CreatedBy { get; init; }
    public Dictionary<string, object?> Fields { get; init; } = new();
}
