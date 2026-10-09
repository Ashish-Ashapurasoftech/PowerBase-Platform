using System.Text;
using System.Text.Json;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>The outcome of <see cref="PipelineAiSearchPlanner.Evaluate"/>: the Azure AI Search filter to use, or why the
/// search has to be answered from SQL instead.</summary>
public sealed record PipelineAiSearchPlan(string? ODataFilter, string? SqlReason)
{
    public bool UseAiSearch => ODataFilter != null;
}

/// <summary>
/// Decides whether a pipeline search (Search Records, Copy Records) may be answered by Azure AI Search, and builds the
/// AI filter when it may. Anything the index cannot answer exactly is sent to SQL — never half-answered.
///
/// AI Search is only a way to find <em>candidate</em> record ids quickly: the caller reads those records from SQL and applies
/// the complete filter to them there, so the AI filter must never leave out a record SQL would match. It therefore
/// returns a plan only when <em>every</em> condition satisfies all of:
///   • the field exists in the table and is a physical column — not a Formula, Lookup, Summary, Report Link or Action
///     Button (no column, never indexed) and not a Reference (a relationship);
///   • the field is both <c>IsSearchable</c> and <c>IsFilterable</c> (a field that is not searchable is indexed as an empty
///     value, see RecordRepository.GetSearchableFieldsAsync);
///   • the field holds plain text (the index keeps every field as a string; numbers, dates, yes/no and users would be
///     compared as text — "10" before "2", dates in a culture format — so those stay in SQL);
///   • the condition compares to a literal (not to another field), without a sub-field, with a non-empty value;
///   • the operator is <c>eq</c> or <c>in</c>. They become a case-insensitive phrase match (SQL compares text without regard
///     to case; an AI <c>eq</c> would not). <c>contains</c>/<c>startsWith</c> need wildcard or regex terms, which Azure AI
///     Search caps at 1,000 matching terms — they could silently miss records — and negations would return nearly the
///     whole table, so those stay in SQL too.
/// </summary>
public static class PipelineAiSearchPlanner
{
    /// <summary>Text-like types whose index value is the very text the SQL column holds.</summary>
    private static readonly HashSet<string> TextTypes = new(StringComparer.Ordinal)
        { "Text", "TextMultiLine", "RichText", "Email", "Phone", "Url", "SingleSelect" };

    /// <summary>Lucene's default English stop words. The index drops them from the text it analyzes, so a value made only of
    /// them cannot be matched through the index.</summary>
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        "a", "an", "and", "are", "as", "at", "be", "but", "by", "for", "if", "in", "into", "is", "it", "no", "not", "of",
        "on", "or", "such", "that", "the", "their", "then", "there", "these", "they", "this", "to", "was", "will", "with"
    };

    /// <summary>The most "in" list values in the whole filter. A long list becomes too many query clauses — Azure AI Search warns that
    /// hundreds risk its limit — and, in the SQL that verifies the candidates, too many parameters (each value is one, under the
    /// 2,100 limit the candidate ids share). Together with <see cref="MaxConditions"/> the filter stays within 100 clauses. Past it,
    /// the search is read from SQL.</summary>
    public const int MaxInValues = 70;

    /// <summary>The most conditions in the whole filter, for the same two reasons.</summary>
    public const int MaxConditions = 30;

    public const int MaxValueLength = 256;

    public static PipelineAiSearchPlan Evaluate(FilterGroup? tree, IReadOnlyList<AppField> fields)
    {
        if (tree == null) return Sql("the search has no filter");

        var fieldsByFid = fields.Where(f => f.Fid.HasValue)
            .GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
        string? reason = null;
        var conditions = 0;
        var inValues = 0;
        var odata = BuildGroup(tree);
        if (reason != null) return Sql(reason);
        if (conditions == 0 || string.IsNullOrWhiteSpace(odata)) return Sql("the search has no usable condition");
        return new PipelineAiSearchPlan(odata, null);

        string? BuildGroup(FilterGroup group)
        {
            var parts = new List<string>();
            foreach (var node in group.Nodes)
            {
                string? part = null;
                if (node.Condition != null) part = BuildCondition(node.Condition);
                else if (node.Group != null)
                {
                    var inner = BuildGroup(node.Group);
                    if (!string.IsNullOrEmpty(inner)) part = $"({inner})";
                }
                if (reason != null) return null;
                if (!string.IsNullOrEmpty(part)) parts.Add(part);
            }
            if (parts.Count == 0) return null;
            return parts.Count == 1 ? parts[0] : string.Join(string.Equals(group.Logic, "or", StringComparison.OrdinalIgnoreCase) ? " or " : " and ", parts);
        }

        string? BuildCondition(FilterCondition c)
        {
            if (++conditions > MaxConditions) { reason = $"the filter has more than {MaxConditions} conditions"; return null; }
            if (!fieldsByFid.TryGetValue(c.FieldId, out var field)) { reason = $"field {c.FieldId} is not in the table"; return null; }
            var name = string.IsNullOrWhiteSpace(field.Label) ? field.Name : field.Label;
            if (PhysicalNaming.IsComputedTypeCode(field.TypeCode) || field.TypeCode == "Reference")
            { reason = $"'{name}' is a {field.TypeCode} field (no physical column / a relationship)"; return null; }
            if (!(field.IsSearchable && field.IsFilterable))
            { reason = $"'{name}' is not both searchable and filterable, so it is not in the search index"; return null; }
            if (!TextTypes.Contains(field.TypeCode))
            { reason = $"'{name}' is a {field.TypeCode} field; the index holds it as text, which compares differently from SQL"; return null; }
            if (!string.IsNullOrWhiteSpace(c.SubField)) { reason = $"'{name}' is filtered by a sub-field"; return null; }
            if (c.ValueFieldId.HasValue || !(string.IsNullOrEmpty(c.ValueMode) || string.Equals(c.ValueMode, "literal", StringComparison.OrdinalIgnoreCase)))
            { reason = $"the condition on '{name}' compares to another field or is not a literal"; return null; }

            var column = $"f_{c.FieldId}";
            switch (c.Operator)
            {
                case "eq":
                    if (!IsMatchable(c.Value)) { reason = $"the value for '{name}' cannot be matched through the index"; return null; }
                    return Phrase(column, c.Value!);
                case "in":
                {
                    var values = ParseValueList(c.Value);
                    inValues += values.Count;
                    if (values.Count == 0 || inValues > MaxInValues || values.Any(v => !IsMatchable(v)))
                    { reason = $"the list for '{name}' is empty, makes the filter's lists longer than {MaxInValues} values, or has a value that cannot be matched through the index"; return null; }
                    return values.Count == 1 ? Phrase(column, values[0]) : $"({string.Join(" or ", values.Select(v => Phrase(column, v)))})";
                }
                default:
                    reason = $"operator '{c.Operator}' on '{name}' is not answered from the index";
                    return null;
            }
        }
    }

    private static PipelineAiSearchPlan Sql(string reason) => new(null, reason);

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

    /// <summary><c>search.ismatch('"text"', 'f_N', 'full', 'any')</c>: the text as a phrase, analyzed the way the indexed text is
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
