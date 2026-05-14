namespace PowerBase.API.Models.Records;

public class RecordResponse
{
    public Guid Id { get; init; }
    public DateTime CreatedOn { get; init; }
    public DateTime? ModifiedOn { get; init; }
    public Dictionary<string, object?> Fields { get; init; } = new();
}
