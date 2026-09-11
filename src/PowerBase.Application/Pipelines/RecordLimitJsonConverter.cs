using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace PowerBase.Application.Pipelines;

/// <summary>Reads record limits from numeric inputs and legacy text inputs.</summary>
public sealed class RecordLimitJsonConverter : JsonConverter<int?>
{
    public override int? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return null;
        if (reader.TokenType == JsonTokenType.Number && reader.TryGetInt32(out var number)) return number;
        if (reader.TokenType == JsonTokenType.String)
        {
            var text = reader.GetString();
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out number)) return number;
        }
        throw new JsonException("Record limit must be a whole number between 1 and 2147483647.");
    }

    public override void Write(Utf8JsonWriter writer, int? value, JsonSerializerOptions options)
    {
        if (value.HasValue) writer.WriteNumberValue(value.Value);
        else writer.WriteNullValue();
    }
}
