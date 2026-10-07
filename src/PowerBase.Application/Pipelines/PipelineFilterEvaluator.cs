using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

public class TriggerFilterRule
{
    public string? Field { get; set; }
    public string? Operator { get; set; }
    // Angular number inputs emit JSON numbers while text/date/select controls emit strings.
    // Accept every scalar representation so one numeric rule cannot make the entire Search
    // Records configuration fail deserialization.
    [JsonConverter(typeof(FilterScalarStringJsonConverter))]
    public string? Value { get; set; }
    public string? Type { get; set; } // "rule" or "nested"
    public List<TriggerFilterGroup>? Groups { get; set; }
}

public sealed class FilterScalarStringJsonConverter : JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType switch
        {
            JsonTokenType.String => reader.GetString(),
            JsonTokenType.Number => ReadNumber(ref reader),
            JsonTokenType.True => "true",
            JsonTokenType.False => "false",
            JsonTokenType.Null => null,
            _ => throw new JsonException($"Filter values must be scalar; received '{reader.TokenType}'.")
        };

    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options)
    {
        if (value is null) writer.WriteNullValue();
        else writer.WriteStringValue(value);
    }

    private static string ReadNumber(ref Utf8JsonReader reader)
    {
        using var document = JsonDocument.ParseValue(ref reader);
        return document.RootElement.GetRawText();
    }
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
        ["NUMBER"] = new[] { "equals", "=", "is", "not_equals", "not-equals", "<>", "!=", "is_not", "is-not", "greater_than", "greater-than", ">", "is-after", "after", "greater_than_or_equals", "greater-than-or-equal", "greater-than-or-equals", ">=", "is-on-or-after", "on-or-after", "on_or_after", "less_than", "less-than", "<", "is-before", "before", "less_than_or_equals", "less-than-or-equal", "less-than-or-equals", "<=", "is-on-or-before", "on-or-before", "on_or_before", "is_blank", "is-blank", "is-null", "is-empty", "is_empty", "is_not_blank", "is-not-blank", "is-not-null", "is-not-empty", "is_not_empty" },
        ["DATE"] = new[] { "equals", "=", "is", "not_equals", "not-equals", "<>", "!=", "is_not", "is-not", "greater_than", "greater-than", ">", "is-after", "after", "greater_than_or_equals", "greater-than-or-equal", "greater-than-or-equals", ">=", "is-on-or-after", "on-or-after", "on_or_after", "less_than", "less-than", "<", "is-before", "before", "less_than_or_equals", "less-than-or-equal", "less-than-or-equals", "<=", "is-on-or-before", "on-or-before", "on_or_before", "is_blank", "is-blank", "is-null", "is-empty", "is_empty", "is_not_blank", "is-not-blank", "is-not-null", "is-not-empty", "is_not_empty" },
        ["BOOLEAN"] = new[] { "equals", "=", "is", "not_equals", "not-equals", "<>", "!=", "is_not", "is-not", "is_true", "is-true", "is_false", "is-false", "is_blank", "is-blank", "is-null", "is-empty", "is_empty", "is_not_blank", "is-not-blank", "is-not-null", "is-not-empty", "is_not_empty" },
        ["TEXT"] = new[] { "equals", "=", "is", "not_equals", "not-equals", "<>", "!=", "is_not", "is-not", "contains", "not_contains", "not-contains", "starts_with", "starts-with", "not_starts_with", "not-starts-with", "ends_with", "ends-with", "not_ends_with", "not-ends-with", "is_blank", "is-blank", "is-null", "is-empty", "is_empty", "is_not_blank", "is-not-blank", "is-not-null", "is-not-empty", "is_not_empty", "is_true", "is-true", "is_false", "is-false" }
    };

    /// <summary>Type category of a field. Formula fields store no type of their own: the result type
    /// comes from the Formula_{X} variant (or, for the generic Formula code, its ResultType setting),
    /// so Formula_Number / Formula_Date / Formula_DateTime etc. compare as numbers/dates instead of text.</summary>
    public static string GetTypeCategory(AppField field)
    {
        if (PowerBase.Application.Formulas.FormulaTypeMap.IsFormulaComputed(field.TypeCode, field.Settings)
            && PowerBase.Application.Formulas.FormulaTypeMap.ExpressionAndType(field.TypeCode, field.Settings) is { } resolved)
        {
            return resolved.Type switch
            {
                PowerBase.Formula.Types.FormulaType.Number or PowerBase.Formula.Types.FormulaType.Duration => "NUMBER",
                PowerBase.Formula.Types.FormulaType.Date or PowerBase.Formula.Types.FormulaType.DateTime or PowerBase.Formula.Types.FormulaType.Time => "DATE",
                PowerBase.Formula.Types.FormulaType.Bool => "BOOLEAN",
                _ => "TEXT"
            };
        }
        return GetTypeCategory(field.TypeCode);
    }

    public static string GetTypeCategory(string typeCode)
    {
        var code = typeCode?.ToUpperInvariant();
        // Formula_{X} variants carry their result type in the suffix (settings-free fallback).
        if (code == "FORMULA_NUMBER" || code == "FORMULA_DURATION") return "NUMBER";
        if (code == "FORMULA_DATE" || code == "FORMULA_DATETIME" || code == "FORMULA_TIME") return "DATE";
        if (code == "FORMULA_BOOL") return "BOOLEAN";
        if (code == "NUMBER" || code == "CURRENCY" || code == "PERCENT" || code == "RATING" || code == "NUMERIC" || code == "INTEGER" || code == "FLOAT" || code == "NUMERICRANGE" || code == "RECORDID" || code == "DURATION")
            return "NUMBER";
        if (code == "DATE" || code == "DATE_TIME" || code == "DATETIME" || code == "TIMESTAMP" || code == "TIME" || code == "TIME_OF_DAY" || code == "DATERANGE")
            return "DATE";
        if (code == "BOOLEAN" || code == "CHECKBOX")
            return "BOOLEAN";
        return "TEXT";
    }

    public static bool EvaluateConditionOperator(string leftVal, string op, string rightVal, string? typeCategory = null, ILogger? logger = null)
    {
        var left = leftVal ?? string.Empty;
        var right = rightVal ?? string.Empty;
        var normalizedOp = NormalizeOperator(op);
        DateTime? relativeDayEnd = null;
        if (RelativeFilterDate.IsRelative(right) && (typeCategory == "DATE" || string.IsNullOrEmpty(typeCategory) || typeCategory == "INFER"))
        {
            var window = RelativeFilterDate.Window(right);
            right = window.Start.ToString("O");
            relativeDayEnd = window.End;
            typeCategory = "DATE";
        }


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
        switch (typeCategory.ToUpperInvariant())
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

                    // A date-only rule value (the picker never collects a time) marks the exact UTC
                    // instant of the picked calendar day's local midnight (frontend: Date.toISOString()).
                    // The field's own value is a full timestamp (Date Created/Modified are UTC
                    // instants, not calendar dates), so truncating both sides to `.Date` compared
                    // raw UTC calendar days — two records "the same local day" can land on different
                    // UTC calendar days depending on time-of-day, silently excluding them. Comparing
                    // the untruncated left value against the [rDate, rDate+1day) window anchored on
                    // that local-midnight instant instead matches the calendar day the user actually
                    // picked, regardless of which UTC day each record's timestamp happens to fall on.
                    var dayStart = rDate;
                    var dayEnd = relativeDayEnd ?? rDate.AddDays(1);

                    switch (normalizedOp)
                    {
                        case "equals":
                        case "=":
                        case "is":
                            return lDate >= dayStart && lDate < dayEnd;
                        case "not_equals":
                        case "<>":
                        case "!=":
                        case "is_not":
                        case "is-not":
                            return !(lDate >= dayStart && lDate < dayEnd);
                        case "greater_than":
                        case ">":
                        case "is-after":
                        case "after":
                            return lDate >= dayEnd;
                        case "greater_than_or_equals":
                        case ">=":
                        case "is-on-or-after":
                        case "on-or-after":
                            return lDate >= dayStart;
                        case "less_than":
                        case "<":
                        case "is-before":
                        case "before":
                            return lDate < dayStart;
                        case "less_than_or_equals":
                        case "<=":
                        case "is-on-or-before":
                        case "on-or-before":
                            return lDate < dayEnd;
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
                        case "not-starts-with":
                            return !left.StartsWith(right, StringComparison.OrdinalIgnoreCase);
                        case "ends_with":
                        case "ends-with":
                            return left.EndsWith(right, StringComparison.OrdinalIgnoreCase);
                        case "not_ends_with":
                        case "not-ends-with":
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
        if (string.IsNullOrWhiteSpace(op)) return "equals";
        var normalized = op.ToLowerInvariant().Trim().Replace('-', '_');
        return normalized switch
        {
            "is_null" => "is_blank",
            "is_not_null" => "is_not_blank",
            "is_empty" => "is_blank",
            "is_not_empty" => "is_not_blank",
            "not_equals" => "not_equals",
            "does_not_equal" => "not_equals",
            "does_not_contain" => "not_contains",
            "not_contains" => "not_contains",
            "starts_with" => "starts_with",
            "not_starts_with" => "not_starts_with",
            "ends_with" => "ends_with",
            "not_ends_with" => "not_ends_with",
            "greater_than_or_equal" => "greater_than_or_equals",
            "greater_than_or_equals" => "greater_than_or_equals",
            "less_than_or_equal" => "less_than_or_equals",
            "less_than_or_equals" => "less_than_or_equals",
            "on_or_before" => "less_than_or_equals",
            "on_or_after" => "greater_than_or_equals",
            "before" => "less_than",
            "after" => "greater_than",
            _ => normalized
        };
    }

    public static bool TryParseDateTime(string input, out DateTime date)
    {
        return DateTime.TryParse(input, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out date);
    }

    /// <summary>
    /// In-memory evaluator for the Quickbase advanced-query grammar's parsed tree
    /// (<see cref="FilterGroup"/>/<see cref="FilterNode"/>/<see cref="FilterCondition"/>, produced by
    /// <see cref="CopyRecordsDefinition.ParseQuery"/>). Search Records hands this same tree to the SQL
    /// layer (RecordRepository/PipelineRecordSearchService); trigger steps (On New Event) have no SQL
    /// round-trip, so they evaluate it here against the changed record's in-memory field values instead —
    /// same grammar, same operators, two execution paths.
    /// </summary>
    public static bool EvaluateFilterGroup(FilterGroup? group, IReadOnlyDictionary<long, object?> valuesSource, IReadOnlyList<AppField> fields, ILogger? logger = null)
    {
        if (group == null || group.Nodes == null || group.Nodes.Count == 0) return true;

        var isAnd = !string.Equals(group.Logic, "or", StringComparison.OrdinalIgnoreCase);
        foreach (var node in group.Nodes)
        {
            bool nodeResult = node.Condition != null
                ? EvaluateFilterCondition(node.Condition, valuesSource, fields, logger)
                : EvaluateFilterGroup(node.Group, valuesSource, fields, logger);

            if (isAnd && !nodeResult) return false;
            if (!isAnd && nodeResult) return true;
        }

        return isAnd;
    }

    private static bool EvaluateFilterCondition(FilterCondition condition, IReadOnlyDictionary<long, object?> valuesSource, IReadOnlyList<AppField> fields, ILogger? logger)
    {
        var field = fields.FirstOrDefault(f => f.Fid == condition.FieldId);
        if (field == null)
        {
            logger?.LogWarning("Advanced query field ID '{FieldId}' not found in AppFields list.", condition.FieldId);
            return false;
        }

        var leftVal = ReadFieldValue(field, valuesSource);

        string rightVal;
        if (string.Equals(condition.ValueMode, "field", StringComparison.OrdinalIgnoreCase))
        {
            var otherField = condition.ValueFieldId.HasValue ? fields.FirstOrDefault(f => f.Fid == condition.ValueFieldId.Value) : null;
            rightVal = otherField != null ? ReadFieldValue(otherField, valuesSource) : string.Empty;
        }
        else
        {
            rightVal = condition.Value ?? string.Empty;
        }

        var typeCategory = GetTypeCategory(field);

        switch (condition.Operator)
        {
            case "eq": return EvaluateConditionOperator(leftVal, "equals", rightVal, typeCategory, logger);
            case "ne": return EvaluateConditionOperator(leftVal, "not_equals", rightVal, typeCategory, logger);
            case "contains": return EvaluateConditionOperator(leftVal, "contains", rightVal, typeCategory, logger);
            case "notContains": return EvaluateConditionOperator(leftVal, "not_contains", rightVal, typeCategory, logger);
            case "startsWith": return EvaluateConditionOperator(leftVal, "starts_with", rightVal, typeCategory, logger);
            case "notStartsWith": return EvaluateConditionOperator(leftVal, "not_starts_with", rightVal, typeCategory, logger);
            case "gt": return EvaluateConditionOperator(leftVal, "greater_than", rightVal, typeCategory, logger);
            case "gte": return EvaluateConditionOperator(leftVal, "greater_than_or_equals", rightVal, typeCategory, logger);
            case "lt": return EvaluateConditionOperator(leftVal, "less_than", rightVal, typeCategory, logger);
            case "lte": return EvaluateConditionOperator(leftVal, "less_than_or_equals", rightVal, typeCategory, logger);
            case "date_eq": return TryParseDateTime(leftVal, out var dl) && TryParseDateTime(rightVal, out var dr) && dl.Date == dr.Date;
            case "isEmpty": return string.IsNullOrWhiteSpace(leftVal) || leftVal == "[]";
            case "isNotEmpty": return !string.IsNullOrWhiteSpace(leftVal) && leftVal != "[]";
            case "in":
            case "notIn":
                {
                    var found = ParseValueList(rightVal).Any(v => EvaluateConditionOperator(leftVal, "equals", v, typeCategory, logger));
                    return condition.Operator == "in" ? found : !found;
                }
            case "wildcard": return IsWildcardMatch(leftVal, rightVal);
            case "notWildcard": return !IsWildcardMatch(leftVal, rightVal);
            case "includes":
            case "notIncludes":
                {
                    var parts = leftVal.Split(';', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim());
                    var has = parts.Any(p => p.Equals(rightVal, StringComparison.OrdinalIgnoreCase));
                    return condition.Operator == "includes" ? has : !has;
                }
            default:
                logger?.LogWarning("Unsupported advanced query operator '{Operator}' in trigger filter evaluation.", condition.Operator);
                return false;
        }
    }

    /// <summary>An "in" operand: a JSON array (how the UI stores it) or, failing that, comma-separated text.</summary>
    private static IEnumerable<string> ParseValueList(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            var list = System.Text.Json.JsonSerializer.Deserialize<List<System.Text.Json.JsonElement>>(raw);
            if (list is not null)
                return list.Select(e => e.ValueKind == System.Text.Json.JsonValueKind.String ? e.GetString() ?? "" : e.GetRawText()).ToList();
        }
        catch (System.Text.Json.JsonException) { }
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(p => p.Trim()).ToList();
    }

    private static string ReadFieldValue(AppField field, IReadOnlyDictionary<long, object?> valuesSource)
    {
        object? valObj = null;
        if (valuesSource.TryGetValue(field.Id, out var directVal)) valObj = directVal;
        else if (field.Fid.HasValue && valuesSource.TryGetValue(field.Fid.Value, out var fidVal)) valObj = fidVal;
        return valObj?.ToString() ?? string.Empty;
    }

    /// <summary>Quickbase's WC/XWC wildcard grammar: '*' = any run of characters, '?' = any single
    /// character, '\*'/'\?' = the literal character (see CopyRecordsDefinition's query parser, which
    /// preserves that exact escaping when building this string).</summary>
    private static bool IsWildcardMatch(string value, string pattern)
    {
        var sb = new StringBuilder("^");
        for (var i = 0; i < pattern.Length; i++)
        {
            var c = pattern[i];
            if (c == '\\' && i + 1 < pattern.Length && (pattern[i + 1] == '*' || pattern[i + 1] == '?'))
            {
                sb.Append(Regex.Escape(pattern[i + 1].ToString()));
                i++;
            }
            else if (c == '*') sb.Append(".*");
            else if (c == '?') sb.Append('.');
            else sb.Append(Regex.Escape(c.ToString()));
        }
        sb.Append('$');
        return Regex.IsMatch(value, sb.ToString(), RegexOptions.IgnoreCase);
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

        var typeCategory = GetTypeCategory(field);
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
            AddValidatorError(errors, path, $"On New Event filter field '{(!string.IsNullOrWhiteSpace(field.Label) ? field.Label : field.Name)}' requires an operator.");
            return;
        }

        var typeCategory = GetTypeCategory(field);
        var normalizedOp = rule.Operator.ToLowerInvariant().Trim();

        if (AllowedOperatorsByTypeCategory.TryGetValue(typeCategory, out var allowedOps))
        {
            if (!allowedOps.Contains(normalizedOp))
            {
                var allowedListStr = string.Join(", ", allowedOps);
                AddValidatorError(errors, path, $"On New Event filter field '{(!string.IsNullOrWhiteSpace(field.Label) ? field.Label : field.Name)}' does not support operator '{rule.Operator}'. Allowed operators for {typeCategory} type are: [{allowedListStr}].");
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
