using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.Extensions.Logging;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

public class TriggerFilterRule
{
    public string? Field { get; set; }
    public string? Operator { get; set; }
    public string? Value { get; set; }
    public string? Type { get; set; } // "rule" or "nested"
    public List<TriggerFilterGroup>? Groups { get; set; }
}

public class TriggerFilterGroup
{
    public string LogicalOp { get; set; } = "OR";
    public List<TriggerFilterRule>? Rules { get; set; }
}

public static class PipelineFilterEvaluator
{
    public static readonly Dictionary<string, string[]> AllowedOperatorsByTypeCategory = new()
    {
        ["NUMBER"] = new[] { "equals", "=", "is", "not_equals", "<>", "!=", "is_not", "is-not", "greater_than", ">", "is-after", "after", "greater_than_or_equals", ">=", "is-on-or-after", "on-or-after", "less_than", "<", "is-before", "before", "less_than_or_equals", "<=", "is-on-or-before", "on-or-before", "is_blank", "is-empty", "is_empty", "is_not_blank", "is-not-empty", "is_not_empty" },
        ["DATE"] = new[] { "equals", "=", "is", "not_equals", "<>", "!=", "is_not", "is-not", "greater_than", ">", "is-after", "after", "greater_than_or_equals", ">=", "is-on-or-after", "on-or-after", "less_than", "<", "is-before", "before", "less_than_or_equals", "<=", "is-on-or-before", "on-or-before", "is_blank", "is-empty", "is_empty", "is_not_blank", "is-not-empty", "is_not_empty" },
        ["BOOLEAN"] = new[] { "equals", "=", "is", "not_equals", "<>", "!=", "is_not", "is-not", "is_true", "is-true", "is_false", "is-false", "is_blank", "is-empty", "is_empty", "is_not_blank", "is-not-empty", "is_not_empty" },
        ["TEXT"] = new[] { "equals", "=", "is", "not_equals", "<>", "!=", "is_not", "is-not", "contains", "not_contains", "not-contains", "starts_with", "starts-with", "not_starts_with", "ends_with", "ends-with", "not_ends_with", "is_blank", "is-empty", "is_empty", "is_not_blank", "is-not-empty", "is_not_empty", "is_true", "is-true", "is_false", "is-false" }
    };

    public static string GetTypeCategory(string typeCode)
    {
        var code = typeCode?.ToUpperInvariant();
        if (code == "NUMBER" || code == "CURRENCY" || code == "PERCENT" || code == "RATING" || code == "NUMERIC" || code == "INTEGER" || code == "FLOAT" || code == "NUMERICRANGE")
            return "NUMBER";
        if (code == "DATE" || code == "DATETIME" || code == "TIME" || code == "DURATION" || code == "DATERANGE")
            return "DATE";
        if (code == "BOOLEAN")
            return "BOOLEAN";
        return "TEXT";
    }

    public static bool EvaluateConditionOperator(string leftVal, string op, string rightVal, string? typeCategory = null, ILogger? logger = null)
    {
        var left = leftVal ?? string.Empty;
        var right = rightVal ?? string.Empty;
        var normalizedOp = NormalizeOperator(op);

        // 1. If typeCategory is null/empty/INFER, execute the exact original fallback logic
        if (string.IsNullOrEmpty(typeCategory) || typeCategory.Equals("INFER", StringComparison.OrdinalIgnoreCase))
        {
            switch (normalizedOp)
            {
                case "equals":
                case "=":
                    {
                        bool leftIsNum = decimal.TryParse(left, out var lNum);
                        bool rightIsNum = decimal.TryParse(right, out var rNum);
                        if (leftIsNum && rightIsNum)
                        {
                            return lNum == rNum;
                        }

                        bool leftIsDate = TryParseDateTime(left, out var lDate);
                        bool rightIsDate = TryParseDateTime(right, out var rDate);
                        if (leftIsDate && rightIsDate)
                        {
                            return lDate == rDate;
                        }

                        return left.Equals(right, StringComparison.OrdinalIgnoreCase);
                    }
                case "not_equals":
                case "<>":
                case "!=":
                    {
                        bool leftIsNum = decimal.TryParse(left, out var lNum);
                        bool rightIsNum = decimal.TryParse(right, out var rNum);
                        if (leftIsNum && rightIsNum)
                        {
                            return lNum != rNum;
                        }

                        bool leftIsDate = TryParseDateTime(left, out var lDate);
                        bool rightIsDate = TryParseDateTime(right, out var rDate);
                        if (leftIsDate && rightIsDate)
                        {
                            return lDate != rDate;
                        }

                        return !left.Equals(right, StringComparison.OrdinalIgnoreCase);
                    }
                case "contains":
                    return left.Contains(right, StringComparison.OrdinalIgnoreCase);
                case "not_contains":
                    return !left.Contains(right, StringComparison.OrdinalIgnoreCase);
                case "starts_with":
                    return left.StartsWith(right, StringComparison.OrdinalIgnoreCase);
                case "not_starts_with":
                    return !left.StartsWith(right, StringComparison.OrdinalIgnoreCase);
                case "ends_with":
                    return left.EndsWith(right, StringComparison.OrdinalIgnoreCase);
                case "not_ends_with":
                    return !left.EndsWith(right, StringComparison.OrdinalIgnoreCase);
                case "is_blank":
                    return string.IsNullOrWhiteSpace(left);
                case "is_not_blank":
                    return !string.IsNullOrWhiteSpace(left);
                case "is_true":
                    return left.Equals("true", StringComparison.OrdinalIgnoreCase) || left == "1";
                case "is_false":
                    return left.Equals("false", StringComparison.OrdinalIgnoreCase) || left == "0" || string.IsNullOrWhiteSpace(left);
                case "greater_than":
                case ">":
                    {
                        bool leftIsNum = decimal.TryParse(left, out var lNum);
                        bool rightIsNum = decimal.TryParse(right, out var rNum);
                        if (leftIsNum || rightIsNum)
                        {
                            return leftIsNum && rightIsNum && lNum > rNum;
                        }

                        bool leftIsDate = TryParseDateTime(left, out var lDate);
                        bool rightIsDate = TryParseDateTime(right, out var rDate);
                        if (leftIsDate || rightIsDate)
                        {
                            return leftIsDate && rightIsDate && lDate > rDate;
                        }

                        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase) > 0;
                    }
                case "greater_than_or_equals":
                case ">=":
                    {
                        bool leftIsNum = decimal.TryParse(left, out var lNum);
                        bool rightIsNum = decimal.TryParse(right, out var rNum);
                        if (leftIsNum || rightIsNum)
                        {
                            return leftIsNum && rightIsNum && lNum >= rNum;
                        }

                        bool leftIsDate = TryParseDateTime(left, out var lDate);
                        bool rightIsDate = TryParseDateTime(right, out var rDate);
                        if (leftIsDate || rightIsDate)
                        {
                            return leftIsDate && rightIsDate && lDate >= rDate;
                        }

                        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase) >= 0;
                    }
                case "less_than":
                case "<":
                    {
                        bool leftIsNum = decimal.TryParse(left, out var lNum);
                        bool rightIsNum = decimal.TryParse(right, out var rNum);
                        if (leftIsNum || rightIsNum)
                        {
                            return leftIsNum && rightIsNum && lNum < rNum;
                        }

                        bool leftIsDate = TryParseDateTime(left, out var lDate);
                        bool rightIsDate = TryParseDateTime(right, out var rDate);
                        if (leftIsDate || rightIsDate)
                        {
                            return leftIsDate && rightIsDate && lDate < rDate;
                        }

                        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase) < 0;
                    }
                case "less_than_or_equals":
                case "<=":
                    {
                        bool leftIsNum = decimal.TryParse(left, out var lNum);
                        bool rightIsNum = decimal.TryParse(right, out var rNum);
                        if (leftIsNum || rightIsNum)
                        {
                            return leftIsNum && rightIsNum && lNum <= rNum;
                        }

                        bool leftIsDate = TryParseDateTime(left, out var lDate);
                        bool rightIsDate = TryParseDateTime(right, out var rDate);
                        if (leftIsDate || rightIsDate)
                        {
                            return leftIsDate && rightIsDate && lDate <= rDate;
                        }

                        return string.Compare(left, right, StringComparison.OrdinalIgnoreCase) <= 0;
                    }
                default:
                    logger?.LogWarning("Unknown operator '{Operator}' in condition step evaluation.", op);
                    return false;
            }
        }

        // 2. Handle null/blank checks first as they apply to all types
        if (normalizedOp == "is_blank" || normalizedOp == "is-empty" || normalizedOp == "is_empty")
        {
            return string.IsNullOrWhiteSpace(left);
        }
        if (normalizedOp == "is_not_blank" || normalizedOp == "is-not-empty" || normalizedOp == "is_not_empty")
        {
            return !string.IsNullOrWhiteSpace(left);
        }

        // 3. Enforce the specified field type category
        switch (typeCategory)
        {
            case "NUMBER":
                {
                    if (!decimal.TryParse(left, out var lNum) || !decimal.TryParse(right, out var rNum))
                    {
                        if (normalizedOp == "equals" || normalizedOp == "=" || normalizedOp == "is")
                            return left.Equals(right, StringComparison.OrdinalIgnoreCase);
                        if (normalizedOp == "not_equals" || normalizedOp == "<>" || normalizedOp == "!=" || normalizedOp == "is_not" || normalizedOp == "is-not")
                            return !left.Equals(right, StringComparison.OrdinalIgnoreCase);
                        return false;
                    }

                    switch (normalizedOp)
                    {
                        case "equals":
                        case "=":
                        case "is":
                            return lNum == rNum;
                        case "not_equals":
                        case "<>":
                        case "!=":
                        case "is_not":
                        case "is-not":
                            return lNum != rNum;
                        case "greater_than":
                        case ">":
                        case "is-after":
                        case "after":
                            return lNum > rNum;
                        case "greater_than_or_equals":
                        case ">=":
                        case "is-on-or-after":
                        case "on-or-after":
                            return lNum >= rNum;
                        case "less_than":
                        case "<":
                        case "is-before":
                        case "before":
                            return lNum < rNum;
                        case "less_than_or_equals":
                        case "<=":
                        case "is-on-or-before":
                        case "on-or-before":
                            return lNum <= rNum;
                        default:
                            return false;
                    }
                }

            case "DATE":
                {
                    if (!TryParseDateTime(left, out var lDate) || !TryParseDateTime(right, out var rDate))
                    {
                        if (normalizedOp == "equals" || normalizedOp == "=" || normalizedOp == "is")
                            return left.Equals(right, StringComparison.OrdinalIgnoreCase);
                        if (normalizedOp == "not_equals" || normalizedOp == "<>" || normalizedOp == "!=" || normalizedOp == "is_not" || normalizedOp == "is-not")
                            return !left.Equals(right, StringComparison.OrdinalIgnoreCase);
                        return false;
                    }

                    switch (normalizedOp)
                    {
                        case "equals":
                        case "=":
                        case "is":
                            return lDate == rDate;
                        case "not_equals":
                        case "<>":
                        case "!=":
                        case "is_not":
                        case "is-not":
                            return lDate != rDate;
                        case "greater_than":
                        case ">":
                        case "is-after":
                        case "after":
                            return lDate > rDate;
                        case "greater_than_or_equals":
                        case ">=":
                        case "is-on-or-after":
                        case "on-or-after":
                            return lDate >= rDate;
                        case "less_than":
                        case "<":
                        case "is-before":
                        case "before":
                            return lDate < rDate;
                        case "less_than_or_equals":
                        case "<=":
                        case "is-on-or-before":
                        case "on-or-before":
                            return lDate <= rDate;
                        default:
                            return false;
                    }
                }

            case "BOOLEAN":
                {
                    bool lBool = left.Equals("true", StringComparison.OrdinalIgnoreCase) || left == "1";
                    bool rBool = right.Equals("true", StringComparison.OrdinalIgnoreCase) || right == "1";

                    if (normalizedOp == "is_true" || normalizedOp == "is-true")
                        return lBool;
                    if (normalizedOp == "is_false" || normalizedOp == "is-false")
                        return !lBool;

                    switch (normalizedOp)
                    {
                        case "equals":
                        case "=":
                        case "is":
                            return lBool == rBool;
                        case "not_equals":
                        case "<>":
                        case "!=":
                        case "is_not":
                        case "is-not":
                            return lBool != rBool;
                        default:
                            return false;
                    }
                }

            default: // TEXT
                {
                    if (normalizedOp == "is_true" || normalizedOp == "is-true")
                        return left.Equals("true", StringComparison.OrdinalIgnoreCase) || left == "1";
                    if (normalizedOp == "is_false" || normalizedOp == "is-false")
                        return left.Equals("false", StringComparison.OrdinalIgnoreCase) || left == "0" || string.IsNullOrWhiteSpace(left);

                    switch (normalizedOp)
                    {
                        case "equals":
                        case "=":
                        case "is":
                            return left.Equals(right, StringComparison.OrdinalIgnoreCase);
                        case "not_equals":
                        case "<>":
                        case "!=":
                        case "is_not":
                        case "is-not":
                            return !left.Equals(right, StringComparison.OrdinalIgnoreCase);
                        case "contains":
                            return left.Contains(right, StringComparison.OrdinalIgnoreCase);
                        case "not_contains":
                        case "not-contains":
                            return !left.Contains(right, StringComparison.OrdinalIgnoreCase);
                        case "starts_with":
                        case "starts-with":
                            return left.StartsWith(right, StringComparison.OrdinalIgnoreCase);
                        case "not_starts_with":
                            return !left.StartsWith(right, StringComparison.OrdinalIgnoreCase);
                        case "ends_with":
                        case "ends-with":
                            return left.EndsWith(right, StringComparison.OrdinalIgnoreCase);
                        case "not_ends_with":
                            return !left.EndsWith(right, StringComparison.OrdinalIgnoreCase);
                        case "greater_than":
                        case ">":
                            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase) > 0;
                        case "greater_than_or_equals":
                        case ">=":
                            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase) >= 0;
                        case "less_than":
                        case "<":
                            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase) < 0;
                        case "less_than_or_equals":
                        case "<=":
                            return string.Compare(left, right, StringComparison.OrdinalIgnoreCase) <= 0;
                        default:
                            logger?.LogWarning("Unknown operator '{Operator}' in filter step evaluation.", op);
                            return false;
                    }
                }
        }
    }

    /// <summary>
    /// Normalizes operator tokens from the condition-step UI (hyphenated) and filter editor
    /// to the underscore forms used by the evaluator.
    /// </summary>
    private static string NormalizeOperator(string op)
    {
        var normalized = op.ToLowerInvariant().Trim().Replace('-', '_');
        return normalized switch
        {
            "is_null" => "is_blank",
            "is_not_null" => "is_not_blank",
            "is_empty" => "is_blank",
            "is_not_empty" => "is_not_blank",
            _ => normalized
        };
    }

    public static bool TryParseDateTime(string input, out DateTime date)
    {
        return DateTime.TryParse(input, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out date);
    }

    public static bool IsRuleCompletelyBlank(TriggerFilterRule rule)
    {
        if (rule == null) return true;
        if (rule.Type == "nested")
        {
            if (rule.Groups == null || !rule.Groups.Any()) return true;
            return rule.Groups.All(g => IsGroupCompletelyBlank(g));
        }
        return string.IsNullOrWhiteSpace(rule.Field) &&
               (string.IsNullOrWhiteSpace(rule.Operator) || rule.Operator.Equals("is", StringComparison.OrdinalIgnoreCase)) &&
               string.IsNullOrWhiteSpace(rule.Value);
    }

    public static bool IsGroupCompletelyBlank(TriggerFilterGroup group)
    {
        if (group == null || group.Rules == null || !group.Rules.Any()) return true;
        return group.Rules.All(r => IsRuleCompletelyBlank(r));
    }

    public static bool EvaluateRule(TriggerFilterRule rule, IReadOnlyDictionary<long, object?> valuesSource, IReadOnlyList<AppField> fields, ILogger? logger = null)
    {
        if (IsRuleCompletelyBlank(rule)) return true;

        if (rule.Type == "nested")
        {
            if (rule.Groups == null || !rule.Groups.Any()) return true;
            return rule.Groups.Any(g => EvaluateGroup(g, valuesSource, fields, logger));
        }

        if (string.IsNullOrEmpty(rule.Field)) return true;

        var field = fields.FirstOrDefault(f =>
            f.Name.Equals(rule.Field, StringComparison.OrdinalIgnoreCase) ||
            $"fid_{f.Id}".Equals(rule.Field, StringComparison.OrdinalIgnoreCase) ||
            $"fid_{f.Fid}".Equals(rule.Field, StringComparison.OrdinalIgnoreCase));

        if (field == null)
        {
            logger?.LogWarning("Trigger filter field '{FieldName}' not found in AppFields list.", rule.Field);
            return false;
        }

        object? valObj = null;
        if (valuesSource.TryGetValue(field.Id, out var directVal))
        {
            valObj = directVal;
        }
        else if (field.Fid.HasValue && valuesSource.TryGetValue(field.Fid.Value, out var fidVal))
        {
            valObj = fidVal;
        }

        var leftVal = valObj?.ToString() ?? string.Empty;
        var rightVal = rule.Value ?? string.Empty;
        var op = rule.Operator ?? "is";

        var typeCategory = GetTypeCategory(field.TypeCode);
        return EvaluateConditionOperator(leftVal, op, rightVal, typeCategory, logger);
    }

    public static bool EvaluateGroup(TriggerFilterGroup group, IReadOnlyDictionary<long, object?> valuesSource, IReadOnlyList<AppField> fields, ILogger? logger = null)
    {
        if (group.Rules == null || !group.Rules.Any()) return true;

        var activeRules = group.Rules.Where(r => !IsRuleCompletelyBlank(r)).ToList();
        if (!activeRules.Any()) return true;

        foreach (var rule in activeRules)
        {
            bool ruleResult = EvaluateRule(rule, valuesSource, fields, logger);

            if (!ruleResult) return false;
        }

        return true;
    }

    public static void ValidateRule(TriggerFilterRule rule, IReadOnlyList<AppField> fields, Dictionary<string, List<string>> errors, string path)
    {
        if (IsRuleCompletelyBlank(rule)) return;

        if (rule.Type == "nested")
        {
            if (rule.Groups != null)
            {
                for (int i = 0; i < rule.Groups.Count; i++)
                {
                    ValidateGroup(rule.Groups[i], fields, errors, $"{path}.Groups[{i}]");
                }
            }
            return;
        }

        if (string.IsNullOrEmpty(rule.Field))
        {
            AddValidatorError(errors, path, "On New Event filter requires a field selection.");
            return;
        }

        var field = fields.FirstOrDefault(f =>
            f.Name.Equals(rule.Field, StringComparison.OrdinalIgnoreCase) ||
            $"fid_{f.Id}".Equals(rule.Field, StringComparison.OrdinalIgnoreCase) ||
            $"fid_{f.Fid}".Equals(rule.Field, StringComparison.OrdinalIgnoreCase));

        if (field == null)
        {
            AddValidatorError(errors, path, $"On New Event filter field '{rule.Field}' does not exist in the selected Table.");
            return;
        }

        if (string.IsNullOrEmpty(rule.Operator))
        {
            AddValidatorError(errors, path, $"On New Event filter field '{field.Name}' requires an operator.");
            return;
        }

        var typeCategory = GetTypeCategory(field.TypeCode);
        var normalizedOp = rule.Operator.ToLowerInvariant().Trim();

        if (AllowedOperatorsByTypeCategory.TryGetValue(typeCategory, out var allowedOps))
        {
            if (!allowedOps.Contains(normalizedOp))
            {
                var allowedListStr = string.Join(", ", allowedOps);
                AddValidatorError(errors, path, $"On New Event filter field '{field.Name}' does not support operator '{rule.Operator}'. Allowed operators for {typeCategory} type are: [{allowedListStr}].");
            }
        }
    }

    public static void ValidateGroup(TriggerFilterGroup group, IReadOnlyList<AppField> fields, Dictionary<string, List<string>> errors, string path)
    {
        if (group.Rules == null) return;
        for (int i = 0; i < group.Rules.Count; i++)
        {
            var rule = group.Rules[i];
            if (IsRuleCompletelyBlank(rule)) continue;
            ValidateRule(rule, fields, errors, $"{path}.Rules[{i}]");
        }
    }

    private static void AddValidatorError(Dictionary<string, List<string>> errors, string key, string message)
    {
        if (!errors.TryGetValue(key, out var list))
        {
            list = new List<string>();
            errors[key] = list;
        }
        list.Add(message);
    }
}
