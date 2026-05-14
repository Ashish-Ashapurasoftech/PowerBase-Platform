using System.Text.Json;

namespace PowerBase.API.Models.Records;

public class UpdateRecordRequest
{
    public Dictionary<string, JsonElement> Fields { get; set; } = new();
}
