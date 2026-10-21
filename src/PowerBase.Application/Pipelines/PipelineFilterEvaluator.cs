using System.Globalization;
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
        ["DATE"] = new[] { "equals", "=", "is", "not_equals", "not-equals", "<>", "!=", "is_not", "is-not", "greater_than", "greater-than", ">", "is-after", "after", "greater_than_or_equals", "greater-than-or-equal", "greater-than-or-equals", ">=", "is-on-or-after", "on-or-after", "on_or_after", "less_than", "less-than", "<", "is-before", "before", "less_than_or_equals", "less-than-or-equal", "less-than-or-equals", "<=", "is-on-or-before", "on-or-before", "on_or_before", "is_blank", "is-blank", "is-null", "is-empty", "is_empty", "is_not_blank", "is-not-blank", "is-not-null", "is-not-empty", "is_not_empty", "during", "not-during", "not_during" },
        // A time of day (Time, Formula_Time, a Lookup of a Time): equal / before / after — no "is during", a time is in no period.
        ["TIME"] = new[] { "equals", "=", "is", "not_equals", "not-equals", "<>", "!=", "is_not", "is-not", "greater_than", "greater-than", ">", "is-after", "after", "greater_than_or_equals", "greater-than-or-equal", "greater-than-or-equals", ">=", "is-on-or-after", "on-or-after", "on_or_after", "less_than", "less-than", "<", "is-before", "before", "less_than_or_equals", "less-than-or-equal", "less-than-or-equals", "<=", "is-on-or-before", "on-or-before", "on_or_before", "is_blank", "is-blank", "is-null", "is-empty", "is_empty", "is_not_blank", "is-not-blank", "is-not-null", "is-not-empty", "is_not_empty" },
        ["BOOLEAN"] = new[] { "equals", "=", "is", "not_equals", "not-equals", "<>", "!=", "is_not", "is-not", "is_true", "is-true", "is_false", "is-false", "is_blank", "is-blank", "is-null", "is-empty", "is_empty", "is_not_blank", "is-not-blank", "is-not-null", "is-not-empty", "is_not_empty" },
        ["TEXT"] = new[] { "equals", "=", "is", "not_equals", "not-equals", "<>", "!=", "is_not", "is-not", "contains", "not_contains", "not-contains", "starts_with", "starts-with", "not_starts_with", "not-starts-with", "ends_with", "ends-with", "not_ends_with", "not-ends-with", "is_blank", "is-blank", "is-null", "is-empty", "is_empty", "is_not_blank", "is-not-blank", "is-not-null", "is-not-empty", "is_not_empty", "is_true", "is-true", "is_false", "is-false",
            "wildcard", "not-wildcard", "not_wildcard", "includes", "not-includes", "not_includes", "is-current-user", "is_current_user", "is-not-current-user", "is_not_current_user" }
    };

    /// <summary>Type category of a field. Formula fields store no type of their own: the result type
    /// comes from the Formula_{X} variant (or, for the generic Formula code, its ResultType setting),
    /// so Formula_Number / Formula_Date / Formula_DateTime etc. compare as numbers/dates instead of text.</summary>
    public static string GetTypeCategory(AppField field)
    {
        // Lookup and Summary fields are compute-on-read too: a Lookup presents the type of the parent
        // field it pulls down, a Summary the type of its calculation (Count/Sum/Avg → number,
        // Exists → checkbox, Combined Text → text, Min/Max → the aggregated field's type).
        if (PowerBase.Application.Formulas.FormulaTypeMap.IsFormulaComputed(field.TypeCode, field.Settings)
            || field.TypeCode is "Lookup" or "Summary")
        {
            return PowerBase.Application.Formulas.FormulaTypeMap.FieldType(field.TypeCode, field.Settings) switch
            {
                PowerBase.Formula.Types.FormulaType.Number or PowerBase.Formula.Types.FormulaType.Duration => "NUMBER",
                PowerBase.Formula.Types.FormulaType.Date or PowerBase.Formula.Types.FormulaType.DateTime => "DATE",
                PowerBase.Formula.Types.FormulaType.Time => "TIME",
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
        if (code == "FORMULA_DATE" || code == "FORMULA_DATETIME") return "DATE";
        if (code == "FORMULA_TIME") return "TIME";
        if (code == "FORMULA_BOOL") return "BOOLEAN";
        if (code == "NUMBER" || code == "CURRENCY" || code == "PERCENT" || code == "RATING" || code == "NUMERIC" || code == "INTEGER" || code == "FLOAT" || code == "NUMERICRANGE" || code == "RECORDID" || code == "DURATION")
            return "NUMBER";
        // A time of day is not a date: "is 10:30" compares times, not the day 10:30 falls on.
        if (code == "TIME" || code == "TIME_OF_DAY")
            return "TIME";
        if (code == "DATE" || code == "DATE_TIME" || code == "DATETIME" || code == "TIMESTAMP" || code == "DATERANGE")
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
        // "is during" / "is not during": the value is a whole period, so the record's date is checked against its window.
        if (normalizedOp is "during" or "not_during")
        {
            if (!RelativeFilterDate.IsPeriod(right) || !TryParseDateTime(left, out var dated)) return false; // a blank date is in no period
            var period = RelativeFilterDate.PeriodWindow(right);
            var inside = dated >= period.Start && dated < period.End;
            return normalizedOp == "during" ? inside : !inside;
        }
        // Operators every value type handles alike: a Quickbase wildcard pattern, and a list holding any of the given values.
        switch (normalizedOp)
        {
            case "wildcard": return IsWildcardMatch(left, right);
            case "not_wildcard": return !IsWildcardMatch(left, right);
            case "includes": return ListIncludesAny(left, right);
            case "not_includes": return !ListIncludesAny(left, right);
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

        // 2. Handle null/blank checks first as they apply to all types. A Date / Numeric Range with neither end is blank too.
        if (normalizedOp == "is_blank" || normalizedOp == "is-empty" || normalizedOp == "is_empty")
        {
            return string.IsNullOrWhiteSpace(left) || IsBlankRange(left);
        }
        if (normalizedOp == "is_not_blank" || normalizedOp == "is-not-empty" || normalizedOp == "is_not_empty")
        {
            return !string.IsNullOrWhiteSpace(left) && !IsBlankRange(left);
        }

        // 3. Enforce the specified field type category
        switch (typeCategory.ToUpperInvariant())
        {
            case "NUMBER":
                {
                    if (TryReadRange(left, out var rangeStart, out var rangeEnd))
                        return EvaluateNumberRange(rangeStart, rangeEnd, normalizedOp, right);
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
                    if (TryReadRange(left, out var rangeStart, out var rangeEnd))
                        return EvaluateDateRange(rangeStart, rangeEnd, normalizedOp, right, relativeDayEnd);
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

            case "TIME":
                {
                    // Times of day: "10:30" is 10:30:00, and 23:59 is after 00:15 — never the day either falls on.
                    if (!TryParseTimeOfDay(left, out var lTime) || !TryParseTimeOfDay(right, out var rTime))
                    {
                        if (normalizedOp == "equals" || normalizedOp == "=" || normalizedOp == "is")
                            return left.Equals(right, StringComparison.OrdinalIgnoreCase);
                        if (normalizedOp == "not_equals" || normalizedOp == "<>" || normalizedOp == "!=" || normalizedOp == "is_not")
                            return !left.Equals(right, StringComparison.OrdinalIgnoreCase);
                        return false;
                    }

                    switch (normalizedOp)
                    {
                        case "equals":
                        case "=":
                        case "is":
                            return lTime == rTime;
                        case "not_equals":
                        case "<>":
                        case "!=":
                        case "is_not":
                            return lTime != rTime;
                        case "greater_than":
                        case ">":
                            return lTime > rTime;
                        case "greater_than_or_equals":
                        case ">=":
                            return lTime >= rTime;
                        case "less_than":
                        case "<":
                            return lTime < rTime;
                        case "less_than_or_equals":
                        case "<=":
                            return lTime <= rTime;
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
            // The editor's date/number operators ("is-after", "is-on-or-before", …) arrive hyphenated and
            // were normalized to underscores that no evaluator branch matched.
            "is_before" => "less_than",
            "is_after" => "greater_than",
            "is_on_or_before" => "less_than_or_equals",
            "is_on_or_after" => "greater_than_or_equals",
            _ => normalized
        };
    }

    public static bool TryParseDateTime(string input, out DateTime date)
    {
        return DateTime.TryParse(input, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AdjustToUniversal | System.Globalization.DateTimeStyles.AssumeUniversal, out date);
    }

    /// <summary>A time of day: "10:30", "10:30:00", "10:30:00.5" (how a Time column and a time formula read), or the time a date &amp; time
    /// value holds ("10:30 AM", "2026-10-19T10:30:00"). A number of days ("10", "1.10:30") is not a time of day.</summary>
    public static bool TryParseTimeOfDay(string? input, out TimeSpan time)
    {
        time = default;
        var text = input?.Trim();
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Contains(':') && TimeSpan.TryParse(text, System.Globalization.CultureInfo.InvariantCulture, out time))
            return time >= TimeSpan.Zero && time < TimeSpan.FromDays(1);
        if (DateTime.TryParse(text, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind | System.Globalization.DateTimeStyles.NoCurrentDateDefault, out var dated))
        {
            time = dated.TimeOfDay;
            return true;
        }
        return false;
    }

    // ── Date / Numeric Range ───────────────────────────────────────────────────────────────────────────────────
    // A range holds a start and an end ({"start":…,"end":…}); one with a single end is that one value. A rule compares the whole range
    // with the value asked for — a number, a day ("today"), or another range:
    //   is / is not          the range includes (does not include) the value   start ≤ value and end ≥ value
    //   after / greater than the range starts after it                         start > value
    //   on or after / ≥      the range starts on or after it                   start ≥ value
    //   before / less than   the range ends before it                          end < value
    //   on or before / ≤     the range ends on or before it                    end ≤ value
    // A day is the whole day: "is 14 Oct" holds for a range from 1 Oct to 31 Oct, "is before 14 Oct" for one that ended by 13 Oct.
    // Search Records runs the same checks on the range's two columns (PipelineEngine.MapRangeRule), so both give the same answer.

    /// <summary>The two ends of a Date / Numeric Range value — a <c>{"start":…,"end":…}</c> object (any letter case) — as text; a missing or
    /// blank end is null. False for any other value.</summary>
    public static bool TryReadRange(string? value, out string? start, out string? end)
    {
        start = end = null;
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text) || text[0] != '{') return false;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            var found = false;
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                var isStart = property.Name.Equals("start", StringComparison.OrdinalIgnoreCase);
                if (!isStart && !property.Name.Equals("end", StringComparison.OrdinalIgnoreCase)) continue;
                found = true;
                var part = property.Value.ValueKind switch
                {
                    JsonValueKind.String => property.Value.GetString(),
                    JsonValueKind.Number => property.Value.GetRawText(),
                    _ => null
                };
                if (string.IsNullOrWhiteSpace(part)) part = null;
                if (isStart) start = part; else end = part;
            }
            return found;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>A range value with neither a start nor an end.</summary>
    private static bool IsBlankRange(string value) => TryReadRange(value, out var start, out var end) && start is null && end is null;

    private static bool EvaluateNumberRange(string? startText, string? endText, string normalizedOp, string right)
    {
        var hasStart = decimal.TryParse(startText, System.Globalization.NumberStyles.Number | System.Globalization.NumberStyles.AllowExponent, System.Globalization.CultureInfo.InvariantCulture, out var start);
        var hasEnd = decimal.TryParse(endText, System.Globalization.NumberStyles.Number | System.Globalization.NumberStyles.AllowExponent, System.Globalization.CultureInfo.InvariantCulture, out var end);
        if (!hasStart) start = end;
        if (!hasEnd) end = start;

        decimal low, high;
        if (TryReadRange(right, out var rightStart, out var rightEnd))
        {
            var hasLow = decimal.TryParse(rightStart, System.Globalization.NumberStyles.Number | System.Globalization.NumberStyles.AllowExponent, System.Globalization.CultureInfo.InvariantCulture, out low);
            var hasHigh = decimal.TryParse(rightEnd, System.Globalization.NumberStyles.Number | System.Globalization.NumberStyles.AllowExponent, System.Globalization.CultureInfo.InvariantCulture, out high);
            if (!hasLow && !hasHigh) return IsNegatedEquality(normalizedOp);
            if (!hasLow) low = high;
            if (!hasHigh) high = low;
        }
        else if (decimal.TryParse(right, out low)) high = low;
        else return IsNegatedEquality(normalizedOp);

        // A range without a number is in no order and includes nothing.
        if (!hasStart && !hasEnd) return IsNegatedEquality(normalizedOp);
        return normalizedOp switch
        {
            "equals" or "=" or "is" => start <= low && end >= high,
            "not_equals" or "<>" or "!=" or "is_not" => !(start <= low && end >= high),
            "greater_than" or ">" => start > high,
            "greater_than_or_equals" or ">=" => start >= low,
            "less_than" or "<" => end < low,
            "less_than_or_equals" or "<=" => end <= high,
            _ => false
        };
    }

    private static bool EvaluateDateRange(string? startText, string? endText, string normalizedOp, string right, DateTime? relativeDayEnd)
    {
        var hasStart = TryParseDateTime(startText ?? string.Empty, out var start);
        var hasEnd = TryParseDateTime(endText ?? string.Empty, out var end);
        if (!hasStart) start = end;
        if (!hasEnd) end = start;
        if (!TryDayWindows(right, relativeDayEnd, out var low, out var high) || (!hasStart && !hasEnd))
            return IsNegatedEquality(normalizedOp);
        return normalizedOp switch
        {
            "equals" or "=" or "is" => start < low.End && end >= high.Start,
            "not_equals" or "<>" or "!=" or "is_not" => !(start < low.End && end >= high.Start),
            "greater_than" or ">" => start >= high.End,
            "greater_than_or_equals" or ">=" => start >= low.Start,
            "less_than" or "<" => end < low.Start,
            "less_than_or_equals" or "<=" => end < high.End,
            _ => false
        };
    }

    /// <summary>The days a Date Range rule's value covers, as UTC instants (start included, end not): a picked day (the instant of its
    /// local midnight) or "today" is one day; another range is its first and its last day.</summary>
    public static bool TryDayWindows(string? value, DateTime? relativeDayEnd, out (DateTime Start, DateTime End) low, out (DateTime Start, DateTime End) high)
    {
        low = high = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (RelativeFilterDate.IsRelative(value))
        {
            low = high = RelativeFilterDate.Window(value);
            return true;
        }
        if (TryReadRange(value, out var first, out var last))
        {
            var hasFirst = TryParseDateTime(first ?? string.Empty, out var firstDay);
            var hasLast = TryParseDateTime(last ?? string.Empty, out var lastDay);
            if (!hasFirst && !hasLast) return false;
            if (!hasFirst) firstDay = lastDay;
            if (!hasLast) lastDay = firstDay;
            low = (firstDay, firstDay.AddDays(1));
            high = (lastDay, lastDay.AddDays(1));
            return true;
        }
        if (!TryParseDateTime(value, out var day)) return false;
        low = high = (day, relativeDayEnd ?? day.AddDays(1));
        return true;
    }

    /// <summary>What a rule that cannot be worked out gives: only "is not" holds (as for a date or number that does not parse).</summary>
    private static bool IsNegatedEquality(string normalizedOp) => normalizedOp is "not_equals" or "<>" or "!=" or "is_not";

    // ── File ───────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>A File field, or a Lookup of one: its value is the files' JSON (<c>[{"name":"a.pdf","path":…}]</c>).</summary>
    /// <summary>A Rich Text (a formula returning one, a Lookup of one): its value is HTML from the editor ("&lt;p&gt;hello&lt;/p&gt;").</summary>
    public static bool IsRichTextField(AppField? field) =>
        field != null && (field.TypeCode is "RichText" or "Formula_RichText"
            || (field.TypeCode == "Lookup" && PowerBase.Application.Relationships.SummaryLookupSources.Settings(field)?.SourceTypeCode is "RichText" or "Formula_RichText"));

    private static readonly Regex HtmlTag = new("<[^>]*>", RegexOptions.Compiled);
    private static readonly Regex Spaces = new(@"\s+", RegexOptions.Compiled);

    /// <summary>The text a Rich Text value shows: its tags taken out, its entities decoded and its spacing collapsed — so "is hello",
    /// "starts with hello" and "is empty" read what the user sees, not the editor's "&lt;p&gt;hello&lt;/p&gt;" (or "&lt;p&gt;&lt;br&gt;&lt;/p&gt;").</summary>
    public static string RichTextPlain(string? html)
    {
        if (string.IsNullOrEmpty(html)) return string.Empty;
        if (html.IndexOf('<') < 0 && html.IndexOf('&') < 0) return html.Trim();
        var text = HtmlTag.Replace(html, " ");
        return Spaces.Replace(System.Net.WebUtility.HtmlDecode(text), " ").Trim();
    }

    public static bool IsFileField(AppField? field) =>
        field != null && (field.TypeCode == "File"
            || (field.TypeCode == "Lookup" && PowerBase.Application.Relationships.SummaryLookupSources.Settings(field)?.SourceTypeCode == "File"));

    /// <summary>The names of the files a File value holds: each file's "name" in its JSON (a list of files, or one file); text that is
    /// not JSON (an old value holding a link) is its own name. Read the way the SQL filter reads them (OPENJSON … '$.name').</summary>
    public static List<string> FileNames(string? value)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text)) return [];
        if (text[0] is '[' or '{')
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                var files = doc.RootElement.ValueKind == JsonValueKind.Array ? doc.RootElement.EnumerateArray().ToList() : [doc.RootElement];
                return files
                    .Select(file => file.ValueKind == JsonValueKind.Object && file.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String
                        ? name.GetString() ?? string.Empty : string.Empty)
                    .Where(name => name.Length > 0).ToList();
            }
            catch (JsonException) { /* not JSON: the text itself */ }
        }
        return [text];
    }

    /// <summary>A rule on a File field compares the files' names, as the report filter's "filename is / contains / starts with…" does —
    /// not the stored JSON, which "starts with" and "ends with" could never match. A positive operator holds when any file's name passes
    /// it, a negated one when none does; a record without files has no name. "is blank" and the rest look at the value itself.</summary>
    public static bool EvaluateFileOperator(string fileValue, string op, string right, ILogger? logger = null)
    {
        var (nameOp, negated) = NormalizeOperator(op) switch
        {
            "equals" or "=" or "is" => ("equals", false),
            "not_equals" or "<>" or "!=" or "is_not" => ("equals", true),
            "contains" => ("contains", false),
            "not_contains" => ("contains", true),
            "starts_with" => ("starts_with", false),
            "not_starts_with" => ("starts_with", true),
            "ends_with" => ("ends_with", false),
            "not_ends_with" => ("ends_with", true),
            "wildcard" => ("wildcard", false),
            "not_wildcard" => ("wildcard", true),
            _ => ((string?)null, false)
        };
        if (nameOp is null) return EvaluateConditionOperator(fileValue, op, right, "TEXT", logger);
        var any = FileNames(fileValue).Any(name => EvaluateConditionOperator(name, nameOp, right, "TEXT", logger));
        return negated ? !any : any;
    }

    /// <summary>The filter-editor operator for an advanced query's, for the operators a File rule compares names with.</summary>
    private static string? FileRuleOperator(string advancedOperator) => advancedOperator switch
    {
        "eq" => "equals",
        "ne" => "not_equals",
        "contains" => "contains",
        "notContains" => "not_contains",
        "startsWith" => "starts_with",
        "notStartsWith" => "not_starts_with",
        "endsWith" => "ends_with",
        "notEndsWith" => "not_ends_with",
        "wildcard" => "wildcard",
        "notWildcard" => "not_wildcard",
        _ => null
    };

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
        // A Rich Text is compared by the text it shows, not the editor's HTML.
        if (IsRichTextField(field)) leftVal = RichTextPlain(leftVal);

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

        // A File is matched by its files' names.
        if (IsFileField(field) && FileRuleOperator(condition.Operator) is { } fileOperator)
            return EvaluateFileOperator(leftVal, fileOperator, rightVal, logger);

        // "is" / "is not" on a list (Multi-select, User list) compares the items in any order — as the filter editor's rules do and as
        // Search Records' SQL does — not the stored JSON text, which never equalled "Red" for ["Red"].
        if (condition.Operator is "eq" or "ne" && !string.Equals(condition.ValueMode, "field", StringComparison.OrdinalIgnoreCase) && IsListField(field))
            return SameItems(leftVal, rightVal) == (condition.Operator == "eq");

        // A Reference is matched by its display text; exact-match operators also accept the stored Record ID#.
        if (condition.Operator is "eq" or "ne" or "in" or "notIn" && TryReferenceIdSource(field, valuesSource, out var byIdSource))
        {
            var viaId = EvaluateFilterCondition(condition, byIdSource, fields, logger);
            var viaLabel = EvaluateFilterConditionCore(condition, leftVal, rightVal, typeCategory, logger);
            return condition.Operator is "eq" or "in" ? viaLabel || viaId : viaLabel && viaId;
        }
        return EvaluateFilterConditionCore(condition, leftVal, rightVal, typeCategory, logger);
    }

    /// <summary>When the trigger interceptor replaced a Reference's stored Record ID# with its display
    /// text, the id is kept under the negated Fid. Returns a values source that reads the id instead.</summary>
    private static bool TryReferenceIdSource(AppField field, IReadOnlyDictionary<long, object?> valuesSource, out IReadOnlyDictionary<long, object?> byIdSource)
    {
        byIdSource = valuesSource;
        if (field.TypeCode != "Reference" || !field.Fid.HasValue) return false;
        var fid = (long)field.Fid.Value;
        if (!valuesSource.TryGetValue(-fid, out var rawId) || rawId is null) return false;
        var copy = new Dictionary<long, object?>(valuesSource) { [fid] = rawId };
        copy.Remove(-fid);
        byIdSource = copy;
        return true;
    }

    private static bool EvaluateFilterConditionCore(FilterCondition condition, string leftVal, string rightVal, string typeCategory, ILogger? logger)
    {
        // Contains / starts with / ends with read the value as shown, whatever its type — "5.5" contains "5" — as SQL does.
        if (condition.Operator is "contains" or "notContains" or "startsWith" or "notStartsWith" or "endsWith" or "notEndsWith")
            typeCategory = "TEXT";
        // A blank value is in no order (as in SQL): "is before x" never holds for it, nor anything "before nothing".
        if (condition.Operator is "gt" or "gte" or "lt" or "lte" && (string.IsNullOrWhiteSpace(leftVal) || string.IsNullOrEmpty(rightVal)))
            return false;
        // HAS / XHAS: the list holds any of the values (it was missing here, so an Advanced Query's HAS never matched in a trigger).
        if (condition.Operator is "includes") return ListIncludesAny(leftVal, rightVal);
        if (condition.Operator is "notIncludes") return !ListIncludesAny(leftVal, rightVal);
        // Text in order: case-insensitive and culture-aware, close to SQL Server's collation ("a_b" comes before "apple").
        if (typeCategory == "TEXT" && condition.Operator is "gt" or "gte" or "lt" or "lte")
        {
            var order = string.Compare(leftVal, rightVal, CultureInfo.InvariantCulture, CompareOptions.IgnoreCase);
            return condition.Operator switch { "gt" => order > 0, "gte" => order >= 0, "lt" => order < 0, _ => order <= 0 };
        }
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

    /// <summary>A field whose value is a list of items: a Multi-select, a User list, or a formula / lookup returning one.</summary>
    internal static bool IsListField(AppField field) =>
        field.TypeCode is "MultiSelect" or "MultiUser" or "Formula_MultiSelect" or "Formula_ListUser"
        || (field.TypeCode == "Lookup" && PowerBase.Application.Relationships.SummaryLookupSources.Settings(field)?.SourceTypeCode is "MultiSelect" or "MultiUser" or "Formula_MultiSelect" or "Formula_ListUser");

    /// <summary>"is empty" / "is not empty" on a list value: a list with no items (an empty JSON array, as a formula returning nothing
    /// reads) is empty, as in Search Records. Null when the operator is not a blank check.</summary>
    public static bool? EvaluateListBlank(string value, string op) => NormalizeOperator(op) switch
    {
        "is_blank" => ListItems(value).Count == 0,
        "is_not_blank" => ListItems(value).Count > 0,
        _ => null
    };

    /// <summary>The items of a list value: a JSON array, or text separated by semicolons or commas.</summary>
    private static HashSet<string> ListItems(string value)
    {
        var trimmed = (value ?? string.Empty).Trim();
        var items = trimmed.StartsWith('[') ? ParseValueList(trimmed)
            : trimmed.Split(trimmed.Contains(';') ? ';' : ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return items.Select(v => v.Trim().Trim('"')).Where(v => v.Length > 0).ToHashSet(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Whether two list values hold the same items, in any order.</summary>
    internal static bool SameItems(string left, string right) => ListItems(left).SetEquals(ListItems(right));

    /// <summary>"includes": a list value (a Multi-select's choices, a User List's user ids — a JSON array, or text separated by
    /// semicolons or commas) holds at least one of the comma-separated values asked for.</summary>
    private static bool ListIncludesAny(string listValue, string wanted)
    {
        var asked = wanted.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(v => v.Trim('"')).Where(v => v.Length > 0).ToList();
        if (asked.Count == 0 || string.IsNullOrWhiteSpace(listValue)) return false;
        var trimmed = listValue.Trim();
        var held = trimmed.StartsWith('[')
            ? ParseValueList(trimmed)
            : trimmed.Split(trimmed.Contains(';') ? ';' : ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var heldSet = held.Select(v => v.Trim().Trim('"')).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // The whole value first: a single choice may itself hold a comma.
        return heldSet.Contains(wanted.Trim()) || asked.Any(heldSet.Contains);
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

    /// <summary>The field a trigger rule names ("fid_9", or its name). "fid_N" is a FID: it is matched against FIDs first, and only
    /// then against an internal id — the two number spaces coincide, and a single combined match picked whichever field came first
    /// (a rule on "fid_9" could end up reading the field whose internal id is 9).</summary>
    private static AppField? ResolveRuleField(IEnumerable<AppField> fields, string fieldRef)
    {
        var list = fields as IReadOnlyCollection<AppField> ?? fields.ToList();
        return list.FirstOrDefault(f => $"fid_{f.Fid}".Equals(fieldRef, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(f => f.Name.Equals(fieldRef, StringComparison.OrdinalIgnoreCase))
            ?? list.FirstOrDefault(f => $"fid_{f.Id}".Equals(fieldRef, StringComparison.OrdinalIgnoreCase));
    }

    private static string ReadFieldValue(AppField field, IReadOnlyDictionary<long, object?> valuesSource)
    {
        object? valObj = null;
        // FID first: a change is keyed by FID, or by FID and internal id alike; an internal id can coincide with ANOTHER field's FID.
        if (field.Fid.HasValue && valuesSource.TryGetValue(field.Fid.Value, out var fidVal)) valObj = fidVal;
        else if (valuesSource.TryGetValue(field.Id, out var directVal)) valObj = directVal;
        return ValueText(valObj);
    }

    /// <summary>A record value as the filters read it. A list (a formula returning a list of users or of choices) reads as a JSON array of
    /// its items — "includes", the user checks and the person details read that — rather than as the list's type name.</summary>
    public static string ValueText(object? value) => value switch
    {
        null => string.Empty,
        string text => text,
        System.Collections.IDictionary map => JsonSerializer.Serialize(map),
        System.Collections.IEnumerable list => JsonSerializer.Serialize(list.Cast<object?>().Select(item => item?.ToString() ?? string.Empty)),
        _ => value.ToString() ?? string.Empty
    };

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

    /// <summary>"is the current user" / "is not the current user" (either spelling); <paramref name="negated"/> for the second.</summary>
    public static bool IsCurrentUserOperator(string? op, out bool negated)
    {
        var normalized = (op ?? string.Empty).Trim().ToLowerInvariant().Replace('-', '_');
        negated = normalized == "is_not_current_user";
        return negated || normalized == "is_current_user";
    }

    /// <summary>Whether a User / User List value (its user ids) holds <paramref name="currentUserId"/>. Unknown → nobody is current. A
    /// blank field is never the current user, so "is not the current user" keeps the records nobody is assigned to (as the report does).</summary>
    public static bool MatchesCurrentUser(string? userValue, bool negated, long? currentUserId)
    {
        var holds = currentUserId is > 0 && PipelineUserFieldFilter.ParseUserIds(userValue).Contains(currentUserId.Value);
        return negated ? !holds : holds;
    }

    /// <param name="users">The people the record's User / User List fields hold, for rules on a person's detail
    /// ("Record Owner → Email"); see <see cref="PipelineUserFieldFilter"/>. Without it only the Id detail is known.</param>
    /// <param name="currentUserId">Who "the current user" is — for a trigger, the person whose change fired it. Without it no record is
    /// "the current user's".</param>
    public static bool EvaluateRule(TriggerFilterRule rule, IReadOnlyDictionary<long, object?> valuesSource, IReadOnlyList<AppField> fields, ILogger? logger = null,
        IReadOnlyDictionary<long, PipelineUserDetails>? users = null, long? currentUserId = null)
    {
        if (IsRuleCompletelyBlank(rule)) return true;

        if (rule.Type == "nested")
        {
            if (rule.Groups == null || !rule.Groups.Any()) return true;
            return rule.Groups.Any(g => EvaluateGroup(g, valuesSource, fields, logger, users, currentUserId));
        }

        if (string.IsNullOrEmpty(rule.Field)) return true;

        if (IsCurrentUserOperator(rule.Operator, out var notCurrent))
        {
            var currentField = ResolveRuleField(fields, rule.Field);
            return currentField != null && MatchesCurrentUser(ReadFieldValue(currentField, valuesSource), notCurrent, currentUserId);
        }

        // One part of an Address ("Address: City") compares as text.
        if (PipelineAddressFieldFilter.TryResolve(rule.Field, fields, out var addressField, out var addressPart))
        {
            object? address = null;
            if (addressField.Fid.HasValue && valuesSource.TryGetValue(addressField.Fid.Value, out var byFid)) address = byFid;
            else if (valuesSource.TryGetValue(addressField.Id, out var byId)) address = byId;
            return EvaluateConditionOperator(PipelineAddressFieldFilter.ReadPart(address, addressPart), rule.Operator ?? "is", rule.Value ?? string.Empty, "TEXT", logger);
        }

        if (PipelineUserFieldFilter.TryResolveRule(rule, fields, out var userField, out var detail))
        {
            var ids = PipelineUserFieldFilter.ParseUserIds(ReadFieldValue(userField, valuesSource));
            return PipelineUserFieldFilter.Matches(ids, users, detail, rule.Operator, rule.Value);
        }

        var field = ResolveRuleField(fields, rule.Field);

        if (field == null)
        {
            logger?.LogWarning("Trigger filter field '{FieldName}' not found in AppFields list.", rule.Field);
            return false;
        }

        object? valObj = null;
        // FID first (see ReadFieldValue): an internal id can coincide with another field's FID.
        if (field.Fid.HasValue && valuesSource.TryGetValue(field.Fid.Value, out var fidVal))
        {
            valObj = fidVal;
        }
        else if (valuesSource.TryGetValue(field.Id, out var directVal))
        {
            valObj = directVal;
        }

        var leftVal = ValueText(valObj);
        // A Rich Text is compared by the text it shows, not the editor's HTML.
        if (IsRichTextField(field)) leftVal = RichTextPlain(leftVal);
        var rightVal = rule.Value ?? string.Empty;
        var op = rule.Operator ?? "is";

        var typeCategory = GetTypeCategory(field);
        // A File (or a Lookup of one) is matched by its files' names, not the stored JSON.
        if (IsFileField(field)) return EvaluateFileOperator(leftVal, op, rightVal, logger);
        // "is" / "is not" on a list (Multi-select, User list, a formula or lookup returning one) compares the items, in any order —
        // as Search Records and the report filter do — not the stored JSON text.
        var normalizedEquality = NormalizeOperator(op);
        if (IsListField(field) && normalizedEquality is "equals" or "=" or "is" or "not_equals" or "<>" or "!=" or "is_not")
        {
            var same = SameItems(leftVal, rightVal);
            return normalizedEquality is "equals" or "=" or "is" ? same : !same;
        }
        // "is empty" / "is not empty" on a list: a list with no items ("[]") is empty, as in Search Records.
        if (IsListField(field) && EvaluateListBlank(leftVal, op) is { } listBlank) return listBlank;

        var matched = EvaluateConditionOperator(leftVal, op, rightVal, typeCategory, logger);

        // A Reference is matched by its display text; an exact-match operator also accepts the stored
        // Record ID# (kept under the negated Fid by the trigger interceptor).
        if (TryReferenceIdSource(field, valuesSource, out var byIdSource))
        {
            var normalized = NormalizeOperator(op);
            if (normalized is "equals" or "=" or "is")
                matched = matched || EvaluateRule(rule, byIdSource, fields, logger);
            else if (normalized is "not_equals" or "<>" or "!=" or "is_not")
                matched = matched && EvaluateRule(rule, byIdSource, fields, logger);
        }
        return matched;
    }

    public static bool EvaluateGroup(TriggerFilterGroup group, IReadOnlyDictionary<long, object?> valuesSource, IReadOnlyList<AppField> fields, ILogger? logger = null,
        IReadOnlyDictionary<long, PipelineUserDetails>? users = null, long? currentUserId = null)
    {
        if (group.Rules == null || !group.Rules.Any()) return true;

        var activeRules = group.Rules.Where(r => !IsRuleCompletelyBlank(r)).ToList();
        if (!activeRules.Any()) return true;

        foreach (var rule in activeRules)
        {
            bool ruleResult = EvaluateRule(rule, valuesSource, fields, logger, users, currentUserId);

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

        // A person's detail of a User / User List field ("Record Owner → Email") compares as text.
        if (PipelineUserFieldFilter.TryResolveRule(rule, fields, out var userField, out _))
        {
            var userFieldLabel = !string.IsNullOrWhiteSpace(userField.Label) ? userField.Label : userField.Name;
            if (string.IsNullOrEmpty(rule.Operator))
                AddValidatorError(errors, path, $"On New Event filter field '{userFieldLabel}' requires an operator.");
            else if (!AllowedOperatorsByTypeCategory["TEXT"].Contains(rule.Operator.ToLowerInvariant().Trim()))
                AddValidatorError(errors, path, $"On New Event filter field '{userFieldLabel}' does not support operator '{rule.Operator}'. Allowed operators for TEXT type are: [{string.Join(", ", AllowedOperatorsByTypeCategory["TEXT"])}].");
            return;
        }

        // One part of an Address ("Address: City") compares as text.
        if (PipelineAddressFieldFilter.TryResolve(rule.Field, fields, out var addressField, out _))
        {
            var addressLabel = !string.IsNullOrWhiteSpace(addressField.Label) ? addressField.Label : addressField.Name;
            if (string.IsNullOrEmpty(rule.Operator))
                AddValidatorError(errors, path, $"On New Event filter field '{addressLabel}' requires an operator.");
            else if (!AllowedOperatorsByTypeCategory["TEXT"].Contains(rule.Operator.ToLowerInvariant().Trim()))
                AddValidatorError(errors, path, $"On New Event filter field '{addressLabel}' does not support operator '{rule.Operator}'. Allowed operators for TEXT type are: [{string.Join(", ", AllowedOperatorsByTypeCategory["TEXT"])}].");
            return;
        }

        var field = ResolveRuleField(fields, rule.Field);

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
