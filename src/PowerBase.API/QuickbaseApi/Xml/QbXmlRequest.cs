using System.Xml.Linq;

namespace PowerBase.API.QuickbaseApi.Xml;

/// <summary>One <c>&lt;field&gt;</c> of an XML request: by fid or name, optional filename (base64 file content).</summary>
public sealed record QbXmlField(int? Fid, string? Name, string? FileName, string Value);

/// <summary>
/// A parsed Quickbase XML API call. Accepts both documented forms: POST with an XML <c>&lt;qdbapi&gt;</c> body and
/// the URL form (<c>/db/{dbid}?a=API_x&amp;param=value&amp;_fid_6=...&amp;_fnm_name=...</c>). Body values win over query values.
/// </summary>
public sealed class QbXmlRequest
{
    public const string ACTION_HEADER = "QUICKBASE-ACTION";

    public string Action { get; init; } = "";
    public string DbId { get; init; } = "";
    public IReadOnlyList<QbXmlField> Fields { get; init; } = Array.Empty<QbXmlField>();
    private readonly Dictionary<string, string> _params = new(StringComparer.OrdinalIgnoreCase);

    public string? Get(string name) => _params.TryGetValue(name, out var v) ? v : null;
    public bool Flag(string name) => Get(name)?.Trim() is "1" or "true" or "yes";
    public string? Udata => Get("udata");

    /// <exception cref="QbXmlException">errcode 11 when the body is not well-formed XML.</exception>
    public static QbXmlRequest Parse(string dbid, string? actionHeader, IQueryCollection query, string? body)
    {
        var fields = new List<QbXmlField>();
        var request = new QbXmlRequest { DbId = dbid, Fields = fields };

        foreach (var (key, values) in query)
        {
            var value = values.ToString();
            if (key.StartsWith("_fid_", StringComparison.OrdinalIgnoreCase) && int.TryParse(key[5..], out var fid))
                fields.Add(new QbXmlField(fid, null, null, value));
            else if (key.StartsWith("_fnm_", StringComparison.OrdinalIgnoreCase))
                fields.Add(new QbXmlField(null, key[5..], null, value));
            else
                request._params[key] = value;
        }

        if (!string.IsNullOrWhiteSpace(body))
        {
            XDocument doc;
            try { doc = SafeParse(body); }
            catch (System.Xml.XmlException ex) { throw new QbXmlException(QbXmlErrors.XML_PARSE, "Could not parse XML input", ex.Message); }
            var root = doc.Root;
            if (root is null || !root.Name.LocalName.Equals("qdbapi", StringComparison.OrdinalIgnoreCase))
                throw new QbXmlException(QbXmlErrors.XML_PARSE, "Could not parse XML input", "Root element must be <qdbapi>");
            foreach (var el in root.Elements())
            {
                var name = el.Name.LocalName;
                if (name.Equals("field", StringComparison.OrdinalIgnoreCase))
                {
                    int? fid = int.TryParse((string?)el.Attribute("fid"), out var f) ? f : null;
                    fields.Add(new QbXmlField(fid, (string?)el.Attribute("name"), (string?)el.Attribute("filename"), el.Value));
                }
                else if (name.StartsWith("_fid_", StringComparison.OrdinalIgnoreCase) && int.TryParse(name[5..], out var fid2))
                    fields.Add(new QbXmlField(fid2, null, null, el.Value));
                else if (name.StartsWith("_fnm_", StringComparison.OrdinalIgnoreCase))
                    fields.Add(new QbXmlField(null, name[5..], null, el.Value));
                else
                    request._params[name] = el.Value;
            }
        }

        var action = !string.IsNullOrWhiteSpace(actionHeader) ? actionHeader.Trim() : request.Get("a")?.Trim() ?? "";
        return new QbXmlRequest { Action = action, DbId = dbid, Fields = fields }.WithParams(request._params);
    }

    /// <summary>Parses request XML with DTDs prohibited (no entity expansion / external entities).</summary>
    public static XDocument SafeParse(string xml)
    {
        var settings = new System.Xml.XmlReaderSettings { DtdProcessing = System.Xml.DtdProcessing.Prohibit, XmlResolver = null };
        using var reader = System.Xml.XmlReader.Create(new StringReader(xml), settings);
        return XDocument.Load(reader);
    }

    private QbXmlRequest WithParams(Dictionary<string, string> p)
    {
        foreach (var (k, v) in p) _params[k] = v;
        return this;
    }
}
