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
        // Written the way Quickbase writes it: <?xml version="1.0" ?>, tab indentation, quotes in text as &quot;.
        var sb = new StringBuilder("<?xml version=\"1.0\" ?>\n");
        var settings = new System.Xml.XmlWriterSettings { OmitXmlDeclaration = true, Indent = true, IndentChars = "\t", NewLineChars = "\n" };
        using (var writer = System.Xml.XmlWriter.Create(sb, settings)) root.Save(writer);
        return QUOTE_IN_TEXT.Replace(sb.ToString(), m => m.Value.Replace("\"", "&quot;")) + "\n";
    }

    private sealed class Utf8StringWriter(StringBuilder sb) : StringWriter(sb)
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
