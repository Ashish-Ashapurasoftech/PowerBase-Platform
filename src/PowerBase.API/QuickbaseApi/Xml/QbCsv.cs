using System.Text;

namespace PowerBase.API.QuickbaseApi.Xml;

/// <summary>Minimal RFC 4180 CSV reader for API_ImportFromCSV (quoted fields, "" escapes, CR/LF/CRLF line ends).</summary>
public static class QbCsv
{
    public static List<List<string>> Parse(string text)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var cell = new StringBuilder();
        var quoted = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (quoted)
            {
                if (c == '"')
                {
                    if (i + 1 < text.Length && text[i + 1] == '"') { cell.Append('"'); i++; }
                    else quoted = false;
                }
                else cell.Append(c);
                continue;
            }
            switch (c)
            {
                case '"' when cell.Length == 0: quoted = true; break;
                case ',': row.Add(cell.ToString()); cell.Clear(); break;
                case '\r':
                case '\n':
                    if (c == '\r' && i + 1 < text.Length && text[i + 1] == '\n') i++;
                    row.Add(cell.ToString()); cell.Clear();
                    if (!(row.Count == 1 && row[0].Length == 0)) rows.Add(row);
                    row = new List<string>();
                    break;
                default: cell.Append(c); break;
            }
        }
        if (cell.Length > 0 || row.Count > 0)
        {
            row.Add(cell.ToString());
            if (!(row.Count == 1 && row[0].Length == 0)) rows.Add(row);
        }
        return rows;
    }
}
