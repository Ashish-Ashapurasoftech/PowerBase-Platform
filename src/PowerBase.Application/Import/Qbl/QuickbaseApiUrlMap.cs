using System.Text.Json;
using System.Text.RegularExpressions;

namespace PowerBase.Application.Import.Qbl;

/// <summary>
/// The PowerBase API endpoint that does what a Quickbase JSON API call (<c>https://api.quickbase.com/v1/...</c>) does. A pipeline
/// Make Request may call the Quickbase API; PowerFlows' PowerBase-account mode runs relative PowerBase paths inside PowerBase.
/// <para>PowerBase has a Quickbase-compatible v1 API (same paths, same request and response bodies, field ids = Quickbase fids), so
/// a supported call keeps its path (<c>/v1/records/query</c>) and body; only the Quickbase table / app ids — in the path, the query
/// string and the body's <c>from</c> / <c>to</c> / <c>tableId</c> — are replaced by the imported ones.</para>
/// <para>A Quickbase call the compatible API does not have but a native PowerBase endpoint does (get app, get table) is mapped to
/// that native endpoint, whose response differs (reported). Anything else is kept as is and reported.</para>
/// </summary>
public static class QuickbaseApiUrlMap
{
    /// <summary>A path part: literal text, or the imported id of a Quickbase table / app.</summary>
    public abstract record Part;
    public sealed record Literal(string Text) : Part;
    public sealed record TableId(string QbTableId) : Part;
    public sealed record AppId(string? QbAppId) : Part;
    /// <summary>An XML API dbid: the imported table when it is a table id, else the imported app.</summary>
    public sealed record XmlDbId(string QbId) : Part;

    /// <param name="Path">The new URL (relative, with its query string).</param>
    /// <param name="BodyTableIds">Quickbase table ids written in the body (<c>"from": "bxxxxxxxx"</c>) that must be replaced.</param>
    /// <param name="ResponseDiffers">The target is a native endpoint whose request/response format differs from Quickbase's.</param>
    /// <param name="Review">Why the mapped call still needs a check before the PowerFlow is turned on (null = runs as is).</param>
    public sealed record Mapped(IReadOnlyList<Part> Path, string Method, string Endpoint, IReadOnlyList<string> BodyTableIds,
        bool ResponseDiffers, string? Review = null)
    {
        /// <summary>Old name kept for callers: true when the body or response must be checked.</summary>
        public bool BodyDiffers => ResponseDiffers;
    }

    private static readonly Regex QbId = new("^[a-z0-9]{9}$", RegexOptions.Compiled);

    public static bool IsQuickbaseApi(Uri url) =>
        url.Host.Equals("api.quickbase.com", StringComparison.OrdinalIgnoreCase)
        || url.Host.EndsWith(".quickbase.com", StringComparison.OrdinalIgnoreCase) && (url.AbsolutePath.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase) || url.AbsolutePath.StartsWith("/db/", StringComparison.OrdinalIgnoreCase));

    /// <summary>A Quickbase-channel request may give only the path of the realm's API (/db/{dbid}?a=API_x, /v1/...): the full
    /// Quickbase address it calls. Anything else is returned unchanged.</summary>
    public static string? AbsoluteQuickbaseUrl(string? url)
    {
        var trimmed = url?.Trim();
        if (trimmed is null) return null;
        if (trimmed.StartsWith("/v1/", StringComparison.OrdinalIgnoreCase)) return "https://api.quickbase.com" + trimmed;
        if (trimmed.StartsWith("/db/", StringComparison.OrdinalIgnoreCase)) return "https://realm.quickbase.com" + trimmed;
        return trimmed;
    }

    /// <summary>Quickbase XML API actions the PowerBase /db/{dbid} endpoint answers.</summary>
    public static readonly IReadOnlySet<string> XmlActions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "API_AddRecord", "API_EditRecord", "API_DeleteRecord", "API_GetFieldProperties", "API_UploadFile", "API_GetUserInfo",
        "API_GetUserRole", "API_UserRoles", "API_AddUserToRole", "API_ProvisionUser", "API_SendInvitation", "API_ImportFromCSV",
        "API_RunImport", "API_GenAddRecordForm", "API_DoQuery", "API_DoQueryCount",
    };

    /// <summary>Credentials Quickbase reads from the XML request; the PowerBase account authenticates instead.</summary>
    private static readonly HashSet<string> XmlCredentialParams = new(StringComparer.OrdinalIgnoreCase) { "usertoken", "ticket", "apptoken" };

    /// <summary>The PowerBase call for <paramref name="method"/> <paramref name="url"/> (body <paramref name="body"/>), or null with
    /// <paramref name="reason"/> when there is none.</summary>
    public static Mapped? Map(string method, Uri url, string? body, out string reason)
    {
        reason = string.Empty;
        method = string.IsNullOrWhiteSpace(method) ? "GET" : method.Trim().ToUpperInvariant();
        var segments = url.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.UnescapeDataString).ToArray();
        if (segments.Length == 2 && segments[0].Equals("db", StringComparison.OrdinalIgnoreCase))
            return MapXml(method, segments[1], url, body, out reason);
        if (segments.Length < 2 || !segments[0].Equals("v1", StringComparison.OrdinalIgnoreCase))
        {
            reason = "it is not a Quickbase JSON API (v1) call";
            return null;
        }
        var query = ParseQuery(url.Query);
        var resource = segments[1].ToLowerInvariant();
        var id = segments.Length > 2 ? segments[2] : null;
        var tail = segments.Length > 3 ? segments[3].ToLowerInvariant() : null;
        var bodyIds = BodyTableIds(body);
        string? failure = null;

        // Compatible v1 endpoint: same path, the Quickbase ids in the query string replaced.
        Mapped V1(string path, string newMethod, string endpoint, string? review = null)
        {
            var parts = new List<Part> { new Literal(path) };
            var first = true;
            foreach (var (key, value) in query)
            {
                parts.Add(new Literal((first ? "?" : "&") + Uri.EscapeDataString(key) + "="));
                first = false;
                if (key.Equals("tableId", StringComparison.OrdinalIgnoreCase) && QbId.IsMatch(value)) parts.Add(new TableId(value));
                else if (key.Equals("appId", StringComparison.OrdinalIgnoreCase)) parts.Add(new AppId(QbId.IsMatch(value) ? value : null));
                else parts.Add(new Literal(Uri.EscapeDataString(value)));
            }
            return new Mapped(Merge(parts), newMethod, endpoint, bodyIds, false, review);
        }
        Mapped? Fail(string why)
        {
            failure = why;
            return null;
        }

        var result = Pick();
        if (result is null) reason = failure ?? $"PowerBase has no endpoint for Quickbase '{method} {url.AbsolutePath}'";
        return result;

        Mapped? Pick()
        {
            switch (resource)
            {
                case "records" when id is not null && id.Equals("query", StringComparison.OrdinalIgnoreCase) && tail is null:
                    // Quickbase only answers POST here; a GET (seen in real files) is sent as POST.
                    return V1("/v1/records/query", "POST", "POST /v1/records/query",
                        string.IsNullOrWhiteSpace(body) ? "the request has no body; Quickbase \"Query for data\" needs {\"from\": \"<table id>\", ...}" : null);
                case "records" when id is null && method == "POST":
                    return V1("/v1/records", "POST", "POST /v1/records",
                        string.IsNullOrWhiteSpace(body) ? "the request has no body; Quickbase \"Insert/Update records\" needs {\"to\": \"<table id>\", \"data\": [...]}" : null);
                case "records" when id is null && method == "DELETE":
                    return V1("/v1/records", "DELETE", "DELETE /v1/records",
                        string.IsNullOrWhiteSpace(body) ? "the request has no body; Quickbase \"Delete records\" needs {\"from\": \"<table id>\", \"where\": \"...\"}" : null);
                case "tables" when id is null && method == "GET":
                    return query.ContainsKey("appId") ? V1("/v1/tables", "GET", "GET /v1/tables") : Fail("the request does not say which app (appId)");
                case "fields" when id is null && method == "GET":
                    return query.ContainsKey("tableId") ? V1("/v1/fields", "GET", "GET /v1/fields") : Fail("the request does not say which table (tableId)");
                case "reports" when id is not null && method == "GET" && tail is null:
                    return query.ContainsKey("tableId")
                        ? V1("/v1/reports/" + Uri.EscapeDataString(id), "GET", "GET /v1/reports/{reportId}", ReportReview(id))
                        : Fail("the request does not say which table (tableId)");
                case "reports" when id is not null && method == "POST" && tail == "run":
                    return query.ContainsKey("tableId")
                        ? V1("/v1/reports/" + Uri.EscapeDataString(id) + "/run", "POST", "POST /v1/reports/{reportId}/run", ReportReview(id))
                        : Fail("the request does not say which table (tableId)");

                case "files" when segments.Length == 6 && method is "GET" or "DELETE" && segments.Length == 6:
                    // Download / delete a file version: /v1/files/{table}/{record}/{field}/{version}.
                    if (!QbId.IsMatch(segments[2])) return Fail($"the table id '{segments[2]}' is not a fixed Quickbase table id");
                    return new Mapped(Merge([new Literal("/v1/files/"), new TableId(segments[2]),
                        new Literal("/" + Uri.EscapeDataString(segments[3]) + "/" + Uri.EscapeDataString(segments[4]) + "/" + Uri.EscapeDataString(segments[5]))]),
                        method, method + " /v1/files/{table}/{record}/{field}/{version}", bodyIds, false);

                // Not in the compatible API: the native endpoint (other response format).
                case "apps" when id is not null && tail is null && method == "GET":
                    return new Mapped([new Literal("/apps/"), new AppId(QbId.IsMatch(id) ? id : null)], "GET", "GET /apps/{app}", bodyIds, true);
                case "tables" when id is not null && tail is null && method == "GET":
                    return QbId.IsMatch(id)
                        ? new Mapped([new Literal("/tables/"), new TableId(id)], "GET", "GET /tables/{table}", bodyIds, true)
                        : Fail($"the table id '{id}' is not a fixed Quickbase table id");
            }
            return null;
        }
    }

    /// <summary>Quickbase XML API <c>/db/{dbid}?a=API_x</c> → PowerBase's <c>/db/{id}</c>, same action, parameters and XML body.
    /// The dbid is a table or an app; which one is decided by the caller (<see cref="TableId"/> first, then <see cref="AppId"/>).</summary>
    private static Mapped? MapXml(string method, string dbid, Uri url, string? body, out string reason)
    {
        reason = string.Empty;
        var query = ParseQuery(url.Query);
        var action = query.FirstOrDefault(p => p.Key.Equals("a", StringComparison.OrdinalIgnoreCase) || p.Key.Equals("act", StringComparison.OrdinalIgnoreCase)).Value;
        if (string.IsNullOrWhiteSpace(action) && body is not null && Regex.Match(body, "<qdbapi>", RegexOptions.IgnoreCase).Success)
            action = null; // the action is then in the QUICKBASE-ACTION header, which the caller keeps
        if (action is not null && !XmlActions.Contains(action))
        {
            reason = $"PowerBase has no XML API action '{action}'";
            return null;
        }
        var parts = new List<Part> { new Literal("/db/") };
        if (dbid.Equals("main", StringComparison.OrdinalIgnoreCase)) parts.Add(new Literal("main"));
        else if (QbId.IsMatch(dbid)) parts.Add(new XmlDbId(dbid));
        else
        {
            reason = $"the dbid '{dbid}' is not a fixed Quickbase id";
            return null;
        }
        var first = true;
        foreach (var (key, value) in query)
        {
            if (XmlCredentialParams.Contains(key)) continue;
            parts.Add(new Literal((first ? "?" : "&") + Uri.EscapeDataString(key) + "=" + Uri.EscapeDataString(value)));
            first = false;
        }
        var hasCredentialsInBody = body is not null && Regex.IsMatch(body, "<(usertoken|ticket|apptoken)>", RegexOptions.IgnoreCase);
        // API_DoQuery / API_DoQueryCount: qid is the Quickbase query number of the report. PowerBase numbers a table's reports the
        // same way (List All 1, List Changes 2, then 5, 6… in the order they were made), but an imported table's other reports may
        // get other numbers than in Quickbase — so a numeric qid is checked once; qname (the report name) needs no check.
        var qid = query.FirstOrDefault(p => p.Key.Equals("qid", StringComparison.OrdinalIgnoreCase)).Value?.Trim();
        var qidReview = action is not null && action.StartsWith("API_DoQuery", StringComparison.OrdinalIgnoreCase)
                        && qid is { Length: > 0 } && qid is not ("1" or "2")
            ? $"qid '{qid}' is the Quickbase query number of a report; check it is the same report in PowerBase (or use qname=<report name>)"
            : null;
        // API_RunImport: <id> is the number of a Quickbase saved import. PowerBase's saved imports have their own ids, and saved imports
        // are not part of an application export, so the import must exist in PowerBase and its id be set (action in the URL, or the
        // QUICKBASE-ACTION header with an <id> in the XML body).
        var importId = query.FirstOrDefault(p => p.Key.Equals("id", StringComparison.OrdinalIgnoreCase)).Value?.Trim()
                       ?? (body is null ? null : Regex.Match(body, @"<id>\s*([^<]+?)\s*</id>", RegexOptions.IgnoreCase) is { Success: true } m ? m.Groups[1].Value : null);
        var runImport = string.Equals(action, "API_RunImport", StringComparison.OrdinalIgnoreCase)
                        || (action is null && body is not null && Regex.IsMatch(body, "<qdbapi>", RegexOptions.IgnoreCase) && importId is not null);
        var importReview = runImport
            ? $"it runs Quickbase saved import '{importId ?? "?"}'; saved imports are not in the application export and PowerBase numbers its own imports, so create that import on the table in PowerBase and put its id (or Guid) in <id>"
            : null;
        var reviews = new[]
        {
            hasCredentialsInBody ? "the XML body carries Quickbase credentials (usertoken / ticket / apptoken); remove them, the PowerBase account signs the call" : null,
            qidReview, importReview,
        }.OfType<string>().ToList();
        // PowerBase answers GET and POST like Quickbase; the method is kept.
        return new Mapped(Merge(parts), method is "GET" ? "GET" : "POST", (method is "GET" ? "GET" : "POST") + " /db/{dbid}" + (action is null ? "" : "?a=" + action), Array.Empty<string>(), false,
            reviews.Count == 0 ? null : string.Join("; and ", reviews));
    }

    /// <summary>Quickbase report ids are numbers per table (1, 2, …); PowerBase report ids are its own, so the id must be chosen again.</summary>
    private static string ReportReview(string reportId) =>
        $"report id '{reportId}' is the Quickbase report number; replace it with the PowerBase report id (its Guid) of the same report";

    private static List<Part> Merge(List<Part> parts)
    {
        var merged = new List<Part>();
        foreach (var p in parts)
            if (p is Literal l && merged.Count > 0 && merged[^1] is Literal prev) merged[^1] = new Literal(prev.Text + l.Text);
            else merged.Add(p);
        return merged;
    }

    /// <summary>The Quickbase table ids written as <c>from</c> / <c>to</c> / <c>tableId</c> in a JSON body. A body with Jinja is
    /// read by pattern (it is not JSON until it runs).</summary>
    public static IReadOnlyList<string> BodyTableIds(string? body)
    {
        if (string.IsNullOrWhiteSpace(body)) return Array.Empty<string>();
        var found = new List<string>();
        foreach (Match m in Regex.Matches(body, "\"(?:from|to|tableId)\"\\s*:\\s*\"([a-z0-9]{9})\""))
            if (!found.Contains(m.Groups[1].Value)) found.Add(m.Groups[1].Value);
        return found;
    }

    /// <summary>Splits <paramref name="text"/> around each quoted Quickbase table id of <paramref name="ids"/> that follows a
    /// from / to / tableId key, so the caller can put the imported table id in its place. Null when nothing is replaced.</summary>
    public static IReadOnlyList<Part>? SplitBody(string text, IReadOnlyCollection<string> ids)
    {
        if (ids.Count == 0) return null;
        var parts = new List<Part>();
        var last = 0;
        foreach (Match m in Regex.Matches(text, "(\"(?:from|to|tableId)\"\\s*:\\s*\")([a-z0-9]{9})\""))
        {
            var dbid = m.Groups[2].Value;
            if (!ids.Contains(dbid)) continue;
            parts.Add(new Literal(text[last..m.Groups[2].Index]));
            parts.Add(new TableId(dbid));
            last = m.Groups[2].Index + m.Groups[2].Length;
        }
        if (last == 0) return null;
        parts.Add(new Literal(text[last..]));
        return Merge(parts.Where(p => p is not Literal { Text.Length: 0 }).ToList());
    }

    /// <summary>The Quickbase tables a Quickbase API call reads or writes (query string tableId, /v1/tables/{id}, XML dbid, body
    /// from / to / tableId), and — when it names exactly one — the field ids it uses (body data keys, select, fieldsToReturn,
    /// sortBy / groupBy fieldId, mergeFieldId, where <c>{6.EX.…}</c>, XML <c>_fid_6</c> / <c>fid="6"</c>). The pipeline-only
    /// import uses them to find the matching existing table.</summary>
    public static IReadOnlyList<(string QbTableId, IReadOnlyList<int> Fids)> Evidence(string? url, string? body)
    {
        if (url is null || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) || !IsQuickbaseApi(uri)) return Array.Empty<(string, IReadOnlyList<int>)>();
        var tables = new List<string>();
        void Add(string? id) { if (id is not null && QbId.IsMatch(id) && !tables.Contains(id)) tables.Add(id); }
        var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var query = ParseQuery(uri.Query);
        foreach (var (k, v) in query) if (k.Equals("tableId", StringComparison.OrdinalIgnoreCase)) Add(v);
        if (segments.Length >= 3 && segments[0] == "v1" && segments[1] == "tables") Add(segments[2]);
        if (segments.Length == 2 && segments[0].Equals("db", StringComparison.OrdinalIgnoreCase)) Add(segments[1]);
        foreach (var id in BodyTableIds(body)) Add(id);
        if (tables.Count != 1) return tables.Select(t => (t, (IReadOnlyList<int>)Array.Empty<int>())).ToList();

        var fids = new HashSet<int>();
        void Fid(string text) { if (int.TryParse(text, out var f) && f > 0) fids.Add(f); }
        var text = (body ?? "") + "\n" + Uri.UnescapeDataString(uri.Query);
        foreach (Match m in Regex.Matches(text, @"""(\d+)""\s*:\s*\{\s*""value""")) Fid(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(text, @"""(?:select|fieldsToReturn)""\s*:\s*\[([^\]]*)\]"))
            foreach (Match n in Regex.Matches(m.Groups[1].Value, @"\d+")) Fid(n.Value);
        foreach (Match m in Regex.Matches(text, @"""(?:fieldId|mergeFieldId)""\s*:\s*""?(\d+)")) Fid(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(text, @"\{'?(\d+)'?\.[A-Za-z]{2,4}\.")) Fid(m.Groups[1].Value);
        foreach (Match m in Regex.Matches(text, @"_fid_(\d+)|fid=""(\d+)""")) Fid(m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value);
        return new[] { (tables[0], (IReadOnlyList<int>)fids.OrderBy(f => f).ToList()) };
    }

    private static List<KeyValuePair<string, string>> ParseQuery(string query)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var pair in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var kv = pair.Split('=', 2);
            result.Add(new(Uri.UnescapeDataString(kv[0]), kv.Length > 1 ? Uri.UnescapeDataString(kv[1]) : string.Empty));
        }
        return result;
    }

    // Kept for callers reading a single body key.
    internal static string? ReadBodyString(string? body, string name)
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

internal static class QueryListExtensions
{
    public static bool ContainsKey(this List<KeyValuePair<string, string>> list, string key) =>
        list.Any(p => p.Key.Equals(key, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(p.Value));
}
