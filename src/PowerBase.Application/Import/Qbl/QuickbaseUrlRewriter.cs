using System.Text;
using System.Text.RegularExpressions;

namespace PowerBase.Application.Import.Qbl;

/// <summary>
/// Rewrites every Quickbase URL inside a PowerFlow value (still in Quickbase syntax, Jinja tokens included) to the PowerBase
/// URL that does the same, so an imported PowerFlow never points at Quickbase:
/// <list type="bullet">
/// <item>JSON API <c>https://api.quickbase.com/v1/…</c> and XML API <c>https://realm.quickbase.com/db/{dbid}?a=API_…</c> →
/// the PowerBase Quickbase-compatible endpoint (<see cref="QuickbaseApiUrlMap"/>), relative inside a Make Request, else on the
/// PowerBase API base URL.</item>
/// <item>Quickbase pages (<c>/db/{dbid}?a=dr&amp;rid=…</c>, <c>/nav/app/{app}/table/{t}/action/dr?rid=…</c>, reports, add
/// record, app home…) → the PowerBase page (<c>/app/{app}/tables/{t}/records/{record}/view</c> …) on the PowerBase site.</item>
/// <item>Anything with no PowerBase equivalent is removed and reported, never kept.</item>
/// </list>
/// Imported ids are written as markers (<see cref="Marker"/>) that the converter turns into table / app values; a Record ID#
/// token used as <c>rid</c> (<c>{{aa.id}}</c>) becomes <c>{{aa.__pb_record}}</c>, the record's PowerBase id.
/// </summary>
public static class QuickbaseUrlRewriter
{
    public const char MARK_START = '\u0001';
    public const char MARK_END = '\u0002';
    /// <summary>Marker kinds: table id, the app of a table, app id, the single imported app, PowerBase site, PowerBase API.</summary>
    public const string TABLE = "T", APP_OF_TABLE = "AT", APP = "A", ANY_APP = "A*", SITE = "U", API = "P";
    public const string RECORD_KEY = "__pb_record";

    public static string Marker(string kind, string? id = null) => MARK_START + kind + (id is null ? "" : ":" + id) + MARK_END;
    public static readonly Regex MarkerPattern = new("\u0001([A-Z*]+)(?::([^\u0002]*))?\u0002", RegexOptions.Compiled);

    /// <summary>A Quickbase URL: any *.quickbase.com host, with or without a scheme, up to the first space / quote / bracket
    /// that is not inside a Jinja token.</summary>
    public static readonly Regex UrlPattern = new(
        @"(?<![@\w.-])(?:https?://)?(?<host>(?:[A-Za-z0-9-]+\.)*quickbase\.com)(?![A-Za-z0-9.-])(?<rest>(?:\{\{[^}]*\}\}|\{%[^%]*%\}|[^\s""'<>\]\)\\,;|])*)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex Token = new(@"\{\{[^}]*\}\}|\{%[^%]*%\}", RegexOptions.Compiled);
    private static readonly Regex QbId = new("^[a-z0-9]{9}$", RegexOptions.Compiled);
    private static readonly Regex RecordIdToken = new(@"^\{\{\s*([A-Za-z_][A-Za-z0-9_]*)\.(?:id|record_id_?|record_id_number)\s*\}\}$", RegexOptions.Compiled);
    private static readonly HashSet<string> Credentials = new(StringComparer.OrdinalIgnoreCase) { "usertoken", "ticket", "apptoken" };

    public static bool ContainsQuickbaseUrl(string? text) => text is not null && UrlPattern.IsMatch(text);

    /// <param name="IsTable">True for a Quickbase table id that was imported.</param>
    /// <param name="IsApp">True for a Quickbase app id that was imported.</param>
    /// <param name="InRequestUrl">The value is a PowerBase-account Make Request URL: API calls become relative paths.</param>
    public sealed record Context(Func<string, bool> IsTable, Func<string, bool> IsApp, bool InRequestUrl = false, string Method = "GET", string? Body = null);

    /// <param name="Problems">What still needs a person (the value makes the step a draft).</param>
    /// <param name="Notes">What was changed, for the import report.</param>
    public sealed record Result(string Text, IReadOnlyList<string> Notes, IReadOnlyList<string> Problems, bool Changed);

    public static Result Rewrite(string text, Context context)
    {
        var notes = new List<string>();
        var problems = new List<string>();
        if (!ContainsQuickbaseUrl(text)) return new Result(text, notes, problems, false);
        var output = UrlPattern.Replace(text, m => One(m.Value, m.Groups["host"].Value, m.Groups["rest"].Value, context, notes, problems));
        return new Result(output, notes, problems, true);
    }

    private static string One(string original, string host, string rest, Context ctx, List<string> notes, List<string> problems)
    {
        // Tokens are protected while the URL is taken apart; they come back unchanged.
        var tokens = new List<string>();
        var safe = Token.Replace(rest, t => { tokens.Add(t.Value); return $"qbtok{tokens.Count - 1}z"; });
        string Restore(string s) => Regex.Replace(s, "qbtok(\\d+)z", t => tokens[int.Parse(t.Groups[1].Value)]);
        string Remove(string why)
        {
            problems.Add($"'{original}' {why}; it was removed. Put the PowerBase address there");
            return string.Empty;
        }

        var hashAt = safe.IndexOf('#');
        if (hashAt >= 0) safe = safe[..hashAt];
        var queryAt = safe.IndexOf('?');
        var path = queryAt >= 0 ? safe[..queryAt] : safe;
        var query = queryAt >= 0 ? safe[(queryAt + 1)..] : string.Empty;
        var segments = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        var pairs = query.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p => p.Split('=', 2)).Select(p => (Key: p[0], Value: p.Length > 1 ? p[1] : string.Empty)).ToList();
        string? Q(params string[] keys) => pairs.FirstOrDefault(p => keys.Any(k => p.Key.Equals(k, StringComparison.OrdinalIgnoreCase))).Value is { Length: > 0 } v ? v : null;
        var action = Q("a", "act");

        // --- APIs: JSON v1 and XML API_* --------------------------------------------------------------------------------------
        var isJson = segments.Length > 0 && segments[0].Equals("v1", StringComparison.OrdinalIgnoreCase);
        var isXml = segments.Length >= 2 && segments[0].Equals("db", StringComparison.OrdinalIgnoreCase)
                    && action is not null && action.StartsWith("API_", StringComparison.OrdinalIgnoreCase);
        if (isJson || isXml)
        {
            var prefix = ctx.InRequestUrl ? string.Empty : Marker(API);
            if (isXml && !QbId.IsMatch(segments[1]) && !segments[1].Equals("main", StringComparison.OrdinalIgnoreCase))
            {
                // A dbid computed at run time: kept as is on the PowerBase XML endpoint; the value must be a PowerBase id there.
                if (!QuickbaseApiUrlMap.XmlActions.Contains(action!)) return Remove($"calls Quickbase XML action '{action}', which PowerBase does not have");
                problems.Add($"'{original}' builds its dbid at run time ({Restore(segments[1])}); it now calls PowerBase, so that value must give the PowerBase table id");
                var kept = pairs.Where(p => !Credentials.Contains(p.Key)).Select(p => p.Key + "=" + p.Value);
                return prefix + Restore("/db/" + segments[1] + "?" + string.Join("&", kept));
            }
            if (!Uri.TryCreate("https://" + host + safe, UriKind.Absolute, out var uri))
                return Remove("is not a valid address");
            var mapped = QuickbaseApiUrlMap.Map(ctx.Method, uri, ctx.Body, out var reason);
            if (mapped is null) return Remove($"calls the Quickbase API and {Lower(reason)}");
            var sb = new StringBuilder(prefix);
            foreach (var part in mapped.Path)
                switch (part)
                {
                    case QuickbaseApiUrlMap.Literal l: sb.Append(Restore(l.Text)); break;
                    case QuickbaseApiUrlMap.TableId t when ctx.IsTable(t.QbTableId): sb.Append(Marker(TABLE, t.QbTableId)); break;
                    case QuickbaseApiUrlMap.XmlDbId d when ctx.IsTable(d.QbId): sb.Append(Marker(TABLE, d.QbId)); break;
                    case QuickbaseApiUrlMap.XmlDbId d when ctx.IsApp(d.QbId): sb.Append(Marker(APP, d.QbId)); break;
                    case QuickbaseApiUrlMap.AppId a when a.QbAppId is null || ctx.IsApp(a.QbAppId): sb.Append(Marker(a.QbAppId is null ? ANY_APP : APP, a.QbAppId)); break;
                    default: return Remove("names a Quickbase table or app that is not imported");
                }
            notes.Add($"'{original}' now calls PowerBase '{mapped.Endpoint}'");
            if (mapped.Review is { } review) problems.Add($"'{original}' now calls PowerBase '{mapped.Endpoint}', but {review}");
            if (mapped.ResponseDiffers) problems.Add($"'{original}' now calls PowerBase '{mapped.Endpoint}', whose response differs from Quickbase's");
            return sb.ToString();
        }

        // --- Pages ------------------------------------------------------------------------------------------------------------
        string? table = null, app = null, page = null;
        if (segments.Length >= 2 && segments[0].Equals("db", StringComparison.OrdinalIgnoreCase))
        {
            if (!segments[1].Equals("main", StringComparison.OrdinalIgnoreCase))
            {
                if (ctx.IsApp(segments[1])) app = segments[1];
                else table = segments[1];
            }
            page = (action ?? (segments.Length > 2 && segments[2].Equals("form", StringComparison.OrdinalIgnoreCase) ? "dr" : null))?.ToLowerInvariant();
        }
        else if (segments.Length >= 3 && segments[0].Equals("nav", StringComparison.OrdinalIgnoreCase) && segments[1].Equals("app", StringComparison.OrdinalIgnoreCase))
        {
            app = segments[2];
            if (segments.Length >= 5 && segments[3].Equals("table", StringComparison.OrdinalIgnoreCase)) table = segments[4];
            var at = Array.FindIndex(segments, s => s.Equals("action", StringComparison.OrdinalIgnoreCase));
            page = at >= 0 && at + 1 < segments.Length ? segments[at + 1].ToLowerInvariant() : null;
        }
        else if (segments.Length == 0)
        {
            notes.Add($"'{original}' now points at the PowerBase home page");
            return Marker(SITE) + "/app/apps";
        }
        else return Remove("is a Quickbase page PowerBase has no equivalent for");

        if (table is not null && !ctx.IsTable(table))
        {
            if (Token.IsMatch(Restore(table)) || table.StartsWith("qbtok", StringComparison.Ordinal))
                return Remove($"builds its table id at run time ({Restore(table)})");
            return Remove($"opens Quickbase table '{table}', which is not imported");
        }
        if (table is null && app is not null && !ctx.IsApp(app) && app != "main")
        {
            if (app.StartsWith("qbtok", StringComparison.Ordinal)) return Remove($"builds its app id at run time ({Restore(app)})");
            // An app link whose id is unknown: the imported app.
            app = null;
        }

        var site = Marker(SITE) + "/app/";
        if (table is null)
        {
            notes.Add($"'{original}' now opens the PowerBase app");
            return site + (app is not null && ctx.IsApp(app) ? Marker(APP, app) : Marker(ANY_APP));
        }
        var tableRoute = site + Marker(APP_OF_TABLE, table) + "/tables/" + Marker(TABLE, table);
        switch (page)
        {
            case "dr" or "er":
            {
                var rid = Restore(Q("rid") ?? string.Empty);
                var record = RecordIdToken.Match(rid);
                if (!record.Success)
                    return Remove(rid.Length == 0 ? "opens a record without saying which one" : $"opens record '{rid}' by its Quickbase number; PowerBase opens a record by its id, so use the record of a step");
                notes.Add($"'{original}' now opens the PowerBase record");
                return tableRoute + "/records/{{" + record.Groups[1].Value + "." + RECORD_KEY + "}}" + (page == "er" ? "/edit" : "/view");
            }
            case "nwr" or "genaddrecordform":
                notes.Add($"'{original}' now opens the PowerBase add-record page");
                return tableRoute + "/records/new";
            case "q" or "td" or "tablehome" or null:
                if (page == "q" && Q("qid") is { } qid)
                    problems.Add($"'{original}' opens Quickbase report {Restore(qid)}; it now opens the PowerBase table, choose the report there if needed");
                else
                    notes.Add($"'{original}' now opens the PowerBase table");
                return tableRoute;
            default:
                return Remove($"opens Quickbase page '{page}', which PowerBase has no equivalent for");
        }
    }

    private static string Lower(string s) => s.Length == 0 ? s : char.ToLowerInvariant(s[0]) + s[1..];

    /// <summary>Splits <paramref name="text"/> into literal strings and the markers in it.</summary>
    public static IEnumerable<(string? Literal, string? Kind, string? Id)> Split(string text)
    {
        var last = 0;
        foreach (Match m in MarkerPattern.Matches(text))
        {
            if (m.Index > last) yield return (text[last..m.Index], null, null);
            yield return (null, m.Groups[1].Value, m.Groups[2].Success ? m.Groups[2].Value : null);
            last = m.Index + m.Length;
        }
        if (last < text.Length) yield return (text[last..], null, null);
    }
}
