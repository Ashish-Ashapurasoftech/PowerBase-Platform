using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerBase.Application.Import.Qbl;

/// <summary>
/// The PowerBase API endpoint that does what a Quickbase JSON API call (<c>https://api.quickbase.com/v1/...</c>) does. A pipeline
/// Make Request may call the Quickbase API; PowerFlows' PowerBase-account mode runs relative PowerBase paths inside PowerBase, so a
/// mapped call is written as that path (<c>/tables/{table}/records</c>) with the Quickbase table/app id replaced by the imported
/// one. Only endpoints with a real PowerBase equivalent are mapped; anything else is kept as is and reported.
/// Request and response bodies are NOT translated (Quickbase's <c>{"from":…,"where":…}</c> / <c>{"data":[{"6":{"value":…}}]}</c>
/// differ from PowerBase's), which the import reports for every mapped call that has one.
/// </summary>
public static class QuickbaseApiUrlMap
{
    /// <summary>A path part: literal text, or the imported id of a Quickbase table / app.</summary>
    public abstract record Part;
    public sealed record Literal(string Text) : Part;
    public sealed record TableId(string QbTableId) : Part;
    public sealed record AppId(string? QbAppId) : Part;

    public sealed record Mapped(IReadOnlyList<Part> Path, string Method, string Endpoint, bool BodyDiffers);

    private static readonly Regex QbId = new("^[a-z0-9]{9}$", RegexOptions.Compiled);

    public static bool IsQuickbaseApi(Uri url) =>
        url.Host.Equals("api.quickbase.com", StringComparison.OrdinalIgnoreCase)
        || url.Host.EndsWith(".quickbase.com", StringComparison.OrdinalIgnoreCase) && url.AbsolutePath.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase);

    /// <summary>The PowerBase call for <paramref name="method"/> <paramref name="url"/> (body <paramref name="body"/>), or null with
    /// <paramref name="reason"/> when there is none or the ids it needs are not in the request.</summary>
    public static Mapped? Map(string method, Uri url, string? body, out string reason)
    {
        reason = string.Empty;
        method = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
        var segments = url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (segments.Length < 2 || !segments[0].Equals("v1", StringComparison.OrdinalIgnoreCase))
        {
            reason = "it is not a Quickbase JSON API (v1) call";
            return null;
        }
        var query = ParseQuery(url.Query);
        var resource = segments[1].ToLowerInvariant();
        var id = segments.Length > 2 ? segments[2] : null;
        var tail = segments.Length > 3 ? segments[3].ToLowerInvariant() : null;
        string? BodyId(string name) => ReadBodyString(body, name);
        string? TableFrom(params string[] names)
        {
            foreach (var n in names)
                if (query.TryGetValue(n, out var q) && !string.IsNullOrWhiteSpace(q)) return q;
            foreach (var n in names)
                if (BodyId(n) is { Length: > 0 } b) return b;
            return null;
        }
        string? failure = null;
        Mapped? NeedTable(string? table, Func<string, Mapped> build)
        {
            if (table is null || !QbId.IsMatch(table))
            {
                failure = table is null ? "the request does not say which table it reads (no tableId / from / to)"
                                       : $"the table id '{table}' is not a fixed Quickbase table id";
                return null;
            }
            return build(table);
        }
        Mapped Table(string table, string suffix, string newMethod, string endpoint, bool bodyDiffers) =>
            new([new Literal("/tables/"), new TableId(table), new Literal(suffix)], newMethod, endpoint, bodyDiffers);

        var result = Pick();
        if (result is null) reason = failure ?? $"PowerBase has no endpoint for Quickbase '{method} {url.AbsolutePath}'";
        return result;

        Mapped? Pick()
        {
        switch (resource)
        {
            case "apps" when id is not null && tail is null && method == "GET":
                return new Mapped([new Literal("/apps/"), new AppId(QbId.IsMatch(id) ? id : null)], "GET", "GET /apps/{app}", false);
            case "tables" when id is null && method == "GET":
                return query.TryGetValue("appId", out var appId)
                    ? new Mapped([new Literal("/apps/"), new AppId(QbId.IsMatch(appId) ? appId : null), new Literal("/tables")], "GET", "GET /apps/{app}/tables", false)
                    : Fail("the request does not say which app (appId)");
            case "tables" when id is not null && tail is null && method == "GET":
                return NeedTable(id, t => Table(t, "", "GET", "GET /tables/{table}", false));
            case "fields" when id is null && method == "GET":
                return NeedTable(TableFrom("tableId"), t => Table(t, "/fields", "GET", "GET /tables/{table}/fields", false));
            case "reports" when id is null && method == "GET":
                return NeedTable(TableFrom("tableId"), t => Table(t, "/reports", "GET", "GET /tables/{table}/reports", false));
            case "records" when id is not null && id.Equals("query", StringComparison.OrdinalIgnoreCase):
                // Quickbase "Query for data" (POST, body {from, select, where, sortBy, options}) → the table's record list.
                return NeedTable(TableFrom("from", "tableId"), t => Table(t, "/records", "GET", "GET /tables/{table}/records", !string.IsNullOrWhiteSpace(body)));
            case "records" when id is null && method == "POST":
                // "Insert/Update record(s)" (body {to, data, mergeFieldId}) → bulk create.
                return NeedTable(TableFrom("to", "tableId"), t => Table(t, "/records/bulk-create", "POST", "POST /tables/{table}/records/bulk-create", true));
            case "records" when id is null && method == "DELETE":
                // "Delete record(s)" (body {from, where}) → bulk delete by record ids.
                return NeedTable(TableFrom("from", "tableId"), t => Table(t, "/records/bulk-delete", "POST", "POST /tables/{table}/records/bulk-delete", true));
        }
        return null;
        }

        Mapped? Fail(string why)
        {
            failure = why;
            return null;
        }
    }

    private static Dictionary<string, string> ParseQuery(string query)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            result[Uri.UnescapeDataString(kv[0])] = kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty;
        }
        return result;
    }

    private static string? ReadBodyString(string? body, string name)
    {
        if (string.IsNullOrWhiteSpace(body) || body.Contains("{{", StringComparison.Ordinal)) return null;
        try
        {
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(name, out var value)
                   && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (JsonException) { return null; }
    }
}
