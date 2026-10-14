using System.Text.Json;
using System.Text.Json.Nodes;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// Field names a step only learns from real traffic, kept in the step's ConfigJson so the pipeline builder can offer them to
/// later steps — the way Quickbase lists an Incoming Request's url_params / body_params once a request has been consumed:
/// the query-string and body parameter names of requests that reached an Incoming Request trigger, and the top-level fields
/// of a Make Request response. The server writes them; the editor only reads them. So a save keeps them even when the
/// editor's copy predates them, and a change in them (or in the field list the editor derives from them) is not an edit.
/// </summary>
public static class PipelineSampleKeys
{
    public const string UrlParams = "sampleUrlParamKeys";
    public const string BodyParams = "sampleBodyParamKeys";
    public const string ResponseFields = "sampleResponseKeys";
    public const int MaxKeys = 100;
    private const int MaxKeyLength = 200;

    private static readonly string[] Learned = { UrlParams, BodyParams, ResponseFields };
    /// <summary>Learned names plus the output field list the editor derives from the config.</summary>
    private static readonly string[] Derived = { UrlParams, BodyParams, ResponseFields, "responseFields" };

    /// <summary>Adds newly seen names to the config's lists (at most <see cref="MaxKeys"/> each). Returns the updated JSON,
    /// or null when nothing new was seen.</summary>
    public static string? Merge(string? configJson, IReadOnlyDictionary<string, IEnumerable<string>> observed)
    {
        if (Parse(configJson) is not JsonObject node) return null;
        var changed = false;
        foreach (var (property, names) in observed)
        {
            var known = Read(node, property);
            var merged = known.ToList();
            foreach (var name in names)
            {
                if (merged.Count >= MaxKeys) break;
                if (string.IsNullOrWhiteSpace(name) || name.Length > MaxKeyLength) continue;
                if (merged.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                merged.Add(name);
            }
            if (merged.Count == known.Count) continue;
            node[property] = new JsonArray(merged.Select(k => (JsonNode?)JsonValue.Create(k)).ToArray());
            changed = true;
        }
        return changed ? node.ToJsonString() : null;
    }

    /// <summary>The config to store on save: the editor's, plus every name the server had already learned for the step.</summary>
    public static string? PreserveOnSave(string? savedConfigJson, string? incomingConfigJson)
    {
        if (Parse(savedConfigJson) is not JsonObject saved || !Learned.Any(p => Read(saved, p).Count > 0)) return incomingConfigJson;
        var observed = Learned.ToDictionary(p => p, p => (IEnumerable<string>)Read(saved, p));
        return Merge(incomingConfigJson, observed) ?? incomingConfigJson;
    }

    /// <summary>The config without what the server learns or the editor derives — the part that is the user's edit.</summary>
    public static JsonNode? WithoutDerived(JsonNode? config)
    {
        if (config is not JsonObject obj || !Derived.Any(obj.ContainsKey)) return config;
        var copy = (JsonObject)obj.DeepClone();
        foreach (var property in Derived) copy.Remove(property);
        return copy;
    }

    private static List<string> Read(JsonObject node, string property) =>
        node[property] is JsonArray array
            ? array.Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null).Where(s => !string.IsNullOrEmpty(s)).ToList()!
            : new List<string>();

    private static JsonNode? Parse(string? json)
    {
        try { return JsonNode.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json); }
        catch (JsonException) { return null; }
    }
}
