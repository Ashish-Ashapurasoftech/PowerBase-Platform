using System.Net;
using System.Text.Json;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// The named parameters of an HTTP request, as later steps read them: <c>url_params</c> (the query string) and
/// <c>body_params</c> (a form post's fields, or the top-level properties of a JSON object body). Shared by the Incoming
/// Request trigger (what a caller sent us) and Make Request (what the step sent). A name used once maps to its value; a
/// name repeated maps to the list of its values.
/// </summary>
public static class RequestParameters
{
    public static Dictionary<string, object?> FromQuery(string? query)
    {
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var part in (query ?? "").TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = WebUtility.UrlDecode(pair[0]);
            if (string.IsNullOrEmpty(key)) continue;
            if (!values.TryGetValue(key, out var list)) values[key] = list = new();
            list.Add(pair.Length > 1 ? WebUtility.UrlDecode(pair[1]) : "");
        }
        return Collapse(values);
    }

    /// <summary>Form-encoded bodies give their fields; a JSON object body gives its top-level properties; anything else
    /// (text, XML, a JSON array, an empty body) has no named parameters.</summary>
    public static Dictionary<string, object?> FromBody(string? mediaType, string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return new();
        if (string.Equals(mediaType?.Split(';')[0].Trim(), "application/x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
            return FromQuery(body);
        try
        {
            using var document = JsonDocument.Parse(body);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return new();
            var result = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var property in document.RootElement.EnumerateObject())
                result[property.Name] = property.Value.Clone();
            return result;
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public static Dictionary<string, object?> Collapse(Dictionary<string, List<string>> values) =>
        values.ToDictionary(pair => pair.Key, pair => pair.Value.Count == 1 ? (object?)pair.Value[0] : pair.Value.ToArray(), StringComparer.Ordinal);
}
