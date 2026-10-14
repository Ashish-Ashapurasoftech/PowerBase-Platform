using System.Text.Json;
using System.Text.RegularExpressions;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

/// <summary>A PowerFlow step as far as the field-reference check needs it.</summary>
public sealed record PipelineStepSnapshot(string RefId, string Label, string Type, string Subtype, string? ConfigJson);

/// <summary>
/// Decides whether a PowerFlow step really uses a given field of a given table. A field ID (<c>fid_10</c>) is only
/// unique within its table, so a bare text match on "fid_10" also hits every other table's field 10; this resolves
/// which table each mention belongs to — the step's own table for a direct field (mapping, filter, field list, JSON key),
/// the source / destination table of a Copy Records step, or the table of the step a <c>{{steps.X.fid_N}}</c> /
/// <c>{{trigger.fid_N}}</c> token reads from (following loops to the step they loop over).
///
/// A field is recognised however a step stores it: <c>fid_N</c>, the field's name / label / public id (in field-type
/// properties and in token members), or its number inside an advanced query (<c>{6.EX.'x'}</c>, <c>_FID_6</c>).
/// A mention that resolves to no table at all — e.g. a token pointing at a step that no longer exists — is a dangling
/// reference to nothing, so it never blocks a delete.
/// </summary>
public static class PipelineFieldReferenceMatcher
{
    private static readonly Regex TokenRegex = new(@"\{\{[^{}]*\}\}", RegexOptions.Compiled);
    private static readonly Regex TokenSourceRegex = new(@"^\{\{\s*(?:steps\.)?([A-Za-z0-9_]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly string[] TableProperties = { "tablePublicId", "tableId", "tableLabel" };
    // Properties (or arrays of them) whose plain-string values name a field of the step's table.
    private static readonly string[] FieldProperties = { "field", "filterField", "fieldName", "mergeField", "mergeKeyFid", "id", "fieldId", "fieldPublicId", "column" };
    private const int MaxLoopDepth = 5;

    public static bool ReferencesField(PipelineStepSnapshot step, IReadOnlyList<PipelineStepSnapshot> pipelineSteps, int fid, Guid tablePublicId) =>
        ReferencesField(step, pipelineSteps, new AppField { Fid = fid, Name = string.Empty }, tablePublicId);

    public static bool ReferencesField(PipelineStepSnapshot step, IReadOnlyList<PipelineStepSnapshot> pipelineSteps, AppField field, Guid tablePublicId)
    {
        if (string.IsNullOrWhiteSpace(step.ConfigJson) || !field.Fid.HasValue) return false;
        var fid = field.Fid.Value;

        JsonElement root;
        try
        {
            using var document = JsonDocument.Parse(step.ConfigJson);
            root = document.RootElement.Clone();
        }
        catch (JsonException)
        {
            return false; // unreadable config: no usable reference to check
        }
        if (root.ValueKind != JsonValueKind.Object) return false;

        var fidPattern = new Regex($@"(?<![A-Za-z0-9_])fid_{fid}(?![0-9])", RegexOptions.IgnoreCase);
        // Advanced queries name fields by number: {6.EX.'x'} or a field-to-field _FID_6.
        var queryPattern = new Regex($@"\{{\s*'?{fid}'?\s*\.|_FID_{fid}(?![0-9])");
        var aliases = Aliases(field);
        var ownTable = TableOf(step, pipelineSteps, 0);

        foreach (var property in root.EnumerateObject())
        {
            var isQuery = property.Name.StartsWith("advancedQuery", StringComparison.OrdinalIgnoreCase);
            // The table a plain (token-free) field reference on this property belongs to.
            var propertyTable = TableForProperty(root, property.Name, ownTable);

            foreach (var (text, immediate, isKey) in Strings(property.Name, property.Value))
            {
                if (isQuery && propertyTable == tablePublicId && queryPattern.IsMatch(TokenRegex.Replace(text, " "))) return true;

                foreach (Match token in TokenRegex.Matches(text))
                    if (TokenReadsField(token.Value, fidPattern, aliases, pipelineSteps, tablePublicId)) return true;

                if (propertyTable != tablePublicId) continue;
                var direct = TokenRegex.Replace(text, " ");
                if (fidPattern.IsMatch(direct)) return true;
                if (!isKey && IsFieldProperty(immediate) && NamesField(direct, fid, aliases)) return true;
            }
        }

        return false;
    }

    private static bool IsFieldProperty(string property) =>
        FieldProperties.Any(p => property.Equals(p, StringComparison.OrdinalIgnoreCase)) ||
        property.EndsWith("fields", StringComparison.OrdinalIgnoreCase);

    /// <summary>The field's name, label and public id: the other ways a step can spell it.</summary>
    private static HashSet<string> Aliases(AppField field)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var alias in new[] { field.Name, field.Label, field.PublicId == Guid.Empty ? null : field.PublicId.ToString() })
            if (!string.IsNullOrWhiteSpace(alias)) set.Add(alias.Trim());
        return set;
    }

    private static bool NamesField(string value, int fid, HashSet<string> aliases)
    {
        var text = value.Trim();
        return text.Length > 0 && (aliases.Contains(text) || text == fid.ToString());
    }

    private static bool TokenReadsField(string token, Regex fidPattern, HashSet<string> aliases, IReadOnlyList<PipelineStepSnapshot> pipelineSteps, Guid tablePublicId)
    {
        var source = TokenSourceRegex.Match(token);
        if (!source.Success) return false;

        // Everything after the step reference: fid_6, item.fid_6, C_name, "Customer Name | upper" ...
        var members = token.Substring(source.Length).Trim('{', '}', ' ', '.');
        var namesField = fidPattern.IsMatch(members) ||
            members.Split('.', '|').Any(m => m.Trim().Length > 0 && aliases.Contains(m.Trim()));
        if (!namesField) return false;

        var segment = source.Groups[1].Value;
        if (segment.Equals("variables", StringComparison.OrdinalIgnoreCase)) return false;

        var sources = segment.Equals("trigger", StringComparison.OrdinalIgnoreCase)
            ? pipelineSteps.Where(s => s.Type.Equals("trigger", StringComparison.OrdinalIgnoreCase)).ToList()
            : pipelineSteps.Where(s => s.RefId.Equals(segment, StringComparison.OrdinalIgnoreCase)).ToList();
        if (sources.Count == 0) return false;

        foreach (var sourceStep in sources)
        {
            var table = TableOf(sourceStep, pipelineSteps, 0);
            if (table == tablePublicId) return true;
        }
        return false;
    }

    /// <summary>Copy Records works on two tables: sourceFields / advancedQuery belong to sourceTable; destinationFields and
    /// mergeField to destinationTable. Every other step keeps its fields on its own table.</summary>
    private static Guid? TableForProperty(JsonElement root, string property, Guid? ownTable)
    {
        if (property.StartsWith("source", StringComparison.OrdinalIgnoreCase) || property.StartsWith("advancedQuery", StringComparison.OrdinalIgnoreCase))
            return ParseTable(StringProperty(root, "sourceTable")) ?? ownTable;
        if (property.StartsWith("destination", StringComparison.OrdinalIgnoreCase) || property.Equals("mergeField", StringComparison.OrdinalIgnoreCase))
            return ParseTable(StringProperty(root, "destinationTable")) ?? ownTable;
        return ownTable;
    }

    /// <summary>A table setting may carry a prefix ("connection:guid"); the id is the last segment.</summary>
    private static Guid? ParseTable(string? text) =>
        text is not null && Guid.TryParse(text.Split(':').Last(), out var id) ? id : null;

    /// <summary>The table a step works on: its own table setting, or for a loop the table of the step it loops over.</summary>
    private static Guid? TableOf(PipelineStepSnapshot step, IReadOnlyList<PipelineStepSnapshot> pipelineSteps, int depth)
    {
        if (string.IsNullOrWhiteSpace(step.ConfigJson) || depth > MaxLoopDepth) return null;
        try
        {
            using var document = JsonDocument.Parse(step.ConfigJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;

            var table = ParseTable(StringProperty(document.RootElement, TableProperties));
            if (table.HasValue) return table;

            var loopOver = StringProperty(document.RootElement, "loopOverStepId");
            if (loopOver is not null)
            {
                var target = pipelineSteps.FirstOrDefault(s => s.RefId.Equals(loopOver, StringComparison.OrdinalIgnoreCase));
                if (target is not null) return TableOf(target, pipelineSteps, depth + 1);
            }
        }
        catch (JsonException)
        {
        }
        return null;
    }

    private static string? StringProperty(JsonElement element, params string[] names)
    {
        foreach (var property in element.EnumerateObject())
            if (property.Value.ValueKind == JsonValueKind.String && names.Any(n => property.Name.Equals(n, StringComparison.OrdinalIgnoreCase)))
                return property.Value.GetString();
        return null;
    }

    /// <summary>Every string under a property: its values and — because rowValues / fieldValues / columnMappings use the
    /// field as the JSON key — its object keys too. <c>Immediate</c> is the nearest property name (array items inherit
    /// the array's property).</summary>
    private static IEnumerable<(string Text, string Immediate, bool IsKey)> Strings(string immediate, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return (element.GetString() ?? string.Empty, immediate, false);
                break;
            case JsonValueKind.Number:
                yield return (element.GetRawText(), immediate, false);
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return (property.Name, immediate, true);
                    foreach (var item in Strings(property.Name, property.Value))
                        yield return item;
                }
                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                    foreach (var nested in Strings(immediate, item))
                        yield return nested;
                break;
        }
    }
}
