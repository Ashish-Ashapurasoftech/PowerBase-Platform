using System.Text;
using System.Xml.Linq;

namespace PowerBase.API.QuickbaseApi.Xml;

/// <summary>
/// Builds a Quickbase XML response: <c>&lt;qdbapi&gt;&lt;action/&gt;&lt;errcode/&gt;&lt;errtext/&gt;[&lt;errdetail/&gt;]&lt;udata/&gt;…&lt;/qdbapi&gt;</c>.
/// Documented elements with no value are written empty (user rule: every Quickbase field present).
/// </summary>
public sealed class QbXmlResponse
{
    private readonly List<XNode> _body = new();
    public string Action { get; }
    /// <summary>When set, the call answers with this HTML page instead of XML (API_GenAddRecordForm).</summary>
    public string? Html { get; init; }
    /// <summary>When set, the complete response text, already laid out exactly as Quickbase writes it (API_DoQuery).</summary>
    public string? Raw { get; init; }

    public QbXmlResponse(string action) => Action = action;

    private static readonly System.Text.RegularExpressions.Regex QUOTE_IN_TEXT = new(@">[^<]*<", System.Text.RegularExpressions.RegexOptions.Compiled);

    public QbXmlResponse Add(string name, object? value)
    {
        _body.Add(new XElement(name, Clean(Text(value))));
        return this;
    }

    public QbXmlResponse AddIf(bool condition, string name, object? value) => condition ? Add(name, value) : this;

    public QbXmlResponse Add(XElement element)
    {
        _body.Add(element);
        return this;
    }

    public static string Text(object? value) => value switch
    {
        null => "",
        bool b => b ? "1" : "0",
        IFormattable f => f.ToString(null, System.Globalization.CultureInfo.InvariantCulture),
        _ => value.ToString() ?? "",
    };

    /// <summary>Drops characters XML 1.0 cannot carry (e.g. U+0001 from a URL parameter), which would otherwise fail the response.</summary>
    public static string Clean(string value) =>
        value.Any(c => !Ok(c)) ? new string(value.Where(Ok).ToArray()) : value;

    private static bool Ok(char c) => System.Xml.XmlConvert.IsXmlChar(c) || char.IsSurrogate(c);

    public string ToXml(string? udata, int errcode = 0, string errtext = "No error", string? errdetail = null,
        IReadOnlyList<XElement>? extra = null)
    {
        var root = new XElement("qdbapi",
            new XElement("action", Action),
            new XElement("errcode", errcode),
            new XElement("errtext", errtext));
        if (!string.IsNullOrEmpty(errdetail)) root.Add(new XElement("errdetail", Clean(errdetail)));
        // Quickbase echoes <udata> only when the request carried one.
        if (udata is not null) root.Add(new XElement("udata", Clean(udata)));
        if (errcode != 0 && extra is not null) foreach (var e in extra) root.Add(e);
        if (errcode == 0) foreach (var node in _body) root.Add(node);
        // Quickbase's own layout for these three answers (live realm, 2026-11-24): see UserLayout.
        if (errcode == 0 && UserLayout(Action, _body) is { } custom)
        {
            root.RemoveNodes();
            root.Add(new XElement("action", Action), new XElement("errcode", errcode), new XElement("errtext", errtext));
            if (udata is not null) root.Add(new XElement("udata", Clean(udata)));
            return Crlf(Serialize(root)).Replace("</qdbapi>" + CRLF, custom + "</qdbapi>" + CRLF);
        }
        return Crlf(Serialize(root));
    }

    private const string CRLF = "\r\n";

    /// <summary>Written the way Quickbase writes it: <c>&lt;?xml version="1.0" ?&gt;</c>, tab indentation, quotes in text as &amp;quot;.</summary>
    private static string Serialize(XElement root)
    {
        var sb = new StringBuilder("<?xml version=\"1.0\" ?>\n");
        var settings = new System.Xml.XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = "\t", NewLineChars = "\n" };
        using (var writer = System.Xml.XmlWriter.Create(sb, settings)) root.Save(writer);
        return QUOTE_IN_TEXT.Replace(sb.ToString(), m => m.Value.Replace("\"", "&quot;")) + "\n";
    }

    /// <summary>Quickbase ends every line with CRLF except the one holding &lt;qdbapi&gt; (live realm, every XML answer seen).</summary>
    public static string Crlf(string xml) => xml.Replace("\r\n", "\n").Replace("\n", CRLF).Replace("<qdbapi>" + CRLF, "<qdbapi>\n");

    private static string T(string? text) =>
        System.Security.SecurityElement.Escape(Clean(text ?? ""))!.Replace("&apos;", "'");

    private static string Attrs(XElement e) =>
        string.Concat(e.Attributes().Select(a => $" {a.Name.LocalName}=\"{T(a.Value)}\""));

    private static string Leaf(XElement e) => $"<{e.Name.LocalName}{Attrs(e)}>{T(e.Value)}</{e.Name.LocalName}>";

    /// <summary>
    /// The body of API_GetUserInfo / API_GetUserRole / API_UserRoles exactly as Quickbase lays it out (spacing, blank lines and
    /// LF / CRLF mix measured on a live realm); null for every other action (generic layout).
    /// </summary>
    private static string? UserLayout(string action, IReadOnlyList<XNode> body)
    {
        var a = action.ToUpperInvariant();
        if (a is not ("API_GETUSERINFO" or "API_GETUSERROLE" or "API_USERROLES")) return null;
        var root = body.OfType<XElement>().FirstOrDefault();
        if (root is null) return null;
        var sb = new StringBuilder();
        if (a == "API_GETUSERINFO")
        {
            var kids = root.Elements().ToList();
            sb.Append($"\t<user{Attrs(root)}>").Append(CRLF).Append('\n');
            sb.Append(' ').Append(string.Join(" ", kids.Take(Math.Max(0, kids.Count - 1)).Select(Leaf))).Append('\n');
            if (kids.Count > 0) sb.Append(' ').Append(Leaf(kids[^1])).Append(CRLF);
            sb.Append("\t</user>").Append(CRLF);
            return sb.ToString();
        }
        static void Role(StringBuilder sb, XElement role, string open, string inner, string close)
        {
            sb.Append(open).Append($"<role{Attrs(role)}>\n");
            foreach (var c in role.Elements()) sb.Append(inner).Append(Leaf(c)).Append('\n');
            sb.Append(close).Append("</role>\n");
        }
        if (a == "API_GETUSERROLE")
        {
            sb.Append($"\t<user{Attrs(root)}>").Append(CRLF).Append('\n');
            foreach (var c in root.Elements())
            {
                if (c.Name.LocalName != "roles") { sb.Append(' ').Append(Leaf(c)).Append('\n'); continue; }
                sb.Append(" <roles>\n");
                foreach (var r in c.Elements()) Role(sb, r, "\t ", "   ", "  ");
                sb.Append(" </roles>\n");
            }
            sb.Append(CRLF).Append("\t</user>").Append(CRLF);
            return sb.ToString();
        }
        sb.Append("\t<users>").Append(CRLF).Append('\n');
        foreach (var u in root.Elements())
        {
            sb.Append($" <user{Attrs(u)}>\n");
            foreach (var c in u.Elements())
            {
                if (c.Name.LocalName != "roles") { sb.Append("  ").Append(Leaf(c)).Append('\n'); continue; }
                sb.Append("  <roles>\n");
                foreach (var r in c.Elements()) Role(sb, r, "\t  ", "    ", "   ");
                sb.Append("  </roles>\n");
            }
            sb.Append(" </user>\n");
        }
        sb.Append("\n\n").Append(CRLF).Append("\t</users>").Append(CRLF);
        return sb.ToString();
    }

    private sealed class Utf8StringWriter(StringBuilder sb) : StringWriter(sb)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
