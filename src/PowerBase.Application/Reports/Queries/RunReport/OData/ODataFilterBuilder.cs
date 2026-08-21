using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports.Queries.RunReport.OData;

public static class ODataFilterBuilder
{
    public static string? Build(FilterGroup? group, IReadOnlyList<AppField> allFields)
    {
        if (group == null || group.Nodes.Count == 0) return null;

        var fieldMap = allFields
            .Where(f => f.Fid.HasValue)
            .GroupBy(f => (long)f.Fid!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        return BuildGroup(group, fieldMap);
    }

    private static string? BuildGroup(FilterGroup group, Dictionary<long, AppField> fieldMap)
    {
        if (group.Nodes.Count == 0) return null;

        var parts = new List<string>();
        foreach (var node in group.Nodes)
        {
            if (node.Condition != null)
            {
                var condStr = BuildCondition(node.Condition, fieldMap);
                if (!string.IsNullOrEmpty(condStr))
                    parts.Add(condStr);
            }
            else if (node.Group != null)
            {
                var grpStr = BuildGroup(node.Group, fieldMap);
                if (!string.IsNullOrEmpty(grpStr))
                    parts.Add($"({grpStr})");
            }
        }

        if (parts.Count == 0) return null;
        if (parts.Count == 1) return parts[0];

        var logic = group.Logic.ToLowerInvariant() == "or" ? " or " : " and ";
        return string.Join(logic, parts);
    }

    private static string? BuildCondition(FilterCondition c, Dictionary<long, AppField> fieldMap)
    {
        if (!fieldMap.TryGetValue(c.FieldId, out var field)) return null;
        if (!field.IsSearchable && !field.IsFilterable) return null; // Cannot filter on fields not in Azure AI Search

        var fieldName = $"f_{c.FieldId}";
        var val = FormatValue(c.Value, field);

        var isMulti = field.TypeCode is "MultiSelect" or "MultiUser" or "CheckboxGroup";

        return c.Operator switch
        {
            "isEmpty"       => $"{fieldName} eq null or {fieldName} eq '' or {fieldName} eq '[]'",
            "isNotEmpty"    => $"{fieldName} ne null and {fieldName} ne '' and {fieldName} ne '[]'",
            "eq"            => isMulti ? BuildPhraseQuery(c.Value, fieldName, false) : $"{fieldName} eq {val}",
            "ne"            => isMulti ? BuildPhraseQuery(c.Value, fieldName, true) : $"{fieldName} ne {val}",
            "gt"            => $"{fieldName} gt {val}",
            "gte"           => $"{fieldName} ge {val}",
            "lt"            => $"{fieldName} lt {val}",
            "lte"           => $"{fieldName} le {val}",
            "contains"      => BuildRegexQuery(c.Value, fieldName, false, false),
            "notContains"   => BuildRegexQuery(c.Value, fieldName, false, true),
            "startsWith"    => BuildRegexQuery(c.Value, fieldName, true, false),
            "notStartsWith" => BuildRegexQuery(c.Value, fieldName, true, true),
            "in"            => BuildInClause(fieldName, c.Value, field, true),
            "notIn"         => BuildInClause(fieldName, c.Value, field, false),
            _               => null
        };
    }

    private static string BuildRegexQuery(string? value, string fieldName, bool isStart, bool isNot)
    {
        if (string.IsNullOrWhiteSpace(value)) return isNot ? "1 eq 1" : "1 eq 0";
        var words = value.ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>();
        foreach (var w in words)
        {
            var escaped = EscapeRegex(w);
            var regex = isStart ? $"/{escaped}.*/" : $"/.*{escaped}.*/";
            parts.Add($"+{regex}");
        }
        var query = string.Join(" ", parts);
        var ismatch = $"search.ismatch('{query}', '{fieldName}')";
        return isNot ? $"not {ismatch}" : ismatch;
    }

    private static string BuildPhraseQuery(string? value, string fieldName, bool isNot)
    {
        if (string.IsNullOrWhiteSpace(value)) return isNot ? "1 eq 1" : "1 eq 0";
        var escaped = EscapeRegex(value.ToLowerInvariant());
        var ismatch = $"search.ismatch('\"{escaped}\"', '{fieldName}')";
        return isNot ? $"not {ismatch}" : ismatch;
    }

    private static string BuildInClause(string fieldName, string? value, AppField field, bool isIn)
    {
        var values = ParseValueList(value);
        if (values.Count == 0) return isIn ? "id eq '00000000-0000-0000-0000-000000000000'" : "id ne '00000000-0000-0000-0000-000000000000'";

        var isMulti = field.TypeCode is "MultiSelect" or "MultiUser" or "CheckboxGroup";

        var parts = values.Select(v => 
        {
            if (isMulti)
            {
                return BuildPhraseQuery(v, fieldName, false);
            }
            return $"{fieldName} eq {FormatValue(v, field)}";
        });

        var logic = isIn ? " or " : " and ";
        var op = isIn ? "" : "not ";

        return $"{op}({string.Join(logic, parts)})";
    }

    private static string FormatValue(string? val, AppField field)
    {
        if (string.IsNullOrEmpty(val)) return "''";

        // Boolean, numbers, etc. in OData don't have quotes, but since we map most things to Edm.String in Azure Search (except IDs),
        // we should default to string wrapping unless it's explicitly a numeric type mapped as such.
        // In AzureSearchService.cs, f_X are mostly Edm.String, except maybe numbers? 
        // Let's assume they are stored as strings for simplicity, or we can check type.
        // Actually, EnsureTableSchemaAsync maps Number and Currency to Edm.Double, Checkbox to Edm.Boolean.
        if (field.TypeCode is "Number" or "Currency")
        {
            return double.TryParse(val, out var d) ? d.ToString(System.Globalization.CultureInfo.InvariantCulture) : "null";
        }
        if (field.TypeCode == "Checkbox")
        {
            return bool.TryParse(val, out var b) ? b.ToString().ToLowerInvariant() : "false";
        }

        return $"'{val.Replace("'", "''")}'";
    }

    private static string EscapeRegex(string val)
    {
        return System.Text.RegularExpressions.Regex.Escape(val).Replace("/", "\\/");
    }

    private static List<string> ParseValueList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(raw);
            if (arr != null) return arr.Where(v => !string.IsNullOrEmpty(v)).ToList();
        }
        catch (JsonException) { /* not JSON — fall through to comma split */ }
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
    }
}
