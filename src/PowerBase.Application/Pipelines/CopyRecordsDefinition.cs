using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PowerBase.Application.Reports;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

/// <summary>The persisted, table-local field contract for the Copy Records action.</summary>
public sealed class CopyRecordsDefinition
{
    public string ConnectionPublicId { get; set; } = "";
    public string SourceTable { get; set; } = "";
    public string DestinationTable { get; set; } = "";
    public List<string> SourceFields { get; set; } = new();
    public List<string> DestinationFields { get; set; } = new();
    public string MergeField { get; set; } = "";
    public string AdvancedQuery { get; set; } = "";
    public JsonElement AdvancedQueryFields { get; set; }
    public string TerminateOnError { get; set; } = "Yes";

    public static CopyRecordsDefinition Read(string json) =>
        JsonSerializer.Deserialize<CopyRecordsDefinition>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
        ?? throw Error("Copy Records configuration is required.");

    public static ValidationException Error(string message) =>
        new(new Dictionary<string, string[]> { ["CopyRecords"] = new[] { message } });

    public static Guid TableId(string value)
    {
        if (Guid.TryParse((value ?? "").Split(':').Last(), out var id)) return id;
        throw Error("Select a valid source and destination table.");
    }

    public void ValidateShape()
    {
        TableId(SourceTable);
        TableId(DestinationTable);
        if (SourceFields == null || SourceFields.Count is < 1 or > 250 || SourceFields.Distinct().Count() != SourceFields.Count)
            throw Error("Select between 1 and 250 distinct source fields.");
        if (DestinationFields == null || DestinationFields.Count != SourceFields.Count || DestinationFields.Any(string.IsNullOrWhiteSpace))
            throw Error("Map every source column to a destination field.");
        if (string.IsNullOrWhiteSpace(MergeField)) throw Error("Select a destination merge field.");
        if (TerminateOnError is not ("Yes" or "No")) throw Error("Terminate on user error must be Yes or No.");
        if (AdvancedQueryFields.ValueKind == JsonValueKind.Array && AdvancedQueryFields.GetArrayLength() > 0 && string.IsNullOrWhiteSpace(AdvancedQuery))
            throw Error("Re-enter the advanced query. Legacy references do not contain a complete query.");
    }

    public static AppField Field(string reference, IReadOnlyList<AppField> fields)
    {
        var raw = reference.StartsWith("fid_", StringComparison.Ordinal) ? reference[4..] : reference;
        var matches = int.TryParse(raw, out var fid)
            ? fields.Where(f => f.Fid == fid && !f.IsDeleted).ToList()
            : fields.Where(f => !f.IsDeleted && (f.Name == reference || f.Label == reference || f.PublicId.ToString() == reference)).ToList();
        if (matches.Count != 1 || !matches[0].Fid.HasValue) throw Error($"Field '{reference}' is missing or ambiguous.");
        return matches[0];
    }

    public static bool IsWritable(AppField field) => (field.IsPrimary || !field.IsSystem) && !PhysicalNaming.IsComputedTypeCode(field.TypeCode) && !IsAttachment(field);
    public static bool IsAttachment(AppField field) =>
        field.TypeCode.Contains("File", StringComparison.OrdinalIgnoreCase) || field.TypeCode.Contains("Attachment", StringComparison.OrdinalIgnoreCase);

    public void ValidateFields(IReadOnlyList<AppField> source, IReadOnlyList<AppField> destination)
    {
        ValidateShape();
        foreach (var fid in SourceFields)
            if (IsAttachment(Field(fid, source))) throw Error("Copy Records does not copy file attachments.");
        foreach (var fid in DestinationFields)
            if (!IsWritable(Field(fid, destination))) throw Error($"Destination field '{fid}' is read-only or an attachment.");
        var merge = Field(MergeField, destination);
        if (!(merge.IsUnique || merge.IsPrimary) || !IsWritable(merge))
            throw Error("The merge field must be a unique or primary destination field.");
        if (!DestinationFields.Any(f => Field(f, destination).Fid == merge.Fid))
            throw Error("Map a source column to the selected merge field.");
    }

    public static object? ConvertValue(object? value, AppField source, AppField destination)
    {
        if (value is JsonElement element) value = element.ValueKind switch
        {
            JsonValueKind.Null => null, JsonValueKind.String => element.GetString(),
            JsonValueKind.Number => element.GetDecimal(), JsonValueKind.True => true,
            JsonValueKind.False => false, _ => element.GetRawText()
        };
        if (value is null or DBNull) return null;
        string Kind(string type) => type.ToUpperInvariant() switch
        {
            "NUMBER" or "NUMERIC" or "CURRENCY" or "PERCENT" or "INTEGER" or "DECIMAL" or "FLOAT" or "RECORDID" or "RATING" => "number",
            "TEXT" or "TEXTMULTILINE" or "RICHTEXT" or "EMAIL" or "PHONE" or "URL" => "text",
            "CHECKBOX" or "BOOLEAN" or "BOOL" => "boolean", _ => type.ToUpperInvariant()
        };
        var from = Kind(PhysicalNaming.IsComputedTypeCode(source.TypeCode)
            ? FormulaTypeMap.FieldType(source.TypeCode, source.Settings)?.ToString() ?? source.TypeCode : source.TypeCode);
        var to = Kind(destination.TypeCode);
        if (from != to && !(from == "number" && to == "text"))
            throw Error($"Field types do not match: '{source.Name}' to '{destination.Name}'.");
        if (to == "text") return Convert.ToString(value, CultureInfo.InvariantCulture);
        return value;
    }

    /// <summary>Parses the supported Quickbase query grammar; unknown syntax fails closed.</summary>
    public static FilterGroup? ParseQuery(string? query, IReadOnlyList<AppField> fields)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        return new QueryParser(query, fields).Parse();
    }

    private sealed class QueryParser(string text, IReadOnlyList<AppField> fields)
    {
        private int position;
        private int depth;
        private void Space() { while (position < text.Length && char.IsWhiteSpace(text[position])) position++; }
        private bool Take(string token)
        {
            Space();
            if (!text.AsSpan(position).StartsWith(token, StringComparison.Ordinal)) return false;
            position += token.Length; return true;
        }
        private void Require(string token) { if (!Take(token)) throw Error($"Invalid advanced query near character {position + 1}."); }
        public FilterGroup Parse()
        {
            if (text.Length > 65536) throw Error("Advanced query is too long.");
            var result = Or(); Space();
            if (position != text.Length) throw Error($"Invalid advanced query near character {position + 1}.");
            return result;
        }
        private FilterGroup Or()
        {
            var group = new FilterGroup { Logic = "or", Nodes = new() { new() { Group = And() } } };
            while (Take("OR")) group.Nodes.Add(new() { Group = And() });
            return group;
        }
        private FilterGroup And()
        {
            var group = new FilterGroup { Logic = "and", Nodes = new() { Atom() } };
            while (Take("AND")) group.Nodes.Add(Atom());
            return group;
        }
        private FilterNode Atom()
        {
            if (++depth > 64) throw Error("Advanced query nesting is too deep.");
            try
            {
                if (Take("(")) { var group = Or(); Require(")"); return new() { Group = group }; }
                Require("{"); Space();
                var quoted = Take("'");
                var start = position;
                while (position < text.Length && char.IsAsciiDigit(text[position])) position++;
                var reference = text[start..position];
                if (quoted) Require("'");
                var field = Field(reference, fields);
                Require("."); start = position;
                while (position < text.Length && char.IsAsciiLetterUpper(text[position])) position++;
                var op = text[start..position] switch
                {
                    "EX" => "eq", "XEX" => "ne", "CT" => "contains", "XCT" => "notContains",
                    "SW" => "startsWith", "XSW" => "notStartsWith", "GT" or "AF" => "gt",
                    "GTE" or "OAF" => "gte", "LT" or "BF" => "lt", "LTE" or "OBF" => "lte",
                    "WC" => "wildcard", "XWC" => "notWildcard", "HAS" => "includes", "XHAS" => "notIncludes",
                    _ => throw Error($"Unsupported advanced query operator at character {start + 1}.")
                };
                Require(".");
                var quotedValue = Take("'");
                var value = new System.Text.StringBuilder();
                var closed = false;
                while (position < text.Length)
                {
                    var c = text[position++];
                    if (!quotedValue && c == '}') { position--; closed = true; break; }
                    if (c == '\\' && position < text.Length)
                    {
                        var escaped = text[position++];
                        if (op is "wildcard" or "notWildcard" && escaped is '*' or '?') value.Append('\\');
                        value.Append(escaped);
                    }
                    else if (quotedValue && c == '\'') { closed = true; break; }
                    else value.Append(c);
                }
                if (!closed) throw Error("Unterminated value in advanced query.");
                Require("}");
                var literal = value.ToString();
                if (!quotedValue && !decimal.TryParse(literal, NumberStyles.Number, CultureInfo.InvariantCulture, out _))
                    throw Error("Text query values must be enclosed in single quotes.");
                if (literal.StartsWith("_FID_", StringComparison.Ordinal))
                {
                    if (op is not ("eq" or "ne" or "gt" or "gte" or "lt" or "lte"))
                        throw Error("This field-to-field comparison operator is not supported.");
                    var other = Field(literal[5..], fields);
                    return new() { Condition = new() { FieldId = field.Fid!.Value, Operator = op, ValueMode = "field", ValueFieldId = other.Fid } };
                }
                if (op is "includes" or "notIncludes")
                {
                    var parts = literal.Split(';', StringSplitOptions.None);
                    if (parts.Any(string.IsNullOrWhiteSpace)) throw Error("HAS requires non-empty list values.");
                    return new() { Group = new() { Logic = op == "includes" ? "and" : "or", Nodes = parts.Select(part => new FilterNode
                    { Condition = new() { FieldId = field.Fid!.Value, Operator = op, Value = part } }).ToList() } };
                }
                // LIKE metacharacters are literal in CT/SW, not SQL pattern syntax.
                if (op is "contains" or "notContains" or "startsWith" or "notStartsWith")
                    literal = literal.Replace("[", "[[]").Replace("%", "[%]").Replace("_", "[_]");
                return new() { Condition = new() { FieldId = field.Fid!.Value, Operator = op, Value = literal } };
            }
            finally { depth--; }
        }
    }
}
