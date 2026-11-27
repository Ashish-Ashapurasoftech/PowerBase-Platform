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
    /// <param name="isTimestamp">Which fids are Date / Time fields: a date or wall-clock value on them is read the way
    /// Quickbase reads it — a plain date means that whole day, both in the app's time zone (<paramref name="zone"/>).</param>
    /// <param name="typeOf">Quickbase field type per fid (timestamp / multitext / phone get Quickbase's own comparison rules).</param>
    public static FilterGroup Parse(string where, ISet<int> knownFids, Func<long, string?>? typeOf, TimeZoneInfo? zone)
    {
        var tree = Parse(where, knownFids);
        if (typeOf is null) return tree;
        ZoneTimestamps(tree, fid => typeOf(fid) == "timestamp", zone ?? TimeZoneInfo.Utc);
        TypedValues(tree, typeOf);
        return tree;
    }

    /// <summary>Only the digits of a phone number: Quickbase compares phone values digit by digit ("(555) 010-0" = "555-0100").</summary>
    public static string PhoneDigits(string? value) => new((value ?? "").Where(char.IsDigit).ToArray());

    /// <summary>
    /// Multi-select text (live realm): a ';' list is a set — EX / XEX compare the whole set in any order, HAS needs every listed
    /// choice, XHAS holds unless all are there. Phone: both sides compared by their digits (the stored side is read that way
    /// in memory, see <see cref="V1.Services.QbRecordService"/>).
    /// </summary>
    public static void TypedValues(FilterGroup group, Func<long, string?> typeOf)
    {
        foreach (var node in group.Nodes)
        {
            if (node.Group is { } g) { TypedValues(g, typeOf); continue; }
            if (node.Condition is not { } c || c.ValueFieldId is not null || !string.IsNullOrEmpty(c.SubField) || c.Value is null) continue;
            var type = typeOf(c.FieldId);
            var raw = c.Value.Trim().Trim('\'');
            // Live realm: a date / date-time compared with milliseconds since 1970 ({16.OAF.1773568800000}) is that instant.
            if (type is "date" or "timestamp" && raw.Length >= 9 && raw.All(char.IsDigit) && long.TryParse(raw, out var ms))
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
                c.Value = type == "date" ? at.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture)
                    : at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture);
                continue;
            }
            // Live realm: an unchecked box and a blank one are the same "0" ({19.EX.0} finds both; {19.XEX.1} too).
            if (type == "checkbox" && ((c.Operator == "eq" && raw is "0" or "false" or "") || (c.Operator == "ne" && raw is "1" or "true")))
            {
                var either = new FilterGroup { Logic = "OR" };
                either.Nodes.Add(new FilterNode { Condition = new FilterCondition { FieldId = c.FieldId, Operator = "eq", Value = c.Operator == "eq" ? c.Value : "0" } });
                either.Nodes.Add(new FilterNode { Condition = new FilterCondition { FieldId = c.FieldId, Operator = "isEmpty" } });
                node.Condition = null;
                node.Group = either;
                continue;
            }
            if (type == "phone" && c.Operator is "eq" or "ne" or "contains" or "notContains" or "startsWith" or "notStartsWith" or "endsWith" or "notEndsWith")
                c.Value = PhoneDigits(c.Value);
            else if (type == "multitext" && c.Value.Contains(';'))
            {
                var parts = c.Value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (c.Operator is "eq" or "ne") c.Value = string.Join(",", parts);
                else if (c.Operator is "includes" or "notIncludes" && parts.Length > 1)
                {
                    var all = new FilterGroup { Logic = c.Operator == "includes" ? "AND" : "OR" };
                    foreach (var part in parts)
                        all.Nodes.Add(new FilterNode { Condition = new FilterCondition { FieldId = c.FieldId, Operator = c.Operator, Value = part } });
                    node.Condition = null;
                    node.Group = all;
                }
            }
        }
    }

    private static readonly string[] DAY_FORMATS = { "MM-dd-yyyy", "M-d-yyyy", "MM/dd/yyyy", "M/d/yyyy", "yyyy-MM-dd" };

    /// <summary>
    /// On a Date / Time field (live realm, app in Eastern time): {f.EX.'03-15-2026'} is that day, AF is after it, OBF is up to its
    /// end, BF / OAF start at its beginning — the day running midnight to midnight in the app's zone (a value with a
    /// time of day is UTC). Rewritten to UTC instants so the shared SQL filter compares them as moments.
    /// </summary>
    public static void ZoneTimestamps(FilterGroup group, Func<long, bool> isTimestamp, TimeZoneInfo zone)
    {
        foreach (var node in group.Nodes)
        {
            if (node.Group is { } g) { ZoneTimestamps(g, isTimestamp, zone); continue; }
            if (node.Condition is not { } c || !isTimestamp(c.FieldId) || c.ValueFieldId is not null || !string.IsNullOrEmpty(c.SubField)
                || c.Value is not { Length: > 0 } value || c.Operator is not ("eq" or "ne" or "gt" or "gte" or "lt" or "lte")) continue;
            var text = value.Trim().Trim('\'');
            if (System.DateTime.TryParseExact(text, DAY_FORMATS, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out var day))
            {
                var start = Utc(day.Date, zone);
                var end = Utc(day.Date.AddDays(1), zone);
                FilterCondition At(string op, string v) => new() { FieldId = c.FieldId, Operator = op, Value = v };
                switch (c.Operator)
                {
                    case "eq":
                    case "ne":
                        var range = new FilterGroup { Logic = c.Operator == "eq" ? "AND" : "OR" };
                        range.Nodes.Add(new FilterNode { Condition = At(c.Operator == "eq" ? "gte" : "lt", start) });
                        range.Nodes.Add(new FilterNode { Condition = At(c.Operator == "eq" ? "lt" : "gte", end) });
                        node.Condition = null;
                        node.Group = range;
                        break;
                    case "gt": c.Operator = "gte"; c.Value = end; break;
                    case "gte": c.Value = start; break;
                    case "lt": c.Value = start; break;
                    default: c.Operator = "lt"; c.Value = end; break;   // lte: up to the end of the day
                }
            }
            // A value with a time of day ('03-15-2026 10:30') is UTC in a query (live realm), unlike in API_AddRecord: left as is.
        }
    }

    private static string Utc(System.DateTime wall, TimeZoneInfo zone) =>
        System.TimeZoneInfo.ConvertTimeToUtc(System.DateTime.SpecifyKind(wall, System.DateTimeKind.Unspecified), zone)
            .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", System.Globalization.CultureInfo.InvariantCulture);

    public static FilterGroup Parse(string where, ISet<int> knownFids)
    {
        // Relative periods (IR/XIR) are allowed in a Quickbase where; _ask_ prompts are not (no user to ask).
        where = QuickbaseOrder(QuickbaseEscapes(where));
        var result = QblCriteriaTranslator.Translate(where, fid => knownFids.Contains(fid) ? fid.ToString() : null, allowPrompts: false, allowRelativeDates: true);
        if (!result.Succeeded)
            throw QbApiException.BadRequest($"Invalid query '{where}': {result.Problem}");
        // Resolve today / yesterday / IR periods to literal dates exactly like report runs do (existing helper, called not changed).
        var tree = Application.Reports.Queries.RunReport.RunReportQueryHandler.ResolveDateValueModeConditions(Convert(result.Filter!))!;
        ModifiedFallsBackToCreated(tree);
        return tree;
    }

    /// <summary>
    /// Date Modified (fid 2) is never empty in Quickbase — a record never changed reads its Date Created. Our column is NULL
    /// until the first change, so a condition on fid 2 also holds when it is empty and Date Created (fid 1) meets it.
    /// </summary>
    public static void ModifiedFallsBackToCreated(FilterGroup group)
    {
        foreach (var node in group.Nodes)
        {
            if (node.Group is { } g) { ModifiedFallsBackToCreated(g); continue; }
            if (node.Condition is not { FieldId: QbIds.DATE_MODIFIED_FID } c || !string.IsNullOrEmpty(c.SubField)) continue;
            if (c.Operator is "isEmpty" or "isNotEmpty") { c.FieldId = QbIds.DATE_CREATED_FID; continue; }
            var created = new FilterCondition
            {
                FieldId = QbIds.DATE_CREATED_FID, Operator = c.Operator, Value = c.Value, ValueMode = c.ValueMode, ValueFieldId = c.ValueFieldId,
            };
            var never = new FilterGroup { Logic = "AND" };
            never.Nodes.Add(new FilterNode { Condition = new FilterCondition { FieldId = QbIds.DATE_MODIFIED_FID, Operator = "isEmpty" } });
            never.Nodes.Add(new FilterNode { Condition = created });
            var either = new FilterGroup { Logic = "OR" };
            either.Nodes.Add(new FilterNode { Condition = c });
            either.Nodes.Add(new FilterNode { Group = never });
            node.Condition = null;
            node.Group = either;
        }
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
            if ((condition.Value is not null || condition.ValueMode is not null) && condition.Operator is "ne" or "notContains" or "notStartsWith" or "notEndsWith" or "notIncludes")
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
