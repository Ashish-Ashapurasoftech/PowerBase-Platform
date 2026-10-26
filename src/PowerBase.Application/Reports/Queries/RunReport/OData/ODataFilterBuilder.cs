using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Reports.Queries.RunReport.OData;

public static class ODataFilterBuilder
{
    private enum RegexMatchMode { Contains, StartsWith, EndsWith }

    private static readonly HashSet<string> SupportedOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "isEmpty", "isNotEmpty", "eq", "ne", "gt", "gte", "lt", "lte",
        "contains", "notContains", "startsWith", "notStartsWith", "endsWith", "notEndsWith",
        "in", "notIn", "date_eq", "date_ne", "date_gt", "date_gte", "date_lt", "date_lte",
        "wildcard", "notWildcard", "includes", "notIncludes"
    };

    private static readonly HashSet<string> TextOnlyPatternOperators = new(StringComparer.OrdinalIgnoreCase)
    {
        "contains", "notContains", "startsWith", "notStartsWith", "endsWith", "notEndsWith",
        "wildcard", "notWildcard", "includes", "notIncludes"
    };

    /// <summary>True when every condition of the tree can be written as an Azure AI Search filter. A condition on
    /// an address sub-field, one that compares to another field, or one using an operator <see cref="Build"/>
    /// has no translation for would otherwise be silently dropped or widened (a "City" condition would search
    /// the whole address) — so a tree holding one must be run through SQL instead.</summary>
    public static bool CanExpress(FilterGroup? group) => CanExpress(group, null);

    public static bool CanExpress(FilterGroup? group, IReadOnlyList<AppField>? allFields)
    {
        if (group is null) return true;

        Dictionary<long, AppField>? fieldMap = null;
        Dictionary<long, string>? slotMap = null;
        bool isWide = false;
        if (allFields != null)
        {
            fieldMap = allFields
                .Where(f => f.Fid.HasValue)
                .GroupBy(f => (long)f.Fid!.Value)
                .ToDictionary(g => g.Key, g => g.First());
            slotMap = PowerBase.Application.Search.SearchSlotResolver.BuildSlotMap(allFields);
            isWide = PowerBase.Application.Search.SearchSlotResolver.IsWideTable(allFields);
        }

        return CanExpressNodes(group, fieldMap, slotMap, isWide);
    }

    private static bool CanExpressNodes(FilterGroup group, Dictionary<long, AppField>? fieldMap, Dictionary<long, string>? slotMap, bool isWide)
    {
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } c)
            {
                if (!string.IsNullOrWhiteSpace(c.SubField)) return false;
                if (!SupportedOperators.Contains(c.Operator)) return false;
                if (!string.IsNullOrWhiteSpace(c.ValueMode) && !string.Equals(c.ValueMode, "literal", StringComparison.OrdinalIgnoreCase)) return false;

                if (fieldMap != null)
                {
                    if (!fieldMap.TryGetValue(c.FieldId, out var field)) return false;
                    if (!field.IsSearchable && !field.IsFilterable) return false;
                    if (!PowerBase.Application.Search.SearchSlotResolver.IsIndexableType(field.TypeCode)) return false; // ranges, durations: SQL

                    if (!isWide && slotMap != null && slotMap.TryGetValue(c.FieldId, out var slot) && slot.StartsWith("f_"))
                    {
                        return false; // Field not mapped to a shared index slot
                    }

                    var isNumeric = PowerBase.Application.Search.SearchSlotResolver.IsNumeric(field.TypeCode);
                    var isDate = PowerBase.Application.Search.SearchSlotResolver.IsDate(field.TypeCode);
                    var isBoolean = PowerBase.Application.Search.SearchSlotResolver.IsBoolean(field.TypeCode);

                    if (TextOnlyPatternOperators.Contains(c.Operator) && (isNumeric || isDate || isBoolean))
                    {
                        return false; // search.ismatch is only valid on text fields
                    }

                    if (c.Operator.StartsWith("date_", StringComparison.OrdinalIgnoreCase) && !isDate)
                    {
                        return false; // date_* operators only valid on date fields
                    }
                }
            }
            if (node.Group is not null && !CanExpressNodes(node.Group, fieldMap, slotMap, isWide)) return false;
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
        var isWide = PowerBase.Application.Search.SearchSlotResolver.IsWideTable(allFields);

        return BuildGroup(group, fieldMap, slotMap, isWide);
    }

    private static string? BuildGroup(FilterGroup group, Dictionary<long, AppField> fieldMap, Dictionary<long, string> slotMap, bool isWide)
    {
        if (group.Nodes.Count == 0) return null;

        var parts = new List<string>();
        foreach (var node in group.Nodes)
        {
            if (node.Condition != null)
            {
                var condStr = BuildCondition(node.Condition, fieldMap, slotMap, isWide);
                if (!string.IsNullOrEmpty(condStr))
                    parts.Add(condStr);
            }
            else if (node.Group != null)
            {
                var grpStr = BuildGroup(node.Group, fieldMap, slotMap, isWide);
                if (!string.IsNullOrEmpty(grpStr))
                    parts.Add($"({grpStr})");
            }
        }

        if (parts.Count == 0) return null;
        if (parts.Count == 1) return parts[0];

        var logic = group.Logic.ToLowerInvariant() == "or" ? " or " : " and ";
        return string.Join(logic, parts);
    }

    private static string? BuildCondition(FilterCondition c, Dictionary<long, AppField> fieldMap, Dictionary<long, string> slotMap, bool isWide)
    {
        if (!fieldMap.TryGetValue(c.FieldId, out var field)) return null;
        if (!field.IsSearchable && !field.IsFilterable) return null; // Cannot filter on fields not in Azure AI Search
        if (!PowerBase.Application.Search.SearchSlotResolver.IsIndexableType(field.TypeCode)) return null;

        if (!slotMap.TryGetValue(c.FieldId, out var slot))
        {
            if (!isWide) return null;
            slot = $"f_{c.FieldId}";
        }
        else if (!isWide && slot.StartsWith("f_"))
        {
            return null; // Not present in shared typed index
        }

        var fieldName = slot;
        var isNumeric = PowerBase.Application.Search.SearchSlotResolver.IsNumeric(field.TypeCode);
        var isDate = PowerBase.Application.Search.SearchSlotResolver.IsDate(field.TypeCode);
        var isBoolean = PowerBase.Application.Search.SearchSlotResolver.IsBoolean(field.TypeCode);
        var isMulti = field.TypeCode is "MultiSelect" or "MultiUser" or "CheckboxGroup";

        if (isDate)
        {
            return BuildDateCondition(c.Operator, c.Value, fieldName);
        }

        if (isNumeric)
        {
            return BuildNumericCondition(c.Operator, c.Value, fieldName);
        }

        if (isBoolean)
        {
            return BuildBooleanCondition(c.Operator, c.Value, fieldName);
        }

        return BuildStringCondition(c.Operator, c.Value, fieldName, isMulti);
    }

    private static string? BuildDateCondition(string op, string? rawValue, string fieldName)
    {
        if (string.Equals(op, "isEmpty", StringComparison.OrdinalIgnoreCase))
            return $"{fieldName} eq null";
        if (string.Equals(op, "isNotEmpty", StringComparison.OrdinalIgnoreCase))
            return $"{fieldName} ne null";

        if (string.Equals(op, "in", StringComparison.OrdinalIgnoreCase) || string.Equals(op, "notIn", StringComparison.OrdinalIgnoreCase))
        {
            var values = ParseValueList(rawValue);
            if (values.Count == 0)
                return op.Equals("in", StringComparison.OrdinalIgnoreCase)
                    ? "id eq '00000000-0000-0000-0000-000000000000'"
                    : "id ne '00000000-0000-0000-0000-000000000000'";

            var parts = new List<string>();
            foreach (var v in values)
            {
                var cond = BuildDateCondition("date_eq", v, fieldName);
                if (cond != null) parts.Add(cond);
            }
            if (parts.Count == 0) return null;
            var logic = op.Equals("in", StringComparison.OrdinalIgnoreCase) ? " or " : " and ";
            var prefix = op.Equals("in", StringComparison.OrdinalIgnoreCase) ? "" : "not ";
            return $"{prefix}({string.Join(logic, parts)})";
        }

        var dto = PowerBase.Application.Search.SearchSlotResolver.ParseDateTimeOffset(rawValue);
        if (!dto.HasValue) return null;

        var start = new DateTimeOffset(dto.Value.UtcDateTime.Date, TimeSpan.Zero);
        var nextDay = start.AddDays(1);
        var startIso = start.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);
        var nextDayIso = nextDay.ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        bool hasSpecificTime = rawValue != null && rawValue.Contains(':') &&
            (dto.Value.Hour != 0 || dto.Value.Minute != 0 || dto.Value.Second != 0);
        var instantIso = dto.Value.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ss.fffZ", CultureInfo.InvariantCulture);

        return op switch
        {
            "date_eq" => $"({fieldName} ge {startIso} and {fieldName} lt {nextDayIso})",
            "eq"      => hasSpecificTime ? $"{fieldName} eq {instantIso}" : $"({fieldName} ge {startIso} and {fieldName} lt {nextDayIso})",
            "date_ne" => $"not ({fieldName} ge {startIso} and {fieldName} lt {nextDayIso})",
            "ne"      => hasSpecificTime ? $"{fieldName} ne {instantIso}" : $"not ({fieldName} ge {startIso} and {fieldName} lt {nextDayIso})",
            "date_gt" => $"{fieldName} ge {nextDayIso}",
            "gt"      => hasSpecificTime ? $"{fieldName} gt {instantIso}" : $"{fieldName} ge {nextDayIso}",
            "date_gte" => $"{fieldName} ge {startIso}",
            "gte"     => hasSpecificTime ? $"{fieldName} ge {instantIso}" : $"{fieldName} ge {startIso}",
            "date_lt" => $"{fieldName} lt {startIso}",
            "lt"      => hasSpecificTime ? $"{fieldName} lt {instantIso}" : $"{fieldName} lt {startIso}",
            "date_lte" => $"{fieldName} lt {nextDayIso}",
            "lte"     => hasSpecificTime ? $"{fieldName} le {instantIso}" : $"{fieldName} lt {nextDayIso}",
            _         => null
        };
    }

    private static string? BuildNumericCondition(string op, string? rawValue, string fieldName)
    {
        if (string.Equals(op, "isEmpty", StringComparison.OrdinalIgnoreCase))
            return $"{fieldName} eq null";
        if (string.Equals(op, "isNotEmpty", StringComparison.OrdinalIgnoreCase))
            return $"{fieldName} ne null";

        if (string.Equals(op, "in", StringComparison.OrdinalIgnoreCase) || string.Equals(op, "notIn", StringComparison.OrdinalIgnoreCase))
        {
            var values = ParseValueList(rawValue);
            if (values.Count == 0)
                return op.Equals("in", StringComparison.OrdinalIgnoreCase)
                    ? "id eq '00000000-0000-0000-0000-000000000000'"
                    : "id ne '00000000-0000-0000-0000-000000000000'";

            var parts = new List<string>();
            foreach (var v in values)
            {
                var num = PowerBase.Application.Search.SearchSlotResolver.ParseDouble(v);
                if (num.HasValue)
                    parts.Add($"{fieldName} eq {num.Value.ToString("0.####################", CultureInfo.InvariantCulture)}");
            }
            if (parts.Count == 0) return null;
            var logic = op.Equals("in", StringComparison.OrdinalIgnoreCase) ? " or " : " and ";
            var prefix = op.Equals("in", StringComparison.OrdinalIgnoreCase) ? "" : "not ";
            return $"{prefix}({string.Join(logic, parts)})";
        }

        var d = PowerBase.Application.Search.SearchSlotResolver.ParseDouble(rawValue);
        if (!d.HasValue) return null;
        var val = d.Value.ToString("0.####################", CultureInfo.InvariantCulture);

        return op switch
        {
            "eq"  => $"{fieldName} eq {val}",
            "ne"  => $"{fieldName} ne {val}",
            "gt"  => $"{fieldName} gt {val}",
            "gte" => $"{fieldName} ge {val}",
            "lt"  => $"{fieldName} lt {val}",
            "lte" => $"{fieldName} le {val}",
            _     => null
        };
    }

    private static string? BuildBooleanCondition(string op, string? rawValue, string fieldName)
    {
        if (string.Equals(op, "isEmpty", StringComparison.OrdinalIgnoreCase))
            return $"{fieldName} eq null";
        if (string.Equals(op, "isNotEmpty", StringComparison.OrdinalIgnoreCase))
            return $"{fieldName} ne null";

        var b = PowerBase.Application.Search.SearchSlotResolver.ParseBoolean(rawValue) ?? false;
        var val = b ? "true" : "false";

        return op switch
        {
            "eq"  => $"{fieldName} eq {val}",
            "ne"  => $"{fieldName} ne {val}",
            "in"  => $"{fieldName} eq {val}",
            "notIn" => $"{fieldName} ne {val}",
            _     => null
        };
    }

    private static string? BuildStringCondition(string op, string? rawValue, string fieldName, bool isMulti)
    {
        return op switch
        {
            "isEmpty"       => $"{fieldName} eq null or {fieldName} eq '' or {fieldName} eq '[]'",
            "isNotEmpty"    => $"{fieldName} ne null and {fieldName} ne '' and {fieldName} ne '[]'",
            "eq"            => isMulti ? BuildPhraseQuery(rawValue, fieldName, false) : $"{fieldName} eq '{EscapeODataString(rawValue)}'",
            "ne"            => isMulti ? BuildPhraseQuery(rawValue, fieldName, true) : $"{fieldName} ne '{EscapeODataString(rawValue)}'",
            "contains"      => BuildRegexQuery(rawValue, fieldName, RegexMatchMode.Contains, false),
            "notContains"   => BuildRegexQuery(rawValue, fieldName, RegexMatchMode.Contains, true),
            "startsWith"    => BuildRegexQuery(rawValue, fieldName, RegexMatchMode.StartsWith, false),
            "notStartsWith" => BuildRegexQuery(rawValue, fieldName, RegexMatchMode.StartsWith, true),
            "endsWith"      => BuildRegexQuery(rawValue, fieldName, RegexMatchMode.EndsWith, false),
            "notEndsWith"   => BuildRegexQuery(rawValue, fieldName, RegexMatchMode.EndsWith, true),
            "wildcard"      => BuildWildcardQuery(rawValue, fieldName, false),
            "notWildcard"   => BuildWildcardQuery(rawValue, fieldName, true),
            "includes"      => BuildPhraseQuery(rawValue, fieldName, false),
            "notIncludes"   => BuildPhraseQuery(rawValue, fieldName, true),
            "in"            => BuildInStringClause(fieldName, rawValue, isMulti, true),
            "notIn"         => BuildInStringClause(fieldName, rawValue, isMulti, false),
            "gt"            => $"{fieldName} gt '{EscapeODataString(rawValue)}'",
            "gte"           => $"{fieldName} ge '{EscapeODataString(rawValue)}'",
            "lt"            => $"{fieldName} lt '{EscapeODataString(rawValue)}'",
            "lte"           => $"{fieldName} le '{EscapeODataString(rawValue)}'",
            _               => null
        };
    }

    private static string BuildRegexQuery(string? value, string fieldName, RegexMatchMode mode, bool isNot)
    {
        if (string.IsNullOrWhiteSpace(value)) return isNot ? "1 eq 1" : "1 eq 0";
        var words = value.Trim().ToLowerInvariant().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var parts = new List<string>();
        foreach (var w in words)
        {
            var escaped = EscapeRegex(w);
            var regex = mode switch
            {
                RegexMatchMode.StartsWith => $"/{escaped}.*/",
                RegexMatchMode.EndsWith => $"/.*{escaped}/",
                _ => $"/.*{escaped}.*/"
            };
            parts.Add($"+{regex}");
        }
        var query = string.Join(" ", parts).Replace("'", "''");
        var ismatch = $"search.ismatch('{query}', '{fieldName}')";
        return isNot ? $"not {ismatch}" : ismatch;
    }

    private static string BuildWildcardQuery(string? value, string fieldName, bool isNot)
    {
        if (string.IsNullOrWhiteSpace(value)) return isNot ? "1 eq 1" : "1 eq 0";
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            var c = value[i];
            if (c == '\\' && i + 1 < value.Length && value[i + 1] is '*' or '?')
            {
                sb.Append(EscapeRegex(value[++i].ToString()));
                continue;
            }
            sb.Append(c switch
            {
                '*' => ".*",
                '?' => ".",
                '/' => "\\/",
                _ => System.Text.RegularExpressions.Regex.Escape(c.ToString())
            });
        }
        var pattern = sb.ToString().Replace("'", "''");
        var ismatch = $"search.ismatch('/{pattern}/', '{fieldName}')";
        return isNot ? $"not {ismatch}" : ismatch;
    }

    private static string BuildPhraseQuery(string? value, string fieldName, bool isNot)
    {
        if (string.IsNullOrWhiteSpace(value)) return isNot ? "1 eq 1" : "1 eq 0";
        var escaped = EscapeRegex(value.ToLowerInvariant()).Replace("'", "''");
        var ismatch = $"search.ismatch('\"{escaped}\"', '{fieldName}')";
        return isNot ? $"not {ismatch}" : ismatch;
    }

    private static string BuildInStringClause(string fieldName, string? value, bool isMulti, bool isIn)
    {
        var values = ParseValueList(value);
        if (values.Count == 0) return isIn ? "id eq '00000000-0000-0000-0000-000000000000'" : "id ne '00000000-0000-0000-0000-000000000000'";

        var parts = values.Select(v =>
        {
            if (isMulti)
            {
                return BuildPhraseQuery(v, fieldName, false);
            }
            return $"{fieldName} eq '{EscapeODataString(v)}'";
        });

        var logic = isIn ? " or " : " and ";
        var op = isIn ? "" : "not ";

        return $"{op}({string.Join(logic, parts)})";
    }

    private static string EscapeODataString(string? val) =>
        (val ?? string.Empty).Replace("'", "''");

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
