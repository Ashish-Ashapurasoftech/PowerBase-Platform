using System.Text.Json;

namespace PowerBase.API.Models.Records;

public class MassUpdateRecordsRequest
{
    public List<Guid> RecordIds { get; set; } = new();
    public Dictionary<string, JsonElement> Fields { get; set; } = new();
}
