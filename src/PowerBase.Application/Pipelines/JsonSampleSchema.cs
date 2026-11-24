using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace PowerBase.Application.Pipelines;

/// <summary>One field Quickbase's "Iterate over JSON records" builds from the JSON Schema Sample.</summary>
public sealed class JsonSchemaField
{
    /// <summary>The exported name (a normalized key: <c>firstName</c> → <c>first_name</c>).</summary>
    public string Key { get; init; } = "";
    /// <summary>The key as the sample (and every record) writes it.</summary>
    public string SourceKey { get; init; } = "";
    /// <summary>string / integer / number / boolean / dict / list.</summary>
    public string Type { get; init; } = "string";
    public string Title { get; init; } = "";
    /// <summary>A dict's own fields; null when the dict has no schema (Quickbase types an empty list sample <c>[]</c> as such a dict).</summary>
    public List<JsonSchemaField>? Children { get; init; }
    /// <summary>A list's item schema.</summary>
    public JsonSchemaField? Item { get; init; }
}

/// <summary>
/// Quickbase's typing of a JSON record, measured on a live realm (2026-11-20):
/// - schema from the sample: null → string, int → integer, float (1.0 too) → number, bool → boolean, object → dict, list → list
///   typed by its first item, empty list → dict without schema, a list of lists is refused;
/// - keys are normalized (camelCase / non-alphanumerics → snake_case, ASCII-folded, leading digit → "n", empty → "field",
///   "type" is reserved, a name already taken gets _0, _1 …; the first key named raw_record replaces the built-in Raw record);
/// - titles: scalars title-case the key's words ("My Str"), dicts and lists capitalize the normalized name ("My dict");
/// - a record's values are converted the Python way: str(), int(), float(), bool(); "" becomes null, a null value is left out,
///   a value that cannot be converted becomes null; extra keys are dropped; a dict / list value is converted item by item.
/// </summary>
public static class JsonSampleSchema
{
    public const string RawRecord = "raw_record";

    public static List<JsonSchemaField> Build(JsonObject sample) => BuildFields(sample, topLevel: true);

    private static List<JsonSchemaField> BuildFields(JsonObject sample, bool topLevel)
    {
        var fields = new List<JsonSchemaField>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        var rawRecordTaken = false;
        foreach (var (sourceKey, value) in sample)
        {
            var name = NormalizeKey(sourceKey);
            if (topLevel && name == RawRecord && !rawRecordTaken)
            {
                rawRecordTaken = true;
                used.Add(name);
            }
            else
            {
                var candidate = name;
                for (var i = 0; candidate == "type" || used.Contains(candidate) || (topLevel && candidate == RawRecord && !rawRecordTaken); i++)
                    candidate = name + "_" + i.ToString(CultureInfo.InvariantCulture);
                name = candidate;
                used.Add(name);
            }
            fields.Add(FieldOf(sourceKey, name, value));
        }
        return fields;
    }

    private static JsonSchemaField FieldOf(string sourceKey, string name, JsonNode? value)
    {
        switch (value)
        {
            case JsonObject obj:
                return new JsonSchemaField { Key = name, SourceKey = sourceKey, Type = "dict", Title = ContainerTitle(name), Children = BuildFields(obj, false) };
            case JsonArray { Count: 0 }:
                return new JsonSchemaField { Key = name, SourceKey = sourceKey, Type = "dict", Title = ContainerTitle(name) };
            case JsonArray array:
                var title = ContainerTitle(name);
                var first = array[0];
                if (first is JsonArray) throw new InvalidOperationException("Internal error. Please contact support to investigate");
                var item = first is JsonObject itemObject
                    ? new JsonSchemaField { Key = name, SourceKey = sourceKey, Type = "dict", Title = title, Children = BuildFields(itemObject, false) }
                    : new JsonSchemaField { Key = name, SourceKey = sourceKey, Type = ScalarType(first), Title = title };
                return new JsonSchemaField { Key = name, SourceKey = sourceKey, Type = "list", Title = title, Item = item };
            default:
                return new JsonSchemaField { Key = name, SourceKey = sourceKey, Type = ScalarType(value), Title = ScalarTitle(sourceKey) };
        }
    }

    private static string ScalarType(JsonNode? value)
    {
        if (value is not JsonValue v) return "string";
        return v.GetValueKind() switch
        {
            JsonValueKind.True or JsonValueKind.False => "boolean",
            JsonValueKind.Number => IsIntegerToken(v.ToJsonString()) ? "integer" : "number",
            _ => "string"
        };
    }

    private static bool IsIntegerToken(string token) => token.IndexOfAny(new[] { '.', 'e', 'E' }) < 0;

    // ---- names -----------------------------------------------------------------------------------------------------------

    /// <summary>inflection.underscore: "HTTPServer" → "http_server", "firstName" → "first_name", "a-b" → "a_b".</summary>
    public static string Underscore(string key)
    {
        var word = Regex.Replace(key, "([A-Z]+)([A-Z][a-z])", "$1_$2");
        word = Regex.Replace(word, "([a-z\\d])([A-Z])", "$1_$2");
        return word.Replace('-', '_').ToLowerInvariant();
    }

    public static string NormalizeKey(string key)
    {
        var folded = FoldToAscii(Underscore(key)).ToLowerInvariant();
        var name = Regex.Replace(folded, "[^a-z0-9]+", "_").Trim('_');
        if (name.Length == 0) return "field";
        return char.IsDigit(name[0]) ? "n" + name : name;
    }

    /// <summary>Python's str.title() of the underscored key with "_" read as a space ("my_str" → "My Str", "_x" → " X").</summary>
    public static string ScalarTitle(string key) => PyTitle(Underscore(key).Replace('_', ' '));

    /// <summary>inflection.humanize of the normalized name ("my_dict" → "My dict").</summary>
    public static string ContainerTitle(string name)
    {
        var text = name.Replace('_', ' ');
        return text.Length == 0 ? text : char.ToUpperInvariant(text[0]) + text[1..];
    }

    private static string PyTitle(string text)
    {
        var sb = new StringBuilder(text.Length);
        var previousCased = false;
        foreach (var c in text)
        {
            var isLetter = char.IsLetter(c);
            if (isLetter && !previousCased && c == 'ß') sb.Append("Ss");
            else sb.Append(isLetter ? (previousCased ? char.ToLowerInvariant(c) : char.ToUpperInvariant(c)) : c);
            previousCased = isLetter;
        }
        return sb.ToString();
    }

    private static readonly Dictionary<char, string> Specials = new()
    {
        ['ß'] = "ss", ['æ'] = "ae", ['Æ'] = "AE", ['ø'] = "o", ['Ø'] = "O", ['œ'] = "oe", ['Œ'] = "OE", ['đ'] = "d", ['Đ'] = "D",
        ['ł'] = "l", ['Ł'] = "L", ['þ'] = "th", ['Þ'] = "Th", ['ð'] = "d", ['Ð'] = "D", ['ı'] = "i", ['ħ'] = "h", ['Ħ'] = "H",
    };

    /// <summary>Unidecode for Latin text: accents dropped (ü → u), ligatures spelled out (ß → ss); other scripts become separators.</summary>
    private static string FoldToAscii(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (c < 128) sb.Append(c);
            else if (Specials.TryGetValue(c, out var s)) sb.Append(s);
            else if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark) sb.Append(' ');
        }
        return sb.ToString();
    }

    // ---- records ---------------------------------------------------------------------------------------------------------

    /// <summary>One record as Quickbase exports it: Raw record first, then the sample's fields the record has.</summary>
    public static JsonObject Project(JsonNode? record, List<JsonSchemaField>? fields)
    {
        var result = new JsonObject { [RawRecord] = record?.DeepClone() };
        if (fields == null || record is not JsonObject obj) return result;
        foreach (var field in fields)
        {
            if (!obj.TryGetPropertyValue(field.SourceKey, out var value) || value == null) continue;
            result[field.Key] = Convert(value, field);
        }
        return result;
    }

    private static JsonObject ProjectChildren(JsonObject value, List<JsonSchemaField> fields)
    {
        var result = new JsonObject();
        foreach (var field in fields)
        {
            if (!value.TryGetPropertyValue(field.SourceKey, out var nested) || nested == null) continue;
            result[field.Key] = Convert(nested, field);
        }
        return result;
    }

    public static JsonNode? Convert(JsonNode? value, JsonSchemaField field)
    {
        if (value == null) return null;
        if (value is JsonValue sv && sv.GetValueKind() == JsonValueKind.String && sv.GetValue<string>().Length == 0) return null;
        return ConvertValue(value, field);
    }

    /// <summary>A list's items are converted as they are: "" stays "", null becomes "None" for a text list.</summary>
    private static JsonNode? ConvertItem(JsonNode? value, JsonSchemaField field) => field.Type switch
    {
        "dict" => value is JsonObject ? Convert(value, field) : value?.DeepClone(),
        _ => ConvertValue(value, field)
    };

    private static JsonNode? ConvertValue(JsonNode? value, JsonSchemaField field)
    {
        switch (field.Type)
        {
            case "string": return JsonValue.Create(PyStr(value));
            case "integer": return PyInt(value);
            case "number": return PyFloat(value);
            case "boolean": return value == null ? null : JsonValue.Create(PyBool(value));
            case "dict":
                if (value is JsonObject obj && field.Children != null) return ProjectChildren(obj, field.Children);
                return value.DeepClone();
            case "list":
                if (value is JsonArray array && field.Item != null)
                    return new JsonArray(array.Select(item => ConvertItem(item, field.Item)).ToArray());
                return value.DeepClone();
            default: return value.DeepClone();
        }
    }

    // ---- Python conversions ------------------------------------------------------------------------------------------------

    public static bool PyBool(JsonNode? value) => value switch
    {
        null => false,
        JsonObject o => o.Count > 0,
        JsonArray a => a.Count > 0,
        JsonValue v => v.GetValueKind() switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String => v.GetValue<string>().Length > 0,
            JsonValueKind.Number => NumberIsNonZero(v.ToJsonString()),
            _ => false
        },
        _ => false
    };

    private static bool NumberIsNonZero(string token) =>
        !double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || d != 0;

    private static readonly Regex PyIntText = new(@"^[+-]?\d+(?:_\d+)*$", RegexOptions.CultureInvariant);
    private static readonly Regex PyFloatText = new(@"^[+-]?(?:(?:\d+(?:_\d+)*)?\.?\d+(?:_\d+)*|\d+(?:_\d+)*\.)(?:[eE][+-]?\d+(?:_\d+)*)?$", RegexOptions.CultureInvariant);

    public static JsonNode? PyInt(JsonNode? value)
    {
        if (value is not JsonValue v) return null;
        switch (v.GetValueKind())
        {
            case JsonValueKind.True: return JsonValue.Create(1);
            case JsonValueKind.False: return JsonValue.Create(0);
            case JsonValueKind.Number:
            {
                var token = v.ToJsonString();
                if (IsIntegerToken(token)) return JsonNode.Parse(token);
                if (!double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) || double.IsNaN(d) || double.IsInfinity(d)) return null;
                return JsonNode.Parse(new BigInteger(Math.Truncate(d)).ToString(CultureInfo.InvariantCulture));
            }
            case JsonValueKind.String:
            {
                var text = ToAsciiDigits(v.GetValue<string>().Trim());
                if (!PyIntText.IsMatch(text)) return null;
                var parsed = BigInteger.Parse(text.Replace("_", ""), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture);
                return JsonNode.Parse(parsed.ToString(CultureInfo.InvariantCulture));
            }
            default: return null;
        }
    }

    public static JsonNode? PyFloat(JsonNode? value)
    {
        if (value is not JsonValue v) return null;
        double d;
        switch (v.GetValueKind())
        {
            case JsonValueKind.True: d = 1; break;
            case JsonValueKind.False: d = 0; break;
            case JsonValueKind.Number:
                if (!double.TryParse(v.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return null;
                break;
            case JsonValueKind.String:
            {
                var text = ToAsciiDigits(v.GetValue<string>().Trim());
                var lower = text.ToLowerInvariant().TrimStart('+');
                var negative = lower.StartsWith('-');
                if (negative) lower = lower[1..];
                if (lower is "nan") return JsonValue.Create("nan");
                if (lower is "inf" or "infinity") return JsonValue.Create(negative ? "-inf" : "inf");
                if (!PyFloatText.IsMatch(text)) return null;
                if (!double.TryParse(text.Replace("_", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out d)) return null;
                break;
            }
            default: return null;
        }
        if (double.IsInfinity(d)) return JsonValue.Create(d > 0 ? "inf" : "-inf");
        return JsonValue.Create(d);
    }

    private static string ToAsciiDigits(string text)
    {
        if (text.All(c => c < 128)) return text;
        var sb = new StringBuilder(text.Length);
        foreach (var c in text)
            sb.Append(char.IsDigit(c) && c >= 128 ? (char)('0' + (int)char.GetNumericValue(c)) : c);
        return sb.ToString();
    }

    /// <summary>Python's str() of a parsed JSON value: True / False, 1.5, "[1, 'a']", "{'a': 1}".</summary>
    public static string PyStr(JsonNode? value) => value is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() : PyRepr(value);

    public static string PyRepr(JsonNode? value)
    {
        switch (value)
        {
            case null: return "None";
            case JsonArray array: return "[" + string.Join(", ", array.Select(PyRepr)) + "]";
            case JsonObject obj: return "{" + string.Join(", ", obj.Select(p => PyStringRepr(p.Key) + ": " + PyRepr(p.Value))) + "}";
            case JsonValue v:
                return v.GetValueKind() switch
                {
                    JsonValueKind.True => "True",
                    JsonValueKind.False => "False",
                    JsonValueKind.String => PyStringRepr(v.GetValue<string>()),
                    JsonValueKind.Number => PyNumber(v.ToJsonString()),
                    _ => "None"
                };
            default: return value.ToJsonString();
        }
    }

    private static string PyNumber(string token)
    {
        if (IsIntegerToken(token)) return token.TrimStart('+');
        return double.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? PyFloatRepr(d) : token;
    }

    /// <summary>Python's repr(float): the shortest round-trip digits, positional between 1e-4 and 1e16, else "1e+16" / "1e-05".</summary>
    public static string PyFloatRepr(double d)
    {
        if (double.IsNaN(d)) return "nan";
        if (double.IsInfinity(d)) return d > 0 ? "inf" : "-inf";
        if (d == 0) return 1 / d < 0 ? "-0.0" : "0.0";
        // Shortest round-trip mantissa digits and decimal exponent.
        var shortest = d.ToString("R", CultureInfo.InvariantCulture);
        var negative = shortest.StartsWith('-');
        if (negative) shortest = shortest[1..];
        string digits; int exponent;
        var e = shortest.IndexOfAny(new[] { 'E', 'e' });
        var mantissa = e >= 0 ? shortest[..e] : shortest;
        var exp = e >= 0 ? int.Parse(shortest[(e + 1)..], CultureInfo.InvariantCulture) : 0;
        var dot = mantissa.IndexOf('.');
        var intPart = dot >= 0 ? mantissa[..dot] : mantissa;
        var fracPart = dot >= 0 ? mantissa[(dot + 1)..] : "";
        digits = (intPart + fracPart).TrimStart('0');
        var leadingZeros = (intPart + fracPart).Length - (intPart + fracPart).TrimStart('0').Length;
        exponent = intPart.Length + exp - leadingZeros - 1;
        digits = digits.TrimEnd('0');
        if (digits.Length == 0) digits = "0";
        string body;
        if (exponent < -4 || exponent >= 16)
        {
            var m = digits.Length > 1 ? digits[0] + "." + digits[1..] : digits;
            body = m + "e" + (exponent < 0 ? "-" : "+") + Math.Abs(exponent).ToString("00", CultureInfo.InvariantCulture);
        }
        else if (exponent < 0)
            body = "0." + new string('0', -exponent - 1) + digits;
        else if (digits.Length <= exponent + 1)
            body = digits + new string('0', exponent + 1 - digits.Length) + ".0";
        else
            body = digits[..(exponent + 1)] + "." + digits[(exponent + 1)..];
        return (negative ? "-" : "") + body;
    }

    private static string PyStringRepr(string s)
    {
        var quote = s.Contains('\'') && !s.Contains('"') ? '"' : '\'';
        var sb = new StringBuilder(s.Length + 2).Append(quote);
        foreach (var c in s)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c == quote) sb.Append('\\').Append(c);
                    else if (c < 0x20 || c == 0x7f) sb.Append("\\x").Append(((int)c).ToString("x2", CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        return sb.Append(quote).ToString();
    }
}
