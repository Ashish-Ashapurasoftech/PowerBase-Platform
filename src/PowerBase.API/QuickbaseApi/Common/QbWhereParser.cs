using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using PowerBase.Application.Reports;

namespace PowerBase.API.QuickbaseApi.Common;

/// <summary>
/// Turns a Quickbase query string (<c>{6.EX.'hello'}AND{7.GT.'3'}</c>) into our fid-based <see cref="FilterGroup"/>.
/// Parsing is done by the existing <see cref="QblCriteriaTranslator"/> (called, not changed); field ids are kept
/// as-is because our fids use Quickbase numbering.
/// </summary>
public static class QbWhereParser
{
    public static FilterGroup Parse(string where, ISet<int> knownFids)
    {
        // Relative periods (IR/XIR) are allowed in a Quickbase where; _ask_ prompts are not (no user to ask).
        where = QuickbaseOrder(QuickbaseEscapes(where));
        var result = QblCriteriaTranslator.Translate(where, fid => knownFids.Contains(fid) ? fid.ToString() : null, allowPrompts: false, allowRelativeDates: true);
        if (!result.Succeeded)
            throw QbApiException.BadRequest($"Invalid query '{where}': {result.Problem}");
        // Resolve today / yesterday / IR periods to literal dates exactly like report runs do (existing helper, called not changed).
        return Application.Reports.Queries.RunReport.RunReportQueryHandler.ResolveDateValueModeConditions(Convert(result.Filter!))!;
    }

    /// <summary>
    /// Rewrites a Quickbase query so the translator reads it the way Quickbase does (measured on a live realm):
    /// AND / OR are evaluated strictly left to right (no AND-before-OR) and must be upper case; an unquoted value ends at
    /// the first '.' ({11.EX.301.5} compares with 301); IR / XIR understand only today / yesterday / tomorrow — any other
    /// period matches nothing (IR) or everything (XIR).
    /// </summary>
    public static string QuickbaseOrder(string where)
    {
        var pos = 0;
        var result = Expr(where, ref pos, top: true);
        SkipWs(where, ref pos);
        if (pos < where.Length) throw QbApiException.BadRequest($"Invalid query '{where}'");
        return result;
    }

    private static string Expr(string s, ref int i, bool top)
    {
        var acc = Term(s, ref i);
        while (true)
        {
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] == ')') return acc;
            var op = s.Length - i >= 3 && s.Substring(i, 3) == "AND" ? "AND" : s.Length - i >= 2 && s.Substring(i, 2) == "OR" ? "OR" : null;
            if (op is null) throw QbApiException.BadRequest("Invalid query: expected AND or OR"); // lower case and/or is not Quickbase syntax
            i += op.Length;
            var right = Term(s, ref i);
            acc = $"({acc}){op}({right})";                         // left to right
        }
    }

    private static string Term(string s, ref int i)
    {
        SkipWs(s, ref i);
        if (i >= s.Length) throw QbApiException.BadRequest("Invalid query: missing condition");
        if (s[i] == '(')
        {
            i++;
            var inner = Expr(s, ref i, top: false);
            SkipWs(s, ref i);
            if (i >= s.Length || s[i] != ')') throw QbApiException.BadRequest("Invalid query: missing ')'");
            i++;
            return inner;
        }
        if (s[i] != '{') throw QbApiException.BadRequest("Invalid query: expected '{'");
        var start = ++i;
        var quoted = false;
        while (i < s.Length && (quoted || s[i] != '}'))
        {
            if (s[i] == '\'') { if (quoted && i + 1 < s.Length && s[i + 1] == '\'') { i += 2; continue; } quoted = !quoted; }
            i++;
        }
        if (i >= s.Length) throw QbApiException.BadRequest("Invalid query: condition is not closed with '}'");
        var body = s[start..i];
        i++;
        return "{" + Condition(body) + "}";
    }

    private static string Condition(string body)
    {
        var d1 = body.IndexOf('.');
        var d2 = d1 < 0 ? -1 : body.IndexOf('.', d1 + 1);
        if (d2 < 0) return body;
        var fid = body[..d1];
        var op = body[(d1 + 1)..d2];
        var value = body[(d2 + 1)..];
        if (!value.StartsWith('\'') && !value.StartsWith("_FID_", StringComparison.OrdinalIgnoreCase) && value.IndexOf('.') is var dot and >= 0)
            value = value[..dot];                                   // unquoted values stop at the first '.'
        if (op.Equals("IR", StringComparison.OrdinalIgnoreCase) || op.Equals("XIR", StringComparison.OrdinalIgnoreCase))
        {
            var plain = value.Trim('\'').Trim().ToLowerInvariant();
            var negate = op.Equals("XIR", StringComparison.OrdinalIgnoreCase);
            if (plain is "today" or "yesterday" or "tomorrow") return $"{fid}.{(negate ? "XEX" : "EX")}.'{plain}'";
            return negate ? "3.XEX.''" : "3.EX.''";                 // an unknown period: nothing in range
        }
        return $"{fid}.{op}.{value}";
    }

    private static void SkipWs(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

    /// <summary>
    /// Quickbase escapes a quote inside a value as <c>\'</c> and a backslash as <c>\\</c> ('O\'Brien'); the translator reads
    /// a doubled quote instead. Rewrites the escapes (only inside quoted values) without touching the translator.
    /// </summary>
    public static string QuickbaseEscapes(string where)
    {
        if (!where.Contains('\\')) return where;
        var sb = new System.Text.StringBuilder(where.Length);
        var inQuote = false;
        for (var i = 0; i < where.Length; i++)
        {
            var c = where[i];
            if (inQuote && c == '\\' && i + 1 < where.Length && where[i + 1] is '\'' or '\\')
            {
                sb.Append(where[i + 1] == '\'' ? "''" : "\\");
                i++;
                continue;
            }
            if (c == '\'')
            {
                // A doubled quote inside a value stays a literal quote.
                if (inQuote && i + 1 < where.Length && where[i + 1] == '\'') { sb.Append("''"); i++; continue; }
                inQuote = !inQuote;
            }
            sb.Append(c);
        }
        return sb.ToString();
    }

    private static FilterGroup Convert(PblFilterGroup group)
    {
        var target = new FilterGroup { Logic = string.Equals(group.Logic, "or", StringComparison.OrdinalIgnoreCase) ? "OR" : "AND" };
        foreach (var c in group.Conditions)
        {
            var condition = Convert(c);
            // Quickbase: a blank value "does not contain / start with / equal" anything, so negative operators match blanks.
            // Our SQL filter (NOT LIKE / <>) skips NULLs, so add "OR is empty" here instead of changing the shared filter.
            if (condition.Value is not null && condition.Operator is "ne" or "notContains" or "notStartsWith" or "notEndsWith")
            {
                var either = new FilterGroup { Logic = "OR" };
                either.Nodes.Add(new FilterNode { Condition = condition });
                either.Nodes.Add(new FilterNode { Condition = new FilterCondition { FieldId = condition.FieldId, Operator = "isEmpty" } });
                target.Nodes.Add(new FilterNode { Group = either });
            }
            else target.Nodes.Add(new FilterNode { Condition = condition });
        }
        foreach (var g in group.Groups)
            target.Nodes.Add(new FilterNode { Group = Convert(g) });
        return target;
    }

    private static FilterCondition Convert(PblFilterCondition c)
    {
        var op = c.Operator;
        var value = c.Value;
        if (c.UseCurrentUser)
        {
            op = op == "ne" ? "isNotCurrentUser" : "isCurrentUser";
            value = null;
        }
        else if (string.IsNullOrEmpty(value) && c.ValueMode is null && op is "eq" or "ne")
        {
            op = op == "eq" ? "isEmpty" : "isNotEmpty";
            value = null;
        }
        return new FilterCondition
        {
            FieldId = long.Parse(c.FieldName),
            Operator = op,
            Value = value,
            // today / yesterday / relative periods / field-to-field (_FID_n) comparisons.
            ValueMode = c.UseCurrentUser ? null : c.ValueMode,
            ValueFieldId = long.TryParse(c.ValueFieldName, out var other) ? other : null,
        };
    }
}
