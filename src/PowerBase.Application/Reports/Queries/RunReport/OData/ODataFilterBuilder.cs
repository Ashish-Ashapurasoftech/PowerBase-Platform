using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports.Queries.RunReport.OData;

public static class ODataFilterBuilder
{
    private static readonly HashSet<string> SupportedOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "isEmpty", "isNotEmpty", "eq", "ne", "gt", "gte", "lt", "lte",
        "contains", "notContains", "startsWith", "notStartsWith", "in", "notIn",
    };

    /// <summary>True when every condition of the tree can be written as an Azure AI Search filter. A condition on
    /// an address sub-field, one that compares to another field, or one using an operator <see cref="Build"/>
    /// has no translation for would otherwise be silently dropped or widened (a "City" condition would search
    /// the whole address) — so a tree holding one must be run through SQL instead.</summary>
    public static bool CanExpress(FilterGroup? group)
    {
        if (group is null) return true;
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c)
            {
                if (!string.IsNullOrWhiteSpace(c.SubField)) return false;
                if (!SupportedOperators.Contains(c.Operator)) return false;
                if (!string.IsNullOrWhiteSpace(c.ValueMode) && !string.Equals(c.ValueMode, "literal", StringComparison.OrdinalIgnoreCase)) return false;
            }
            if (node.Group is not null && !CanExpress(node.Group)) return false;
        }
        return true;
    }

    public static string? Build(FilterGroup? group, IReadOnlyList<AppField> allFields)
    {
        if (group == null || group.Nodes.Count == 0) return null;

        var fieldMap = allFields
            .Where(f => f.Fid.HasValue)
            .GroupBy(f => (long)f.Fid!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var slotMap = PowerBase.Application.Search.SearchSlotResolver.BuildSlotMap(allFields);

        return BuildGroup(group, fieldMap, slotMap);
    }

    private static string? BuildGroup(FilterGroup group, Dictionary<long, AppField> fieldMap, Dictionary<long, string> slotMap)
    {
        if (group.Nodes.Count == 0) return null;

        var parts = new List<string>();
        foreach (var node in group.Nodes)
        {
            if (node.Condition != null)
            {
                var condStr = BuildCondition(node.Condition, fieldMap, slotMap);
                if (!string.IsNullOrEmpty(condStr))
                    parts.Add(condStr);
            }
            else if (node.Group != null)
            {
                var grpStr = BuildGroup(node.Group, fieldMap, slotMap);
                if (!string.IsNullOrEmpty(grpStr))
                    parts.Add($"({grpStr})");
            }
        }

        if (parts.Count == 0) return null;
        if (parts.Count == 1) return parts[0];

        var logic = group.Logic.ToLowerInvariant() == "or" ? " or " : " and ";
        return string.Join(logic, parts);
    }

    private static string? BuildCondition(FilterCondition c, Dictionary<long, AppField> fieldMap, Dictionary<long, string> slotMap)
    {
        if (!fieldMap.TryGetValue(c.FieldId, out var field)) return null;
        if (!field.IsSearchable && !field.IsFilterable) return null; // Cannot filter on fields not in Azure AI Search

        var fieldName = slotMap.TryGetValue(c.FieldId, out var slot) ? slot : $"f_{c.FieldId}";
        var val = FormatValue(c.Value, field);

        var isNumeric = PowerBase.Application.Search.SearchSlotResolver.IsNumeric(field.TypeCode);
        var isDate = PowerBase.Application.Search.SearchSlotResolver.IsDate(field.TypeCode);
        var isBoolean = PowerBase.Application.Search.SearchSlotResolver.IsBoolean(field.TypeCode);
        var isMulti = field.TypeCode is "MultiSelect" or "MultiUser" or "CheckboxGroup";

        return c.Operator switch
        {
            "isEmpty"       => isNumeric || isDate || isBoolean 
                                ? $"{fieldName} eq null" 
                                : $"{fieldName} eq null or {fieldName} eq '' or {fieldName} eq '[]'",
            "isNotEmpty"    => isNumeric || isDate || isBoolean 
                                ? $"{fieldName} ne null" 
                                : $"{fieldName} ne null and {fieldName} ne '' and {fieldName} ne '[]'",
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

        if (PowerBase.Application.Search.SearchSlotResolver.IsNumeric(field.TypeCode))
        {
            var num = PowerBase.Application.Search.SearchSlotResolver.ParseDouble(val);
            if (num.HasValue)
                return num.Value.ToString("0.####################", System.Globalization.CultureInfo.InvariantCulture);
            return "0";
        }

        if (PowerBase.Application.Search.SearchSlotResolver.IsDate(field.TypeCode))
        {
            var dto = PowerBase.Application.Search.SearchSlotResolver.ParseDateTimeOffset(val);
            if (dto.HasValue)
                return dto.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", System.Globalization.CultureInfo.InvariantCulture);
            return $"'{val.Replace("'", "''")}'";
        }

        if (PowerBase.Application.Search.SearchSlotResolver.IsBoolean(field.TypeCode))
        {
            var b = PowerBase.Application.Search.SearchSlotResolver.ParseBoolean(val);
            return (b.HasValue && b.Value) ? "true" : "false";
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
