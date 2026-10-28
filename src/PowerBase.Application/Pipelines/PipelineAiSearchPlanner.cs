using System.Globalization;
using System.Text;
using System.Text.Json;
using PowerBase.Application.Reports;
using PowerBase.Application.Search;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>The outcome of <see cref="PipelineAiSearchPlanner.Evaluate"/>: the Azure AI Search filter to use, or why the
/// search has to be answered from SQL instead. <see cref="FieldIds"/> are the fields the filter reads from the index.</summary>
public sealed record PipelineAiSearchPlan(string? ODataFilter, string? SqlReason, IReadOnlyList<long>? FieldIds = null)
{
    public bool UseAiSearch => ODataFilter != null;
}

/// <summary>Switches of <see cref="PipelineAiSearchPlanner"/>.</summary>
/// <param name="PatternOperators">Answer contains / starts with / ends with / wildcard from the index (regular expressions over
/// its words). Off in this type unless asked for; the pipeline asks for it (PipelineExecutionOptions.AiSearchPatternOperators, on by default)
/// after it was checked against the real service (2026-10-27, the dev search service: 657 conditions, no record of the
/// documented SQL meaning left out), so it can be switched on in configuration.</param>
public sealed record PipelineAiSearchPlanOptions(bool PatternOperators = false);

/// <summary>
/// Decides how much of a pipeline search (Search Records, Copy Records) Azure AI Search can narrow down, and builds that filter.
///
/// AI Search is only a way to find <em>candidate</em> record ids quickly. The caller reads those records from SQL and applies the
/// COMPLETE filter to them there, so the filter built here must never leave out a record SQL would match: it is a SUPERSET of
/// the SQL filter. That is what makes a partial answer safe:
///   • an AND keeps the conditions the index can answer and drops the rest (SQL still checks them on every candidate);
///   • an OR keeps nothing unless every branch can be answered (one unanswerable branch makes the whole OR unconstrained);
///   • a search with nothing the index can answer, or past <see cref="MaxConditions"/> conditions or <see cref="MaxInValues"/>
///     list values, is read from SQL.
///
/// Per condition (column = the field's stored slot in the shared index, <c>f_{fid}</c> in a table's own index):
///   • text (Text, Multi-line, Email, Phone, Url, Single select): is / is any of = case-insensitive phrase match (SQL compares
///     without regard to case); is empty; and, with <see cref="PipelineAiSearchPlanOptions.PatternOperators"/>, contains /
///     starts with / ends with / wildcard as regular expressions over the index's words;
///   • number (Number, Currency, Percent, Rating): is / > / ≥ / &lt; ≤ / is any of as a range widened by a rounding margin;
///   • date (Date, Date &amp; Time): the same, widened by a day on each side (SQL compares a calendar day, the index an instant);
///   • yes/no: is; multi-value (Multi select, Checkbox group): includes / is as phrase matches;
///   • never answered by the index (SQL decides): negations (they would return nearly the whole table), Formula / Lookup /
///     Summary / Reference / File / Address / User / Time / Duration / ranges, Rich Text (the index holds its HTML), system
///     fields (not indexed), a condition on another field or on a sub-field, a relative date.
/// </summary>
public static class PipelineAiSearchPlanner
{
    /// <summary>Text-like types whose index value is the very text the SQL column holds.</summary>
    private static readonly HashSet<string> TextTypes = new(StringComparer.Ordinal)
        { "Text", "TextMultiLine", "Email", "Phone", "Url", "SingleSelect" };

    private static readonly HashSet<string> MultiTypes = new(StringComparer.Ordinal) { "MultiSelect", "CheckboxGroup" };

    /// <summary>Lucene's default English stop words. The index drops them from the text it analyzes, so a value made only of
    /// them, or a fragment of one, cannot be matched through the index.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "if", "in", "into", "is", "it", "no", "not", "of",
        "on", "or", "such", "that", "the", "their", "then", "there", "these", "they", "this", "to", "was", "will", "with"
    };

    /// <summary>The most "in" list values in the whole filter. A long list becomes too many query clauses — Azure AI Search warns that
    /// hundreds risk its limit — and, in the SQL that verifies the candidates, too many parameters (each value is one, under the
    /// 2,100 limit the candidate ids share). Past it, the whole search is read from SQL.</summary>
    public const int MaxInValues = 70;

    /// <summary>The most conditions in the whole filter, for the same two reasons. Past it, the whole search is read from SQL.</summary>
    public const int MaxConditions = 30;

    public const int MaxValueLength = 256;

    /// <summary>The most words of one pattern condition sent to the index (the longest ones; the rest are left to SQL).</summary>
    public const int MaxPatternWords = 5;

    /// <summary>Added to / taken from a number's bounds: the index keeps a double, SQL a DECIMAL(18,4), so the two can differ in the
    /// last digits of a large value. Relative to the value, and never less than the column's own precision.</summary>
    private const double NumberMarginAbsolute = 1e-4;
    private const double NumberMarginRelative = 1e-12;

    /// <summary>Days added to each side of a date's bounds. SQL compares a calendar day (or an instant) where the index holds an instant.</summary>
    private const int DateMarginDays = 1;

    public static PipelineAiSearchPlan Evaluate(FilterGroup? tree, IReadOnlyList<AppField> fields, PipelineAiSearchPlanOptions? options = null)
    {
        if (tree == null) return Sql("the search has no filter");

        var patternOperators = options?.PatternOperators ?? false;
        var isWide = SearchSlotResolver.IsWideTable(fields);
        var fieldsByFid = fields.Where(f => f.Fid.HasValue && !f.IsDeleted)
            .GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());

        var (conditions, inValues) = Count(tree);
        if (conditions == 0) return Sql("the search has no usable condition");
        if (conditions > MaxConditions) return Sql($"the filter has more than {MaxConditions} conditions");
        if (inValues > MaxInValues) return Sql($"the filter's lists are longer than {MaxInValues} values");

        var used = new HashSet<long>();
        var odata = NarrowGroup(tree);
        if (string.IsNullOrWhiteSpace(odata)) return Sql("no condition of the search can be answered from the index");
        return new PipelineAiSearchPlan(odata, null, used.Order().ToList());

        string? NarrowGroup(FilterGroup group)
        {
            var isOr = string.Equals(group.Logic, "or", StringComparison.OrdinalIgnoreCase);
            var parts = new List<string>();
            var any = false;
            var unconstrained = false;
            foreach (var node in group.Nodes)
            {
                string? part;
                if (node.Condition != null)
                {
                    part = NarrowCondition(node.Condition);
                }
                else if (node.Group != null && node.Group.Nodes.Count > 0)
                {
                    part = NarrowGroup(node.Group);
                    if (part != null) part = $"({part})";
                }
                else continue;

                any = true;
                if (part == null) unconstrained = true;
                else parts.Add(part);
            }
            if (!any) return null;
            if (isOr) return unconstrained ? null : string.Join(" or ", parts);   // one open branch opens the whole OR
            return parts.Count == 0 ? null : string.Join(" and ", parts);
        }

        string? NarrowCondition(FilterCondition c)
        {
            if (!fieldsByFid.TryGetValue(c.FieldId, out var field)) return null;
            if (!string.IsNullOrWhiteSpace(c.SubField)) return null;
            if (c.ValueFieldId.HasValue || !(string.IsNullOrEmpty(c.ValueMode) || string.Equals(c.ValueMode, "literal", StringComparison.OrdinalIgnoreCase)))
                return null;
            if (field.IsSystem || PhysicalNaming.IsComputedTypeCode(field.TypeCode)) return null;
            if (!SearchSlotResolver.IsAiFilterableType(field.TypeCode) || !SearchSlotResolver.IsSearchEligible(field)) return null;

            var slot = SearchSlotResolver.ResolveSlotName(field, fields);
            // A field with no slot of its own in the shared index is "answered from SQL": its legacy name is not a column there.
            if (!isWide && SearchSlotResolver.IsWideSlot(slot)) return null;

            var filter = field.TypeCode switch
            {
                var t when TextTypes.Contains(t) => TextFilter(slot, c, multi: false, patternOperators),
                var t when MultiTypes.Contains(t) => TextFilter(slot, c, multi: true, patternOperators),
                var t when SearchSlotResolver.IsNumeric(t) => NumberFilter(slot, c),
                var t when SearchSlotResolver.IsDate(t) => DateFilter(slot, c),
                var t when SearchSlotResolver.IsBoolean(t) => BooleanFilter(slot, c),
                _ => null
            };
            if (filter != null) used.Add(c.FieldId);
            return filter;
        }
    }

    private static PipelineAiSearchPlan Sql(string reason) => new(null, reason);

    /// <summary>Every condition of the tree, and the values of its "in" lists.</summary>
    private static (int Conditions, int InValues) Count(FilterGroup group)
    {
        var conditions = 0;
        var inValues = 0;
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c)
            {
                conditions++;
                if (c.Operator is "in" or "notIn") inValues += ParseValueList(c.Value).Count;
            }
            else if (node.Group != null)
            {
                var (nestedConditions, nestedInValues) = Count(node.Group);
                conditions += nestedConditions;
                inValues += nestedInValues;
            }
        }
        return (conditions, inValues);
    }

    // ── text ─────────────────────────────────────────────────────────────────────────────────

    private static string? TextFilter(string slot, FilterCondition c, bool multi, bool patternOperators)
    {
        switch (c.Operator)
        {
            case "eq" when !multi:
                return IsMatchable(c.Value) ? Phrase(slot, c.Value!) : null;
            case "in" when !multi:
            {
                var values = ParseValueList(c.Value);
                if (values.Count == 0 || values.Any(v => !IsMatchable(v))) return null;
                return AnyOf(values.Select(v => Phrase(slot, v)).ToList());
            }
            case "includes" when multi:
            {
                var tokens = SplitPicks(c.Value);
                if (tokens.Count == 0 || tokens.Any(v => !IsMatchable(v))) return null;
                return AnyOf(tokens.Select(v => Phrase(slot, v)).ToList());
            }
            case "eq" when multi:
            {
                // The same SET of values: each picked value is in the list, so each one can be required. A value the index cannot
                // match is left to SQL (dropping a required value only widens the candidates).
                var clauses = SplitPicks(c.Value).Where(IsMatchable).Select(v => Phrase(slot, v)).ToList();
                return clauses.Count == 0 ? null : string.Join(" and ", clauses);
            }
            case "isEmpty":
                return multi ? $"({slot} eq null or {slot} eq '' or {slot} eq '[]')" : $"({slot} eq null or {slot} eq '')";
            case "contains" or "startsWith" or "endsWith" or "wildcard" when patternOperators:
                return PatternFilter(slot, c.Operator, c.Value);
            default:
                return null;
        }
    }

    private static string? AnyOf(List<string> clauses) => clauses.Count == 1 ? clauses[0] : $"({string.Join(" or ", clauses)})";

    /// <summary>The picked values of a multi-value condition: comma separated, as the SQL filter reads them.</summary>
    private static List<string> SplitPicks(string? raw) =>
        (raw ?? string.Empty).Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>
    /// contains / starts with / ends with / wildcard. SQL matches a substring of the raw text (LIKE, case-insensitive); the index
    /// holds the text split into words, so the condition is asked of the words: every run of ASCII letters / digits of the value
    /// must lie inside some word of the text. A run lies inside a single word of any text containing it (the analyzer never splits
    /// letters or digits), which makes the result a superset of the SQL match. A run that is a fragment of a stop word is skipped
    /// (the index drops stop words), and a value holding a LIKE character (% _ [) is left to SQL, because there it is a pattern.
    /// The first word of a "starts with" and the last of an "ends with" are anchored to the start / end of a word.
    /// </summary>
    private static string? PatternFilter(string slot, string op, string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw) || raw.Length > MaxValueLength) return null;
        if (op != "wildcard" && raw.IndexOfAny(['%', '_', '[']) >= 0) return null;

        var runs = AsciiRuns(raw);
        var kept = runs.Where(r => !IsStopWordFragment(r.Text)).ToList();
        if (kept.Count == 0) return null;
        if (kept.Count > MaxPatternWords) kept = kept.OrderByDescending(r => r.Text.Length).Take(MaxPatternWords).ToList();

        var clauses = new List<string>(kept.Count);
        foreach (var run in kept.OrderBy(r => r.Start))
        {
            var word = run.Text.ToLowerInvariant();
            var anchoredStart = op == "startsWith" && run.Start == 0;
            var anchoredEnd = op == "endsWith" && run.End == raw.Length;
            var regex = anchoredStart ? $"/{word}.*/" : anchoredEnd ? $"/.*{word}/" : $"/.*{word}.*/";
            clauses.Add($"search.ismatch('{regex}', '{slot}', 'full', 'any')");
        }
        return string.Join(" and ", clauses);
    }

    /// <summary>The maximal runs of ASCII letters and digits of the text, with where each starts and ends.</summary>
    private static List<(string Text, int Start, int End)> AsciiRuns(string text)
    {
        var runs = new List<(string, int, int)>();
        var start = -1;
        for (var i = 0; i <= text.Length; i++)
        {
            var isRunChar = i < text.Length && text[i] < 128 && char.IsAsciiLetterOrDigit(text[i]);
            if (isRunChar && start < 0) start = i;
            else if (!isRunChar && start >= 0)
            {
                runs.Add((text[start..i], start, i));
                start = -1;
            }
        }
        return runs;
    }

    private static bool IsStopWordFragment(string run) =>
        StopWords.Any(w => w.Contains(run, StringComparison.OrdinalIgnoreCase));

    // ── numbers ──────────────────────────────────────────────────────────────────────────────

    private static string? NumberFilter(string slot, FilterCondition c)
    {
        switch (c.Operator)
        {
            case "isEmpty":
                return $"{slot} eq null";
            case "eq" or "gt" or "gte" or "lt" or "lte":
                return TryNumber(c.Value, out var single) ? NumberBounds(slot, c.Operator, single) : null;
            case "in":
            {
                var clauses = new List<string>();
                foreach (var raw in ParseValueList(c.Value))
                {
                    if (!TryNumber(raw, out var listed)) return null;
                    clauses.Add(NumberBounds(slot, "eq", listed));
                }
                return clauses.Count == 0 ? null : AnyOf(clauses);
            }
            default:
                return null;
        }
    }

    private static string NumberBounds(string slot, string op, double value)
    {
        var margin = Math.Max(NumberMarginAbsolute, Math.Abs(value) * NumberMarginRelative);
        var low = FormatNumber(value - margin);
        var high = FormatNumber(value + margin);
        return op switch
        {
            "gt" or "gte" => $"{slot} ge {low}",
            "lt" or "lte" => $"{slot} le {high}",
            _ => $"({slot} ge {low} and {slot} le {high})"
        };
    }

    private static bool TryNumber(string? raw, out double value)
    {
        value = 0;
        if (string.IsNullOrWhiteSpace(raw) || !decimal.TryParse(raw.Trim(), NumberStyles.Number, CultureInfo.InvariantCulture, out var parsed)) return false;
        value = (double)parsed;
        return !double.IsNaN(value) && !double.IsInfinity(value);
    }

    private static string FormatNumber(double value) => value.ToString("0.############", CultureInfo.InvariantCulture);

    // ── dates ────────────────────────────────────────────────────────────────────────────────

    private static string? DateFilter(string slot, FilterCondition c)
    {
        var op = c.Operator;
        if (op == "isEmpty") return $"{slot} eq null";

        // Plain and date_* comparisons answer the same question here (a day or an instant), widened below.
        var kind = op switch
        {
            "eq" or "date_eq" => "eq",
            "gt" or "date_gt" or "gte" or "date_gte" => "ge",
            "lt" or "date_lt" or "lte" or "date_lte" => "le",
            "in" => "in",
            _ => null
        };
        if (kind == null) return null;

        if (kind == "in")
        {
            var clauses = new List<string>();
            foreach (var raw in ParseValueList(c.Value))
            {
                if (!TryInstant(raw, out var instant)) return null;
                clauses.Add(DateBounds(slot, "eq", instant));
            }
            return clauses.Count == 0 ? null : AnyOf(clauses);
        }
        return TryInstant(c.Value, out var at) ? DateBounds(slot, kind, at) : null;   // a relative date ("today") is left to SQL
    }

    private static string DateBounds(string slot, string kind, DateTime instant)
    {
        var low = Iso(instant.AddDays(-DateMarginDays));
        var high = Iso(instant.AddDays(1 + DateMarginDays));
        return kind switch
        {
            "ge" => $"{slot} ge {low}",
            "le" => $"{slot} le {high}",
            _ => $"({slot} ge {low} and {slot} le {high})"
        };
    }

    /// <summary>The instant a date value means, read the way the SQL filter reads it (universal time).</summary>
    private static bool TryInstant(string? raw, out DateTime instant)
    {
        instant = default;
        if (string.IsNullOrWhiteSpace(raw) || RelativeFilterDate.IsRelative(raw) || RelativeFilterDate.IsPeriod(raw)) return false;
        if (!DateTime.TryParse(raw.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out instant)) return false;
        // Keep the widened bounds inside what a DateTimeOffset can hold.
        return instant > DateTime.MinValue.AddDays(10) && instant < DateTime.MaxValue.AddDays(-10);
    }

    private static string Iso(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

    // ── yes / no ─────────────────────────────────────────────────────────────────────────────

    private static string? BooleanFilter(string slot, FilterCondition c)
    {
        switch (c.Operator)
        {
            case "isEmpty":
                return $"{slot} eq null";
            case "eq":
                return TryBoolean(c.Value, out var single) ? $"{slot} eq {(single ? "true" : "false")}" : null;
            case "in":
            {
                var clauses = new List<string>();
                foreach (var raw in ParseValueList(c.Value))
                {
                    if (!TryBoolean(raw, out var listed)) return null;
                    clauses.Add($"{slot} eq {(listed ? "true" : "false")}");
                }
                return clauses.Count == 0 ? null : AnyOf(clauses.Distinct().ToList());
            }
            default:
                return null;
        }
    }

    private static bool TryBoolean(string? raw, out bool value)
    {
        value = false;
        var text = raw?.Trim();
        if (text == "1") { value = true; return true; }
        if (text == "0") return true;
        return bool.TryParse(text, out value);
    }

    // ── shared ───────────────────────────────────────────────────────────────────────────────

    /// <summary>True when the text has at least one word the index keeps (letters/digits that are not all stop words) and is not
    /// unreasonably long.</summary>
    public static bool IsMatchable(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > MaxValueLength) return false;
        var word = new StringBuilder();
        foreach (var ch in value + " ")
        {
            if (char.IsLetterOrDigit(ch)) { word.Append(ch); continue; }
            if (word.Length > 0 && !StopWords.Contains(word.ToString())) return true;
            word.Clear();
        }
        return false;
    }

    /// <summary><c>search.ismatch('"text"', 'column', 'full', 'any')</c>: the text as a phrase, analyzed the way the indexed text is
    /// (lower-cased, split into words), so it matches whatever case the stored value has.</summary>
    public static string Phrase(string column, string text)
    {
        var lucene = "\"" + text.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";
        return $"search.ismatch('{lucene.Replace("'", "''")}', '{column}', 'full', 'any')";
    }

    /// <summary>The wire format of an "in" condition: a JSON string array, or a comma-separated list (as the SQL filter reads it).</summary>
    public static List<string> ParseValueList(string? raw)
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
                    var str = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
                    if (!string.IsNullOrWhiteSpace(str)) list.Add(str.Trim('"'));
                }
                return list;
            }
        }
        catch (JsonException) { /* not JSON: comma split below */ }
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
