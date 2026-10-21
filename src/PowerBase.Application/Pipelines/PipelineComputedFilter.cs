using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Constants;
using PowerBase.Application.Formulas;
using PowerBase.Application.Common.Filtering;

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
        // One end of a Date / Numeric Range (Search Records' range rules, on an encrypted range read here).
        if (field != null && PhysicalNaming.IsRangeTypeCode(field.TypeCode) && c.SubField is "start" or "end")
            return MatchesRangeEnd(c, ReadRangeEnd(p, field, c.SubField));
        var val = ReadValue(p, c.FieldId, field);
        // One part of an Address ("Address: City"): the property of the stored address JSON.
        if (!string.IsNullOrWhiteSpace(c.SubField) && field?.TypeCode == "Address")
            val = PipelineAddressFieldFilter.ReadPart(val, c.SubField);

        var operand = c.Value;
        var byField = string.Equals(c.ValueMode, "field", StringComparison.OrdinalIgnoreCase);
        if (byField && c.ValueFieldId is { } otherFid)
        {
            lookup.TryGetValue(otherFid, out var otherField);
            operand = ToText(ReadValue(p, otherFid, otherField));
        }

        if (!byField && field != null)
        {
            // A File (a Lookup of one, or an encrypted one) is matched by its files' names, as in SQL: any name for a positive operator,
            // none for a negated one.
            if (PipelineFilterEvaluator.IsFileField(field) && FileNameOperator(c.Operator) is { } fileCheck)
            {
                var anyName = PipelineFilterEvaluator.FileNames(ToText(val)).Any(name => Evaluate(fileCheck.Op, name, operand));
                return fileCheck.Negated ? !anyName : anyName;
            }
            // A time of day (a Formula_Time, a Lookup of a Time) compares as one, whatever form it arrives in.
            if (PipelineFilterEvaluator.GetTypeCategory(field) == "TIME" && c.Operator is "eq" or "ne" or "gt" or "gte" or "lt" or "lte")
                return MatchesTime(c.Operator, val, operand);
        }

        // "is" / "is not" on a list (a formula or lookup returning users or choices) compares the items, in any order — as on a stored
        // Multi-select / User list and in the trigger filters.
        var matched = field != null && c.Operator is "eq" or "ne" && PipelineFilterEvaluator.IsListField(field)
            ? PipelineFilterEvaluator.SameItems(ToText(val) ?? string.Empty, operand ?? string.Empty) == (c.Operator == "eq")
            : Evaluate(c.Operator, val, operand);

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
        // A Lookup of an encrypted parent field arrives as decrypted text too: compare it as the type it presents.
        if (field != null && PhysicalNaming.IsComputedTypeCode(field.TypeCode))
            return p.Computed.TryGetValue(fieldId, out var cv) ? StoredValueCoercion.ToFieldType(cv, field) : null;
        if (field?.TypeCode == "Reference" && p.Computed.TryGetValue(fieldId, out var label) && label is not null)
            return label;
        // An encrypted field is decrypted to text: compare it as the field's own type (number, date, checkbox).
        return StoredValueCoercion.ToFieldType(ReadRaw(p, fieldId, field), field);
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
        // The value of a contains / starts / ends condition is escaped for SQL LIKE ("a_b" is sent as "a[_]b"); here it is
        // compared as plain text, so the escapes are taken off again.
        var likeNeedle = UnescapeLike(needle);
        return op switch
        {
            "isEmpty"       => IsBlank(val),
            "isNotEmpty"    => !IsBlank(val),
            "eq"            => Compare(val, operand) == 0,
            "ne"            => Compare(val, operand) != 0,
            // A blank value is in no order, as in SQL (NULL < x is not true): "before today" or the edges of "is not during"
            // never match a record without a date.
            "gt"            => !IsBlank(val) && Compare(val, operand) > 0,
            "gte"           => !IsBlank(val) && Compare(val, operand) >= 0,
            "lt"            => !IsBlank(val) && Compare(val, operand) < 0,
            "lte"           => !IsBlank(val) && Compare(val, operand) <= 0,
            "contains"      => text?.Contains(likeNeedle, StringComparison.OrdinalIgnoreCase) == true,
            "notContains"   => text?.Contains(likeNeedle, StringComparison.OrdinalIgnoreCase) != true,
            "startsWith"    => text?.StartsWith(likeNeedle, StringComparison.OrdinalIgnoreCase) == true,
            "notStartsWith" => text?.StartsWith(likeNeedle, StringComparison.OrdinalIgnoreCase) != true,
            // Was missing: an unknown operator falls through to "true", so "ends with" matched every record.
            "endsWith"      => text?.EndsWith(likeNeedle, StringComparison.OrdinalIgnoreCase) == true,
            "notEndsWith"   => text?.EndsWith(likeNeedle, StringComparison.OrdinalIgnoreCase) != true,
            "wildcard"      => text is not null && Wildcard(text, needle),
            "notWildcard"   => text is null || !Wildcard(text, needle),
            // Several values picked at once are joined with commas: the list holds any of them.
            "includes"      => IncludesAny(Members(val), likeNeedle),
            "notIncludes"   => !IncludesAny(Members(val), likeNeedle),
            // The operand is the exact UTC instant of the picked day's local midnight: match the [dv, dv+1day) window.
            "date_eq"       => TryDate(val, out var a1) && TryInstant(operand, out var b1) && a1 >= b1 && a1 < b1.AddDays(1),
            "date_ne"       => !(TryDate(val, out var a2) && TryInstant(operand, out var b2) && a2 >= b2 && a2 < b2.AddDays(1)),
            "date_gt"       => TryDate(val, out var a3) && TryInstant(operand, out var b3) && a3 >= b3.AddDays(1),
            "date_gte"      => TryDate(val, out var a4) && TryInstant(operand, out var b4) && a4 >= b4,
            "date_lt"       => TryDate(val, out var a5) && TryInstant(operand, out var b5) && a5 < b5,
            "date_lte"      => TryDate(val, out var a6) && TryInstant(operand, out var b6) && a6 < b6.AddDays(1),
            "in"            => ParseValueList(operand).Any(v => Compare(val, v) == 0),
            "notIn"         => !ParseValueList(operand).Any(v => Compare(val, v) == 0),
            _               => true,
        };
    }

    /// <summary>The name comparison a File condition makes, and whether no name may pass it. Null for "is blank" and the rest.</summary>
    private static (string Op, bool Negated)? FileNameOperator(string op) => op switch
    {
        "eq" => ("eq", false), "ne" => ("eq", true),
        "contains" => ("contains", false), "notContains" => ("contains", true),
        "startsWith" => ("startsWith", false), "notStartsWith" => ("startsWith", true),
        "endsWith" => ("endsWith", false), "notEndsWith" => ("endsWith", true),
        "wildcard" => ("wildcard", false), "notWildcard" => ("wildcard", true),
        _ => null,
    };

    /// <summary>A time of day: a Time column's TimeSpan, a TimeOnly, the time of a DateTime, or text such as "10:30:00".</summary>
    private static bool TryTimeOfDay(object? v, out TimeSpan time)
    {
        switch (v)
        {
            case TimeSpan span when span >= TimeSpan.Zero && span < TimeSpan.FromDays(1): time = span; return true;
            case TimeOnly only: time = only.ToTimeSpan(); return true;
            case DateTime dt: time = dt.TimeOfDay; return true;
            case string s: return PipelineFilterEvaluator.TryParseTimeOfDay(s, out time);
            default: time = default; return false;
        }
    }

    /// <summary>eq/ne/gt/gte/lt/lte on times of day. A blank value, like a value that is no time, is only "not equal".</summary>
    private static bool MatchesTime(string op, object? val, string? operand)
    {
        if (!TryTimeOfDay(val, out var time) || !PipelineFilterEvaluator.TryParseTimeOfDay(operand, out var asked)) return op == "ne";
        return op switch
        {
            "eq" => time == asked,
            "ne" => time != asked,
            "gt" => time > asked,
            "gte" => time >= asked,
            "lt" => time < asked,
            _ => time <= asked,   // lte
        };
    }

    /// <summary>One end of a Date / Numeric Range: from its own column (f_N / f_N_e), or — in an encrypted app, which keeps the whole
    /// range as JSON in the start column — from that JSON. A date as its UTC instant, a number as a decimal; null when blank.</summary>
    private static object? ReadRangeEnd((IReadOnlyDictionary<string, object?> Row, IReadOnlyDictionary<long, object?> Computed) p, AppField field, string part)
    {
        var start = ReadRaw(p, field.Fid ?? 0, field);
        object? raw;
        if (PipelineFilterEvaluator.TryReadRange(ToText(start), out var first, out var last)) raw = part == "end" ? last : first;
        else raw = part == "end" ? (p.Row.TryGetValue(PhysicalNaming.EndColumnName(field.Fid ?? 0), out var end) ? end : null) : start;
        if (raw is null || (raw is string blank && string.IsNullOrWhiteSpace(blank))) return null;
        if (field.TypeCode == "NumericRange")
        {
            if (TryNumber(raw, out var n)) return n;
            return decimal.TryParse(ToText(raw), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out n) ? n : null;
        }
        return raw switch
        {
            DateTime dt => DateTime.SpecifyKind(dt, DateTimeKind.Utc),
            DateOnly d => DateTime.SpecifyKind(d.ToDateTime(TimeOnly.MinValue), DateTimeKind.Utc),
            _ => PipelineFilterEvaluator.TryParseDateTime(ToText(raw) ?? string.Empty, out var parsed) ? parsed : null
        };
    }

    /// <summary>A condition on one end of a range, with SQL's rule for a blank end: it is in no order and equals nothing.</summary>
    private static bool MatchesRangeEnd(FilterCondition c, object? end)
    {
        if (c.Operator == "isEmpty") return end is null;
        if (c.Operator == "isNotEmpty") return end is not null;
        if (end is null) return false;
        int cmp;
        if (end is decimal n)
        {
            if (!decimal.TryParse(c.Value, NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var asked)) return false;
            cmp = n.CompareTo(asked);
        }
        else
        {
            if (!PipelineFilterEvaluator.TryParseDateTime(c.Value ?? string.Empty, out var asked)) return false;
            cmp = ((DateTime)end).CompareTo(asked);
        }
        return c.Operator switch
        {
            "eq" => cmp == 0,
            "ne" => cmp != 0,
            "gt" => cmp > 0,
            "gte" => cmp >= 0,
            "lt" => cmp < 0,
            "lte" => cmp <= 0,
            _ => false,
        };
    }

    private static bool IncludesAny(IEnumerable<string> members, string needle)
    {
        var wanted = needle.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        // The whole value first: a single choice may itself hold a comma.
        return members.Any(m => string.Equals(m, needle.Trim(), StringComparison.OrdinalIgnoreCase)
            || wanted.Any(w => string.Equals(m, w, StringComparison.OrdinalIgnoreCase)));
    }

    // A list (a formula returning users or choices) with no items is blank too.
    private static bool IsBlank(object? v) => v switch
    {
        null => true,
        string s => s.Length == 0,
        System.Collections.IDictionary => false,
        System.Collections.IEnumerable list => !list.Cast<object?>().Any(item => !IsBlank(item)),
        _ => v.ToString() == string.Empty
    };

    private static string? ToText(object? v) => v switch
    {
        null => null,
        string s => s,
        bool b => b ? "true" : "false",
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        DateOnly d => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        // A list (a formula returning users or choices) reads as its items, not its type name.
        System.Collections.IEnumerable list => PipelineFilterEvaluator.ValueText(list),
        _ => v.ToString(),
    };

    /// <summary>A date or instant as text, read as UTC — what the columns hold. "…Z" (the date picker's instant) stays that instant
    /// instead of moving into the server's own time zone, which shifted "is 15 Oct" by a day on a server not running on UTC.</summary>
    private static bool TryInstant(string? text, out DateTime date) =>
        DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out date);

    private static bool TryDate(object? v, out DateTime date)
    {
        switch (v)
        {
            case DateTime dt: date = dt; return true;
            case DateTimeOffset dto: date = dto.UtcDateTime; return true;
            case DateOnly d: date = d.ToDateTime(TimeOnly.MinValue); return true;
            case string s when TryInstant(s, out var parsed): date = parsed; return true;
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

    /// <summary>Takes off the SQL LIKE escapes a contains / starts / ends value carries ("[%]" "[_]" "[[]"); the reverse of the escaping
    /// in <see cref="CopyRecordsDefinition.ParseQuery"/> and of <see cref="EscapeLike"/>.</summary>
    public static string UnescapeLike(string value) =>
        value.Contains('[') ? value.Replace("[%]", "%").Replace("[_]", "_").Replace("[[]", "[") : value;

    /// <summary>Escapes a plain-text value for SQL LIKE, so "%", "_" and "[" match themselves rather than acting as wildcards.</summary>
    public static string EscapeLike(string value) =>
        value.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");

    /// <summary>Quickbase's WC/XWC wildcard: '*' any run of characters, '?' one character, '\*' / '\?' the character itself.</summary>
    private static bool Wildcard(string text, string pattern)
    {
        var regex = new System.Text.StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length && pattern[i + 1] is '*' or '?') { regex.Append(Regex.Escape(pattern[++i].ToString())); }
            else if (c == '*') regex.Append(".*");
            else if (c == '?') regex.Append('.');
            else regex.Append(Regex.Escape(c.ToString()));
        }
        return Regex.IsMatch(text, regex.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.Singleline);
    }

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
        if (a is DateTime or DateTimeOffset or DateOnly && TryDate(a, out var dt) && TryInstant(b, out var dtb))
            return dt.CompareTo(dtb);
        if (a is bool bl && bool.TryParse(b, out var blb)) return bl.CompareTo(blb);
        return string.Compare(ToText(a), b, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
    }
}
