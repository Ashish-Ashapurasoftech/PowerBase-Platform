using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Constants;
using PowerBase.Application.Formulas;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// Pipeline-side in-memory evaluation of filter conditions on compute-on-read fields (Formula,
/// Lookup, Summary) and on Reference fields searched by their display text. Search Records and
/// Copy Records split a filter tree into a SQL half and an in-memory half
/// (<see cref="FormulaFilterSorter.SplitFilterTree"/>); this class evaluates the in-memory half with
/// result-type-aware comparisons — a Summary Count/Sum is a long/decimal and must compare as a number,
/// a Lookup of a date as a date — and supports the wildcard / includes operators and the
/// "the value in the field" tier, none of which the generic evaluator handles.
/// </summary>
public static class PipelineComputedFilter
{
    private static readonly HashSet<string> IdStyleOperators = new(StringComparer.Ordinal)
        { "eq", "ne", "in", "notIn", "isEmpty", "isNotEmpty" };

    /// <summary>
    /// Fids of Reference fields the tree searches by their display text (a text operator such as
    /// "contains", or a non-numeric value). A Reference column stores the parent's Record ID#, so a
    /// condition that compares an id (the common token-driven case) stays in SQL; only the
    /// label-style conditions need the in-memory pass.
    /// </summary>
    public static HashSet<long> LabelStyleReferenceFids(IReadOnlyList<AppField> fields, FilterGroup? tree)
    {
        var result = new HashSet<long>();
        if (tree == null) return result;
        var references = fields.Where(f => f.Fid.HasValue && f.TypeCode == "Reference")
            .Select(f => (long)f.Fid!.Value).ToHashSet();
        if (references.Count == 0) return result;
        Collect(tree);
        return result;

        void Collect(FilterGroup group)
        {
            foreach (var node in group.Nodes)
            {
                if (node.Condition is { } c && references.Contains(c.FieldId) && IsLabelStyle(c)) result.Add(c.FieldId);
                if (node.Group != null) Collect(node.Group);
            }
        }
    }

    private static bool IsLabelStyle(FilterCondition c)
    {
        if (!IdStyleOperators.Contains(c.Operator)) return true;
        if (c.Operator is "isEmpty" or "isNotEmpty") return false;
        if (string.Equals(c.ValueMode, "field", StringComparison.OrdinalIgnoreCase)) return false;
        var values = c.Operator is "in" or "notIn" ? ParseValueList(c.Value) : [c.Value ?? string.Empty];
        return values.Any(v => !long.TryParse(v?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out _));
    }

    public static List<(IReadOnlyDictionary<string, object?> Row, IReadOnlyDictionary<long, object?> Computed)> Apply(
        IEnumerable<(IReadOnlyDictionary<string, object?> Row, IReadOnlyDictionary<long, object?> Computed)> pairs,
        FilterGroup tree,
        IReadOnlyList<AppField> fields)
    {
        var lookup = fields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
        return pairs.Where(p => MatchesGroup(p, tree, lookup)).ToList();
    }

    private static bool MatchesGroup((IReadOnlyDictionary<string, object?> Row, IReadOnlyDictionary<long, object?> Computed) p,
        FilterGroup group, Dictionary<long, AppField> lookup)
    {
        if (group.Nodes.Count == 0) return true;
        bool NodeMatches(FilterNode n) =>
            n.Condition != null ? MatchesCondition(p, n.Condition, lookup)
            : n.Group != null && MatchesGroup(p, n.Group, lookup);
        return string.Equals(group.Logic, "or", StringComparison.OrdinalIgnoreCase)
            ? group.Nodes.Any(NodeMatches)
            : group.Nodes.All(NodeMatches);
    }

    private static bool MatchesCondition((IReadOnlyDictionary<string, object?> Row, IReadOnlyDictionary<long, object?> Computed) p,
        FilterCondition c, Dictionary<long, AppField> lookup)
    {
        lookup.TryGetValue(c.FieldId, out var field);
        var val = ReadValue(p, c.FieldId, field);

        var operand = c.Value;
        if (string.Equals(c.ValueMode, "field", StringComparison.OrdinalIgnoreCase) && c.ValueFieldId is { } otherFid)
        {
            lookup.TryGetValue(otherFid, out var otherField);
            operand = ToText(ReadValue(p, otherFid, otherField));
        }

        var matched = Evaluate(c.Operator, val, operand);

        // A Reference stores the parent's Record ID# but is searched by display text; exact-match
        // operators also accept the stored id so an id-based condition keeps matching.
        if (field?.TypeCode == "Reference" && p.Computed.ContainsKey(c.FieldId))
        {
            var rawId = ReadRaw(p, c.FieldId, field);
            if (c.Operator is "eq" or "in") matched = matched || Evaluate(c.Operator, rawId, operand);
            else if (c.Operator is "ne" or "notIn") matched = matched && Evaluate(c.Operator, rawId, operand);
        }
        return matched;
    }

    private static object? ReadValue((IReadOnlyDictionary<string, object?> Row, IReadOnlyDictionary<long, object?> Computed) p,
        long fieldId, AppField? field)
    {
        if (field != null && PhysicalNaming.IsComputedTypeCode(field.TypeCode))
            return p.Computed.TryGetValue(fieldId, out var cv) ? cv : null;
        if (field?.TypeCode == "Reference" && p.Computed.TryGetValue(fieldId, out var label) && label is not null)
            return label;
        return ReadRaw(p, fieldId, field);
    }

    private static object? ReadRaw((IReadOnlyDictionary<string, object?> Row, IReadOnlyDictionary<long, object?> Computed) p,
        long fieldId, AppField? field)
    {
        if (fieldId == -1) return p.Row.TryGetValue("PublicId", out var pv) ? pv : null;
        var col = field != null && !string.IsNullOrWhiteSpace(field.PhysicalColumnName)
            ? field.PhysicalColumnName
            : fieldId switch
            {
                1 => "CreatedOn",
                2 => "ModifiedOn",
                3 => "Id",
                4 => "CreatedBy",
                5 => "ModifiedBy",
                _ => PhysicalNaming.ColumnName(field?.Fid ?? (int)fieldId)
            };
        return p.Row.TryGetValue(col, out var v) ? v : null;
    }

    private static bool Evaluate(string op, object? val, string? operand)
    {
        var text = ToText(val);
        var needle = operand ?? string.Empty;
        return op switch
        {
            "isEmpty"       => IsBlank(val),
            "isNotEmpty"    => !IsBlank(val),
            "eq"            => Compare(val, operand) == 0,
            "ne"            => Compare(val, operand) != 0,
            "gt"            => Compare(val, operand) > 0,
            "gte"           => Compare(val, operand) >= 0,
            "lt"            => Compare(val, operand) < 0,
            "lte"           => Compare(val, operand) <= 0,
            "contains"      => text?.Contains(needle, StringComparison.OrdinalIgnoreCase) == true,
            "notContains"   => text?.Contains(needle, StringComparison.OrdinalIgnoreCase) != true,
            "startsWith"    => text?.StartsWith(needle, StringComparison.OrdinalIgnoreCase) == true,
            "notStartsWith" => text?.StartsWith(needle, StringComparison.OrdinalIgnoreCase) != true,
            "wildcard"      => text is not null && Wildcard(text, needle),
            "notWildcard"   => text is null || !Wildcard(text, needle),
            "includes"      => Members(val).Any(m => string.Equals(m, needle.Trim(), StringComparison.OrdinalIgnoreCase)),
            "notIncludes"   => !Members(val).Any(m => string.Equals(m, needle.Trim(), StringComparison.OrdinalIgnoreCase)),
            // The operand is the exact UTC instant of the picked day's local midnight: match the [dv, dv+1day) window.
            "date_eq"       => TryDate(val, out var a1) && DateTime.TryParse(operand, out var b1) && a1 >= b1 && a1 < b1.AddDays(1),
            "date_ne"       => !(TryDate(val, out var a2) && DateTime.TryParse(operand, out var b2) && a2 >= b2 && a2 < b2.AddDays(1)),
            "date_gt"       => TryDate(val, out var a3) && DateTime.TryParse(operand, out var b3) && a3 >= b3.AddDays(1),
            "date_gte"      => TryDate(val, out var a4) && DateTime.TryParse(operand, out var b4) && a4 >= b4,
            "date_lt"       => TryDate(val, out var a5) && DateTime.TryParse(operand, out var b5) && a5 < b5,
            "date_lte"      => TryDate(val, out var a6) && DateTime.TryParse(operand, out var b6) && a6 < b6.AddDays(1),
            "in"            => ParseValueList(operand).Any(v => Compare(val, v) == 0),
            "notIn"         => !ParseValueList(operand).Any(v => Compare(val, v) == 0),
            _               => true,
        };
    }

    private static bool IsBlank(object? v) => v is null || (v is string s ? s.Length == 0 : v.ToString() == string.Empty);

    private static string? ToText(object? v) => v switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => v.ToString(),
    };

    private static bool TryDate(object? v, out DateTime date)
    {
        switch (v)
        {
            case DateTime dt: date = dt; return true;
            case DateTimeOffset dto: date = dto.UtcDateTime; return true;
            case DateOnly d: date = d.ToDateTime(TimeOnly.MinValue); return true;
            case string s when DateTime.TryParse(s, out var parsed): date = parsed; return true;
            default: date = default; return false;
        }
    }

    private static bool TryNumber(object? v, out decimal n)
    {
        switch (v)
        {
            case decimal m: n = m; return true;
            case double d when double.IsFinite(d): n = (decimal)d; return true;
            case float f when float.IsFinite(f): n = (decimal)f; return true;
            case long or int or short or byte or sbyte or ushort or uint or ulong:
                n = Convert.ToDecimal(v, CultureInfo.InvariantCulture); return true;
            case TimeSpan ts: n = (decimal)ts.TotalMinutes; return true;
            default: n = default; return false;
        }
    }

    private static bool Wildcard(string text, string pattern) =>
        Regex.IsMatch(text, "^" + Regex.Escape(pattern).Replace("\\*", ".*").Replace("\\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.Singleline);

    private static IEnumerable<string> Members(object? v)
    {
        if (v is null) return [];
        if (v is System.Collections.IEnumerable list and not string)
            return list.Cast<object?>().Select(x => ToText(x)?.Trim() ?? string.Empty).ToList();
        var raw = ToText(v) ?? string.Empty;
        if (raw.TrimStart().StartsWith('[')) return ParseValueList(raw);
        return raw.Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static List<string> ParseValueList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var s = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
                    if (!string.IsNullOrWhiteSpace(s)) list.Add(s.Trim('"'));
                }
                return list;
            }
        }
        catch (JsonException) { /* plain delimited text */ }
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }

    private static int Compare(object? a, string? b)
    {
        if (a is null && b is null) return 0;
        if (a is null) return -1;
        if (b is null) return 1;
        if (TryNumber(a, out var da) && decimal.TryParse(b, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var db))
            return da.CompareTo(db);
        if (a is DateTime or DateTimeOffset or DateOnly && TryDate(a, out var dt) && DateTime.TryParse(b, out var dtb))
            return dt.CompareTo(dtb);
        if (a is bool bl && bool.TryParse(b, out var blb)) return bl.CompareTo(blb);
        return string.Compare(ToText(a), b, StringComparison.OrdinalIgnoreCase);
    }
}
