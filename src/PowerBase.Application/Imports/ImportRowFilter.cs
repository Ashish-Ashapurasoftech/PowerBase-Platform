using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Imports;

/// <summary>Evaluates a filter tree against an already-read source row (formula, lookup and summary values are
/// present under their column names after projection). Used for the part of a definition's conditions that SQL
/// cannot evaluate: computed and encrypted fields. Follows the SQL builder's semantics operator for operator —
/// a NULL never satisfies a comparison, and a condition with no value is ignored — so a filter behaves the same
/// whichever side ends up evaluating it.</summary>
public sealed class ImportRowFilter
{
    private readonly FilterGroup _tree;
    private readonly IReadOnlyDictionary<long, AppField> _fields;

    public ImportRowFilter(FilterGroup tree, IEnumerable<AppField> fields)
    {
        _tree = tree;
        _fields = fields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
    }

    public bool Matches(IReadOnlyDictionary<string, object?> row) => MatchesGroup(_tree, row);

    private bool MatchesGroup(FilterGroup group, IReadOnlyDictionary<string, object?> row)
    {
        var results = group.Nodes
            .Select(n => n.Condition is { } c ? (bool?)MatchesCondition(c, row) : n.Group is { } g ? MatchesGroup(g, row) : null)
            .Where(r => r.HasValue).Select(r => r!.Value);
        return string.Equals(group.Logic, "or", StringComparison.OrdinalIgnoreCase)
            ? !group.Nodes.Any() || results.Any(r => r)
            : results.All(r => r);
    }

    private bool MatchesCondition(FilterCondition c, IReadOnlyDictionary<string, object?> row)
    {
        var cell = CellValue(c.FieldId, row);
        var op = c.Operator;
        if (op == "isEmpty") return IsBlank(cell);
        if (op == "isNotEmpty") return !IsBlank(cell);

        object? other = null;
        string? literal = c.Value;
        var fieldToField = string.Equals(c.ValueMode, "field", StringComparison.OrdinalIgnoreCase) && c.ValueFieldId.HasValue;
        if (fieldToField) other = CellValue(c.ValueFieldId!.Value, row);
        else if (string.IsNullOrEmpty(literal)) return true; // no value entered: the condition is ignored, as in SQL

        if (cell is null || (fieldToField && other is null)) return false; // NULL satisfies no comparison
        var field = _fields.GetValueOrDefault(c.FieldId);

        switch (op)
        {
            case "eq": return Compare(cell, other, literal, field) == 0;
            case "ne": return Compare(cell, other, literal, field) != 0;
            case "gt": return Compare(cell, other, literal, field) > 0;
            case "gte": return Compare(cell, other, literal, field) >= 0;
            case "lt": return Compare(cell, other, literal, field) < 0;
            case "lte": return Compare(cell, other, literal, field) <= 0;
            case "date_eq": return SameDate(cell, other, literal) == true;
            case "date_ne": return SameDate(cell, other, literal) == false;
        }

        var text = AsText(cell);
        var needle = literal ?? "";
        return op switch
        {
            "contains" => text.Contains(needle, StringComparison.OrdinalIgnoreCase),
            "notContains" => !text.Contains(needle, StringComparison.OrdinalIgnoreCase),
            "startsWith" => text.StartsWith(needle, StringComparison.OrdinalIgnoreCase),
            "notStartsWith" => !text.StartsWith(needle, StringComparison.OrdinalIgnoreCase),
            "endsWith" => text.EndsWith(needle, StringComparison.OrdinalIgnoreCase),
            "wildcard" => WildcardRegex(needle).IsMatch(text),
            "notWildcard" => !WildcardRegex(needle).IsMatch(text),
            "includes" => ListItems(text).Contains(needle.Trim(), StringComparer.OrdinalIgnoreCase),
            "notIncludes" => !ListItems(text).Contains(needle.Trim(), StringComparer.OrdinalIgnoreCase),
            "in" => ValueList(needle).Any(v => Compare(cell, null, v, field) == 0),
            "notIn" => !ValueList(needle).Any(v => Compare(cell, null, v, field) == 0),
            _ => false // unknown operators fail closed: never import rows the filter could not evaluate
        };
    }

    private object? CellValue(long fieldId, IReadOnlyDictionary<string, object?> row)
    {
        if (fieldId == -1) return row.GetValueOrDefault("PublicId");
        var field = _fields.GetValueOrDefault(fieldId);
        var column = field is not null ? PhysicalNaming.GetPhysicalColumnName(field) : fieldId switch
        {
            1 => "CreatedOn", 2 => "ModifiedOn", 3 => "Id", 4 => "CreatedBy", 5 => "ModifiedBy",
            _ => PhysicalNaming.ColumnName((int)fieldId)
        };
        return row.GetValueOrDefault(column);
    }

    private static bool IsBlank(object? value) => value is null or DBNull || (value is string s && s.Length == 0);

    private static string AsText(object value) => value switch
    {
        DateTime d => d.ToString("O", CultureInfo.InvariantCulture),
        bool b => b ? "true" : "false",
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? ""
    };

    /// <summary>Compares the cell with the other field's value or the literal, using the cell's own type.</summary>
    private static int Compare(object cell, object? other, string? literal, AppField? field)
    {
        var right = other ?? literal;
        switch (cell)
        {
            case bool b:
                return b.CompareTo(right is bool ob ? ob : ParseBool(right?.ToString()));
            case DateTime d:
                return d.CompareTo(right is DateTime od ? od : DateTime.TryParse(right?.ToString(), CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var pd) ? pd : DateTime.MinValue);
        }
        if (TryDecimal(cell, out var left) && TryDecimal(right, out var rightNumber)) return left.CompareTo(rightNumber);
        return string.Compare(AsText(cell), right is null ? "" : AsText(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ParseBool(string? s) => s is "1" || bool.TryParse(s, out var b) && b;

    private static bool TryDecimal(object? value, out decimal result)
    {
        switch (value)
        {
            case decimal d: result = d; return true;
            case byte or short or int or long or float or double:
                result = Convert.ToDecimal(value, CultureInfo.InvariantCulture); return true;
            case string s when ImportNumber.TryParse(s, out var parsed):
                result = parsed; return true;
            default: result = 0; return false;
        }
    }

    private static bool? SameDate(object cell, object? other, string? literal)
    {
        var right = other is DateTime od ? od : DateTime.TryParse(literal, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var pd) ? pd : (DateTime?)null;
        return cell is DateTime d && right.HasValue ? d.Date == right.Value.Date : null;
    }

    /// <summary>Wildcard syntax: * any run of characters, ? one character, backslash escapes either.</summary>
    private static Regex WildcardRegex(string pattern)
    {
        var sb = new System.Text.StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var ch = pattern[i];
            if (ch == '\\' && i + 1 < pattern.Length) sb.Append(Regex.Escape(pattern[++i].ToString()));
            else if (ch == '*') sb.Append(".*");
            else if (ch == '?') sb.Append('.');
            else sb.Append(Regex.Escape(ch.ToString()));
        }
        return new Regex(sb.Append('$').ToString(), RegexOptions.IgnoreCase | RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));
    }

    /// <summary>Items of a multi-value cell stored as a JSON array or a comma list.</summary>
    private static IEnumerable<string> ListItems(string stored)
    {
        var trimmed = stored.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(trimmed);
                return doc.RootElement.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()! : e.GetRawText()).ToList();
            }
            catch (JsonException) { /* not JSON: fall through to the comma split */ }
        }
        return trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>The "in"/"notIn" wire format: a JSON string array, falling back to a comma list.</summary>
    private static List<string> ValueList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        if (raw.TrimStart().StartsWith('['))
        {
            try
            {
                using var doc = JsonDocument.Parse(raw);
                return doc.RootElement.EnumerateArray()
                    .Select(e => (e.ValueKind == JsonValueKind.String ? e.GetString() : e.GetRawText())?.Trim())
                    .Where(v => !string.IsNullOrWhiteSpace(v)).Select(v => v!).ToList();
            }
            catch (JsonException) { /* fall through */ }
        }
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
