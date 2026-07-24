using System.Text.Json;

namespace PowerBase.Application.Import.Pbl;

public static class PblSerializer
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Throws <see cref="JsonException"/> on malformed JSON.</summary>
    public static PblDocument Deserialize(string json) =>
        JsonSerializer.Deserialize<PblDocument>(json, Options)
            ?? throw new JsonException("PBL document deserialized to null.");
}
