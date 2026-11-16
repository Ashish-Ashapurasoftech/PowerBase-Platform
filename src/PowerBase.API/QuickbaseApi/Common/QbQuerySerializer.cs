using System.Text;
using PowerBase.Application.Reports;

namespace PowerBase.API.QuickbaseApi.Common;

/// <summary>
/// Writes one of our saved report filter trees as a Quickbase query string, e.g. <c>{6.CT.'abc'}AND{7.GT.'10'}</c>
/// (Quickbase "Components of a query"). Field ids in our trees are Fids, the same numbers Quickbase uses.
/// </summary>
public static class QbQuerySerializer
{
    /// <param name="isDateField">Returns true for a fid whose comparisons use Quickbase's date operators (AF/BF/OAF/OBF).</param>
    public static string? Serialize(FilterGroup? tree, Func<long, bool>? isDateField = null)
    {
        if (tree is null) return null;
        var asks = 0;
        var text = WriteGroup(tree, isDateField ?? (_ => false), top: true, () => ++asks);
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private static string WriteGroup(FilterGroup group, Func<long, bool> isDate, bool top, Func<int> nextAsk)
    {
        var parts = new List<string>();
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c)
            {
                var s = WriteCondition(c, isDate, nextAsk);
                if (!string.IsNullOrEmpty(s)) parts.Add(s);
            }
            else if (node.Group is { } g)
            {
                var s = WriteGroup(g, isDate, top: false, nextAsk);
                if (!string.IsNullOrEmpty(s)) parts.Add(s);
            }
        }
        if (parts.Count == 0) return "";
        var joiner = string.Equals(group.Logic, "OR", StringComparison.OrdinalIgnoreCase) ? "OR" : "AND";
        var joined = string.Join(joiner, parts);
        return top || parts.Count == 1 ? joined : "(" + joined + ")";
    }

    private static string WriteCondition(FilterCondition c, Func<long, bool> isDate, Func<int> nextAsk)
    {
        var fid = c.FieldId;
        var date = isDate(fid);
        var mode = c.ValueMode ?? "";
        var op = (c.Operator ?? "").Trim();

        // "is during" / "is not during" a relative period → Quickbase IR / XIR ('this week', 'last 2 months', 'next 3 days').
        if (op is "during" or "notDuring")
            return Clause(fid, op == "during" ? "IR" : "XIR", RelativePeriod(mode, c.Value));

        string? value = mode.ToLowerInvariant() switch
        {
            "field" when c.ValueFieldId is { } other => "_FID_" + other,
            "ask" => $"_ask{nextAsk()}_",                       // ask-the-user prompt, numbered like Quickbase
            "today" => "today",
            "yesterday" => "yesterday",
            "tomorrow" => "tomorrow",
            "pastdays" => int.TryParse(c.Value, out var pd) ? DateValue(DateTime.UtcNow.Date.AddDays(-pd).ToString("yyyy-MM-dd")) : c.Value,
            "futuredays" => int.TryParse(c.Value, out var fd) ? DateValue(DateTime.UtcNow.Date.AddDays(fd).ToString("yyyy-MM-dd")) : c.Value,
            _ => date ? DateValue(c.Value) : c.Value,
        };

        switch (op)
        {
            case "isEmpty": return Clause(fid, "EX", "");
            case "isNotEmpty": return Clause(fid, "XEX", "");
            case "isCurrentUser": return Clause(fid, "EX", "_curuser_");
            case "isNotCurrentUser": return Clause(fid, "XEX", "_curuser_");
            case "in":
            case "notIn":
            {
                var values = SplitList(value);
                if (values.Count == 0) return "";
                var neg = op == "notIn";
                var items = values.Select(v => Clause(fid, neg ? "XEX" : "EX", v)).ToList();
                var joined = string.Join(neg ? "AND" : "OR", items);
                return items.Count == 1 ? joined : "(" + joined + ")";
            }
        }

        var qbOp = op switch
        {
            "eq" or "is" or "equals" => "EX",
            "ne" or "is-not" or "not-equals" => "XEX",
            "contains" => "CT",
            "notContains" or "not-contains" => "XCT",
            "startsWith" or "starts-with" => "SW",
            "notStartsWith" => "XSW",
            "includes" => "HAS",
            "notIncludes" => "XHAS",
            "gt" or "greater_than" or "after" => date ? "AF" : "GT",
            "gte" => date ? "OAF" : "GTE",
            "lt" or "less_than" or "before" => date ? "BF" : "LT",
            "lte" => date ? "OBF" : "LTE",
            _ => op.ToUpperInvariant(),
        };
        return Clause(fid, qbOp, value ?? "");
    }

    /// <summary>Quickbase relative period text: 'this week', 'last 2 months', 'next day'.</summary>
    public static string RelativePeriod(string mode, string? value)
    {
        var parts = (value ?? "").Split(':');
        var count = parts.Length >= 1 && int.TryParse(parts[0], out var n) && n > 0 ? n : 1;
        var unit = parts.Length == 2 && parts[1].Length > 0 ? parts[1].ToLowerInvariant() : "week";
        var units = count == 1 ? unit : unit + "s";
        return mode.ToLowerInvariant() switch
        {
            "duringprevious" => count == 1 ? $"last {unit}" : $"last {count} {units}",
            "duringnext" => count == 1 ? $"next {unit}" : $"next {count} {units}",
            _ => $"this {unit}",
        };
    }

    /// <summary>Quickbase writes dates in queries as MM-DD-YYYY; our stored literals are yyyy-MM-dd.</summary>
    public static string? DateValue(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        return DateTime.TryParseExact(value.Trim(), "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.None, out var d) ? d.ToString("MM-dd-yyyy", System.Globalization.CultureInfo.InvariantCulture) : value;
    }

    private static List<string> SplitList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return new();
        var v = value.Trim();
        if (v.StartsWith('['))
        {
            try { return System.Text.Json.JsonSerializer.Deserialize<List<string>>(v) ?? new(); }
            catch (System.Text.Json.JsonException) { }
        }
        return v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static string Clause(long fid, string op, string value)
    {
        var sb = new StringBuilder();
        sb.Append('{').Append(fid).Append('.').Append(op).Append('.');
        if (value.StartsWith("_FID_", StringComparison.Ordinal)) sb.Append(value);
        else sb.Append('\'').Append(value.Replace("\\", "\\\\").Replace("'", "\\'")).Append('\'');
        sb.Append('}');
        return sb.ToString();
    }
}
