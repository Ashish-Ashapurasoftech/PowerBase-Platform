using System.Text.Json;

namespace PowerBase.API.Models.Records;

public class CreateRecordRequest
{
    public Dictionary<string, JsonElement> Fields { get; set; } = new();
}
