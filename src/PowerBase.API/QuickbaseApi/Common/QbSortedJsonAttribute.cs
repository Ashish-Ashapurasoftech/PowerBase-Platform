using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Options;

namespace PowerBase.API.QuickbaseApi.Common;

/// <summary>
/// Quickbase REST writes every object of a success body with its keys in ordinal (alphabetical) order — data cells too
/// ("10","11","3","6"), metadata ("numFields","numRecords","skip","top","totalRecords") — live realm 2026-11-27.
/// Error bodies keep their own order ("message","description"), so only 2xx object results are rewritten.
/// </summary>
public sealed class QbSortedJsonAttribute : ResultFilterAttribute
{
    public override void OnResultExecuting(ResultExecutingContext context)
    {
        if (context.Result is not ObjectResult { Value: not null } result) return;
        var status = result.StatusCode ?? StatusCodes.Status200OK;
        if (status < 200 || status >= 300 || result.Value is QbErrorBody) return;
        var options = context.HttpContext.RequestServices.GetRequiredService<IOptions<JsonOptions>>().Value.JsonSerializerOptions;
        var node = JsonSerializer.SerializeToNode(result.Value, result.Value.GetType(), options);
        // Quickbase writes & < > ' and non-ASCII as they are, escaping only quotes and backslashes ("[T Text] & \"!\"").
        var write = new JsonSerializerOptions(options) { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = false };
        context.Result = new ContentResult
        {
            StatusCode = status,
            ContentType = "application/json; charset=utf-8",
            Content = AsciiEscape(Sort(node)?.ToJsonString(write) ?? "null"),
        };
    }

    /// <summary>Quickbase JSON escapes every non-ASCII character as \uXXXX with upper-case hex (live realm: "Ü" → "\u00DC").</summary>
    public static string AsciiEscape(string json)
    {
        if (json.All(c => c < 128)) return json;
        var sb = new System.Text.StringBuilder(json.Length + 32);
        foreach (var c in json)
            if (c < 128) sb.Append(c); else sb.Append("\\u").Append(((int)c).ToString("X4"));
        return sb.ToString();
    }

    public static JsonNode? Sort(JsonNode? node) => node switch
    {
        JsonObject o => new JsonObject(o.OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => KeyValuePair.Create(p.Key, Sort(p.Value?.DeepClone())))),
        JsonArray a => new JsonArray(a.Select(x => Sort(x?.DeepClone())).ToArray()),
        _ => node?.DeepClone(),
    };
}
