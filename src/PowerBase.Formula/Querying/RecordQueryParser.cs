using System.Text;

namespace PowerBase.Formula.Querying;

/// <summary>
/// Parses QuickBase-style query strings into a <see cref="RecordQuery"/>. Grammar:
/// <code>
///   query     := group ( connector group )*
///   group     := '{' fid '.' op '.' value '}'
///   connector := 'AND' | 'OR'
///   value     := "'" chars "'"   |   bareChars        (quotes optional for simple values)
/// </code>
/// Whitespace between tokens is ignored. Returns false (and an empty query) on any
/// malformed input — callers fail soft rather than throwing on a bad runtime string.
/// </summary>
public static class RecordQueryParser
{
    public static bool TryParse(string? text, out RecordQuery query)
    {
        query = RecordQuery.Empty;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var clauses = new List<RecordQueryClause>();
        var connectors = new List<QueryConnector>();
        int i = 0;
        int n = text.Length;

        while (true)
        {
            SkipWs(text, ref i);
            if (i >= n || text[i] != '{') return false;
            if (!TryParseGroup(text, ref i, out var clause)) return false;
            clauses.Add(clause);

            SkipWs(text, ref i);
            if (i >= n) break;

            if (!TryParseConnector(text, ref i, out var connector)) return false;
            connectors.Add(connector);
        }

        if (clauses.Count == 0) return false;
        query = new RecordQuery(clauses, connectors);
        return true;
    }

    private static bool TryParseGroup(string s, ref int i, out RecordQueryClause clause)
    {
        clause = null!;
        // s[i] == '{'
        i++;
        SkipWs(s, ref i);

        // fid
        int start = i;
        while (i < s.Length && char.IsDigit(s[i])) i++;
        if (i == start || !long.TryParse(s.AsSpan(start, i - start), out var fid)) return false;

        SkipWs(s, ref i);
        if (i >= s.Length || s[i] != '.') return false;
        i++;
        SkipWs(s, ref i);

        // op (letters)
        start = i;
        while (i < s.Length && char.IsLetter(s[i])) i++;
        if (i == start) return false;
        var op = s.Substring(start, i - start).ToUpperInvariant();

        SkipWs(s, ref i);
        if (i >= s.Length || s[i] != '.') return false;
        i++;
        SkipWs(s, ref i);

        // value — quoted or bare (up to the closing brace)
        if (!TryParseValue(s, ref i, out var value)) return false;

        SkipWs(s, ref i);
        if (i >= s.Length || s[i] != '}') return false;
        i++;

        clause = new RecordQueryClause(fid, op, value);
        return true;
    }

    private static bool TryParseValue(string s, ref int i, out string value)
    {
        value = string.Empty;
        if (i >= s.Length) return false;

        if (s[i] == '\'')
        {
            i++;
            var sb = new StringBuilder();
            while (i < s.Length && s[i] != '\'')
            {
                // Backslash escapes the next char (so a value may contain a quote).
                if (s[i] == '\\' && i + 1 < s.Length) { i++; sb.Append(s[i]); }
                else sb.Append(s[i]);
                i++;
            }
            if (i >= s.Length) return false; // unterminated
            i++; // closing quote
            value = sb.ToString();
            return true;
        }

        // Bare value: everything up to the closing brace, trimmed.
        int start = i;
        while (i < s.Length && s[i] != '}') i++;
        if (i >= s.Length) return false;
        value = s.Substring(start, i - start).Trim();
        return true;
    }

    private static bool TryParseConnector(string s, ref int i, out QueryConnector connector)
    {
        connector = QueryConnector.And;
        if (Matches(s, i, "AND")) { i += 3; connector = QueryConnector.And; return true; }
        if (Matches(s, i, "OR")) { i += 2; connector = QueryConnector.Or; return true; }
        return false;
    }

    private static bool Matches(string s, int i, string token) =>
        i + token.Length <= s.Length && string.Compare(s, i, token, 0, token.Length, StringComparison.OrdinalIgnoreCase) == 0;

    private static void SkipWs(string s, ref int i)
    {
        while (i < s.Length && char.IsWhiteSpace(s[i])) i++;
    }
}
