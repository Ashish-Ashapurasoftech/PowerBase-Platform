namespace PowerBase.API.Models.Records;

public class BulkCreateRecordsResponse
{
    /// <summary>The created records, in the same order as the request's <c>records</c>.</summary>
    public List<RecordResponse> Records { get; init; } = new();
}
