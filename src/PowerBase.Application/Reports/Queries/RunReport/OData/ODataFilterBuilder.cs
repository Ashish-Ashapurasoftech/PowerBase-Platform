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

        var isOr = group.Logic?.ToLowerInvariant() == "or";
        var parts = new List<string>();
        foreach (var node in group.Nodes)
        {
            string? part = null;
            if (node.Condition != null)
            {
                part = BuildCondition(node.Condition, fieldMap);
            }
            else if (node.Group != null)
            {
                var grpStr = BuildGroup(node.Group, fieldMap);
                if (!string.IsNullOrEmpty(grpStr))
                    part = $"({grpStr})";
            }

            if (string.IsNullOrEmpty(part))
            {
                // For OR logic: if ANY child node cannot be evaluated in Azure AI Search,
                // the entire OR group cannot be safely pushed down without dropping valid matches.
                if (isOr) return null;
            }
            else
            {
                parts.Add(part);
            }
        }

        if (parts.Count == 0) return null;
        if (parts.Count == 1) return parts[0];

        var logic = isOr ? " or " : " and ";
        return string.Join(logic, parts);
    }

    private static string? BuildCondition(FilterCondition c, Dictionary<long, AppField> fieldMap)
    {
        if (!fieldMap.TryGetValue(c.FieldId, out var field)) return null;
        if (!field.IsSearchable && !field.IsFilterable) return null; // Cannot filter on fields not in Azure AI Search

        // Field-to-field comparisons (ValueMode == "field") cannot be evaluated in Azure OData; leave for SQL
        if (string.Equals(c.ValueMode, "field", StringComparison.OrdinalIgnoreCase)) return null;

        if (c.Operator is not ("isEmpty" or "isNotEmpty") && string.IsNullOrEmpty(c.Value))
            return null;

        var fieldName = $"f_{c.FieldId}";
        var isMulti = field.TypeCode is "MultiSelect" or "MultiUser" or "CheckboxGroup";
        var isNumeric = field.TypeCode is "Number" or "Numeric" or "Currency" or "Percent" or "Rating" or "Duration" or "Integer";
        var isDate = field.TypeCode is "Date" or "DateTime";

        // Numeric range comparisons (gt, gte, lt, lte) on string-typed Azure index fields produce
        // lexicographic string comparisons ("10" < "9"), which drops valid records. Return null
        // so that SQL Server / FormulaFilterSorter evaluates them with full mathematical precision.
        if (isNumeric && c.Operator is "gt" or "gte" or "lt" or "lte")
            return null;

        if (isDate)
        {
            if (c.Operator == "date_eq")
            {
                if (DateTime.TryParse(c.Value, out var dt))
                {
                    var utc = dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
                    var dayStart = utc.Date.ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
                    var dayEnd = utc.Date.AddDays(1).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
                    return $"{fieldName} ge '{dayStart}' and {fieldName} lt '{dayEnd}'";
                }
                return null;
            }

            if (c.Operator is "gt" or "gte" or "lt" or "lte")
            {
                if (DateTime.TryParse(c.Value, out var dtVal))
                {
                    var utc = (dtVal.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dtVal, DateTimeKind.Utc) : dtVal.ToUniversalTime()).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
                    return c.Operator switch
                    {
                        "gt"  => $"{fieldName} gt '{utc}'",
                        "gte" => $"{fieldName} ge '{utc}'",
                        "lt"  => $"{fieldName} lt '{utc}'",
                        "lte" => $"{fieldName} le '{utc}'",
                        _     => null
                    };
                }
                return null;
            }
        }

        var val = FormatValue(c.Value, field);

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
            "includes"      => BuildPhraseQuery(c.Value, fieldName, false),
            "notIncludes"   => BuildPhraseQuery(c.Value, fieldName, true),
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

        if ((field.TypeCode is "Date" or "DateTime") && DateTime.TryParse(val, out var dt))
        {
            var utc = (dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime()).ToString("yyyy-MM-ddTHH:mm:ssZ", System.Globalization.CultureInfo.InvariantCulture);
            return $"'{utc}'";
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
