using System.Text.Encodings.Web;
using System.Text.Json;

namespace PowerBase.Application.Import.Qbl;

/// <summary>
/// Pure helpers for the stand-in ("placeholder") fields the converter creates for a source field PowerBase cannot create as
/// itself: which plain field type holds its place, and a lossless JSON snapshot of what the source said about it.
/// A placeholder is typed after the source's result type only where that type can be told for certain; everything else
/// is plain Text, which accepts any value.
/// </summary>
public static class QblPlaceholders
{
    public const string TEXT = "Text";

    /// <summary>The plain field type that holds the place of a formula field of the given QBL type.</summary>
    public static string TypeForFormula(string qblType) => qblType switch
    {
        "QB::Field::Numeric::Formula" => "Number",
        "QB::Field::Currency::Formula" => "Currency",
        "QB::Field::Percent::Formula" => "Percent",
        "QB::Field::Date::Formula" => "Date",
        "QB::Field::DateTime::Formula" => "DateTime",
        "QB::Field::Duration::Formula" => "Duration",
        "QB::Field::Checkbox::Formula" => "Boolean",
        "QB::Field::User::Formula" => "User",
        "QB::Field::TimeOfDay::Formula" => "Time",
        "QB::Field::URL::Formula" => "Url",
        "QB::Field::Email::Formula" => "Email",
        "QB::Field::PhoneNumber::Formula" => "Phone",
        _ => TEXT, // Text, RichText, multi-select and multiple-choice results, anything unknown
    };

    /// <summary>The same for a formula whose PBL result type (and optional variant) is already known.</summary>
    public static string TypeForFormula(string? resultType, string? variant, string? qblType = null)
    {
        if (!string.IsNullOrEmpty(qblType) && qblType.EndsWith("::Formula", StringComparison.Ordinal) && TypeForFormula(qblType) != TEXT)
            return TypeForFormula(qblType);

        if (variant is "Url" or "Email" or "Phone")
            return variant;

        return resultType switch
        {
            "Number" => "Number",
            "Date" => "Date",
            "DateTime" => "DateTime",
            "Duration" => "Duration",
            "Bool" => "Boolean",
            "User" => "User",
            "Time" => "Time",
            _ => TEXT,
        };
    }

    /// <summary>The plain field type that holds the place of a summary: a count/total/average is a Number, "any related records"
    /// a Boolean; Min/Max/combined text depend on the summarized field's type, which the converter cannot tell for certain here.</summary>
    public static string TypeForSummaryFunction(string? qblFunction) => qblFunction switch
    {
        "Count" or "DistinctCount" or "Total" or "Average" or "StdDeviation" => "Number",
        "AnyRelatedRecords" => "Boolean",
        _ => TEXT,
    };

    /// <summary>The plain field type for a stored/formula PowerBase type code of a field a lookup or summary reads from, or Text.</summary>
    public static string TypeForExistingField(string? typeCode) => typeCode switch
    {
        "Number" or "Formula_Number" => "Number",
        "Currency" => "Currency",
        "Percent" => "Percent",
        "Date" or "Formula_Date" => "Date",
        "DateTime" or "Formula_DateTime" => "DateTime",
        "Time" or "Formula_Time" => "Time",
        "Duration" or "Formula_Duration" => "Duration",
        "Boolean" or "Formula_Bool" => "Boolean",
        "User" or "Formula_User" => "User",
        "Email" or "Formula_Email" => "Email",
        "Phone" or "Formula_Phone" => "Phone",
        "Url" or "Formula_Url" => "Url",
        _ => TEXT,
    };

    private static readonly JsonSerializerOptions SnapshotOptions = new()
    {
        WriteIndented = false,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>Everything a source field node says, as JSON: its type, number and every property (references as their scopes).</summary>
    public static string SnapshotJson(QblResourceNode node)
    {
        var map = new Dictionary<string, object?>
        {
            ["type"] = node.Type,
            ["id"] = node.SourceId,
            ["properties"] = Plain(node.Properties),
        };
        return JsonSerializer.Serialize(map, SnapshotOptions);
    }

    private static object? Plain(object? value, int depth = 0)
    {
        if (depth > 24) return value?.ToString();
        switch (value)
        {
            case null:
                return null;
            case string or bool or int or long or double or decimal:
                return value;
            case QblRef r:
                return new Dictionary<string, object?> { ["$ref"] = r.Scopes.ToDictionary(k => k.Key, k => (object?)k.Value) };
            case QblBadRef bad:
                return new Dictionary<string, object?> { ["$badRef"] = bad.Message };
            case QblVarRef v:
                return new Dictionary<string, object?> { ["$var"] = v.ParameterName };
            case QblResourceNode n:
                return new Dictionary<string, object?> { ["type"] = n.Type, ["id"] = n.SourceId, ["properties"] = Plain(n.Properties, depth + 1) };
            case System.Collections.IDictionary d:
            {
                var result = new Dictionary<string, object?>();
                foreach (System.Collections.DictionaryEntry e in d)
                    result[e.Key?.ToString() ?? ""] = Plain(e.Value, depth + 1);
                return result;
            }
            case IReadOnlyDictionary<string, object?> rd:
                return rd.ToDictionary(k => k.Key, k => Plain(k.Value, depth + 1));
            case System.Collections.IEnumerable list:
            {
                var result = new List<object?>();
                foreach (var item in list) result.Add(Plain(item, depth + 1));
                return result;
            }
            default:
                return value.ToString();
        }
    }
}
