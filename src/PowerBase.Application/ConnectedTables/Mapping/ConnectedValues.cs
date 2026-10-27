using System.Collections;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.ConnectedTables.Mapping;

/// <summary>Reads, converts, compares and hashes field values for a sync.</summary>
public static class ConnectedValues
{
    /// <summary>The value of <paramref name="field"/> in a row read through the record repository (built-in fields come from
    /// their own columns, range fields from their two columns, computed fields from <paramref name="computed"/>).</summary>
    public static object? Read(AppField field, IReadOnlyDictionary<string, object?> row, IReadOnlyDictionary<long, object?>? computed)
    {
        if (field.IsSystem && !string.IsNullOrWhiteSpace(field.PhysicalColumnName))
            return row.TryGetValue(field.PhysicalColumnName!, out var system) ? Clean(system) : null;
        if (!field.Fid.HasValue) return null;
        if (PhysicalNaming.IsComputedTypeCode(field.TypeCode))
            return computed is not null && computed.TryGetValue(field.Fid.Value, out var c) ? Clean(c) : null;

        var column = PhysicalNaming.ColumnName(field.Fid.Value);
        var value = row.TryGetValue(column, out var v) ? Clean(v) : null;
        if (!PhysicalNaming.IsRangeTypeCode(field.TypeCode)) return value;
        var end = row.TryGetValue(PhysicalNaming.EndColumnName(field.Fid.Value), out var e) ? Clean(e) : null;
        return value is null && end is null ? null : new Dictionary<string, object?> { ["start"] = value, ["end"] = end };
    }

    /// <summary>The user ids held by a User / List-user value ("12", 12, "[12,34]" or "12;34").</summary>
    public static IEnumerable<long> UserIds(object? value)
    {
        switch (value)
        {
            case null: yield break;
            case long l: yield return l; yield break;
            case int i: yield return i; yield break;
            case decimal d: yield return (long)d; yield break;
        }
        var text = System.Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        foreach (var part in text.Trim().Trim('[', ']').Split([';', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (long.TryParse(part.Trim('"'), NumberStyles.Integer, CultureInfo.InvariantCulture, out var id)) yield return id;
    }

    /// <summary>Applies a field's conversion. <paramref name="emailOf"/> resolves user ids to email addresses (cross-realm only).</summary>
    public static object? Convert(string conversion, object? value, Func<long, string?> emailOf) => conversion switch
    {
        _ when value is null => null,
        ConnectedConversions.UserToEmail => JoinEmails(value, emailOf),
        ConnectedConversions.ReferenceToNumber => ToDecimal(value),
        ConnectedConversions.ToText => System.Convert.ToString(value is JsonElement je ? je.GetRawText() : value, CultureInfo.InvariantCulture),
        _ => value,
    };

    private static string? JoinEmails(object value, Func<long, string?> emailOf)
    {
        var emails = UserIds(value).Select(emailOf).Where(e => !string.IsNullOrWhiteSpace(e)).ToList();
        return emails.Count == 0 ? null : string.Join("; ", emails);
    }

    /// <summary>A canonical text form of a value as a field of <paramref name="destinationType"/> stores it, so a value read
    /// from the source and the same value read back from the destination compare equal (12.5 and 12.5000; a DATE and a
    /// DATETIME at midnight; blank text and NULL).</summary>
    public static string? Normalize(string destinationType, object? value)
    {
        value = Clean(value);
        if (value is null) return null;

        if (PhysicalNaming.IsRangeTypeCode(destinationType))
        {
            var (start, end) = RangeParts(value);
            var inner = destinationType == "DateRange" ? "Date" : "Number";
            var s = Normalize(inner, start);
            var e = Normalize(inner, end);
            return s is null && e is null ? null : $"{s}|{e}";
        }

        switch (destinationType)
        {
            case "Number" or "Currency" or "Percent" or "Rating" or "Duration":
                return ToDecimal(value) is { } d ? decimal.Round(d, 4).ToString("0.####", CultureInfo.InvariantCulture) : Text(value);
            case "Date":
                return ToDate(value) is { } date ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : Text(value);
            case "DateTime":
                return ToDate(value) is { } dt ? dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture) : Text(value);
            case "Time":
                return value switch
                {
                    TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
                    TimeOnly to => to.ToTimeSpan().ToString("c", CultureInfo.InvariantCulture),
                    DateTime t => t.TimeOfDay.ToString("c", CultureInfo.InvariantCulture),
                    _ => TimeSpan.TryParse(Text(value), CultureInfo.InvariantCulture, out var parsed) ? parsed.ToString("c", CultureInfo.InvariantCulture) : Text(value),
                };
            case "Boolean":
                return value switch
                {
                    bool b => b ? "1" : "0",
                    _ when ToDecimal(value) is { } n => n != 0 ? "1" : "0",
                    _ => bool.TryParse(Text(value), out var parsedBool) ? (parsedBool ? "1" : "0") : Text(value),
                };
            default:
                return Text(value);
        }
    }

    /// <summary>SHA-256 over the canonical values of one record, in field order, plus the mapping version (so connecting a
    /// new field makes every record count as changed once).</summary>
    public static byte[] Hash(IReadOnlyList<(long Fid, string? Canonical)> values, int mappingVersion)
    {
        var sb = new StringBuilder();
        sb.Append('v').Append(mappingVersion.ToString(CultureInfo.InvariantCulture));
        foreach (var (fid, canonical) in values)
        {
            sb.Append('\u001f').Append(fid.ToString(CultureInfo.InvariantCulture)).Append('=');
            if (canonical is null) sb.Append('\u0000');
            else sb.Append(canonical.Length.ToString(CultureInfo.InvariantCulture)).Append(':').Append(canonical);
        }
        return SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString()));
    }

    /// <summary>The sync key of a record: the canonical form of its refresh-key value (case-sensitive, at most 450 characters).</summary>
    public static string? Key(object? value)
    {
        var text = value switch
        {
            null => null,
            decimal or double or float or int or long => ToDecimal(value) is { } d ? decimal.Round(d, 4).ToString("0.####", CultureInfo.InvariantCulture) : null,
            DateTime dt => dt.ToString("yyyy-MM-dd'T'HH:mm:ss.fff", CultureInfo.InvariantCulture),
            _ => Text(value)?.Trim(),
        };
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= 450 ? text : "#" + System.Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    }

    private static object? Clean(object? value) => value switch
    {
        null or DBNull => null,
        string s when s.Length == 0 => null,
        JsonElement { ValueKind: JsonValueKind.Null or JsonValueKind.Undefined } => null,
        _ => value,
    };

    private static string? Text(object? value) => value switch
    {
        null => null,
        string s => s.Length == 0 ? null : s,
        JsonElement je => je.ValueKind == JsonValueKind.String ? je.GetString() : je.GetRawText(),
        IDictionary dict => JsonSerializer.Serialize(dict),
        _ => System.Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    private static decimal? ToDecimal(object? value) => value switch
    {
        null => null,
        decimal d => d,
        double db when !double.IsNaN(db) && !double.IsInfinity(db) => (decimal)db,
        float f when !float.IsNaN(f) && !float.IsInfinity(f) => (decimal)f,
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        bool bo => bo ? 1 : 0,
        TimeSpan ts => (decimal)ts.TotalMilliseconds,
        JsonElement { ValueKind: JsonValueKind.Number } je => je.GetDecimal(),
        _ => decimal.TryParse(Text(value), NumberStyles.Number | NumberStyles.AllowExponent, CultureInfo.InvariantCulture, out var parsed) ? parsed : null,
    };

    private static DateTime? ToDate(object? value) => value switch
    {
        DateTime dt => dt,
        DateTimeOffset dto => dto.UtcDateTime,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        _ => DateTime.TryParse(Text(value), CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var parsed) ? parsed : null,
    };

    private static (object? Start, object? End) RangeParts(object value) => value switch
    {
        IDictionary dict => (dict.Contains("start") ? dict["start"] : null, dict.Contains("end") ? dict["end"] : null),
        JsonElement { ValueKind: JsonValueKind.Object } je => (
            je.TryGetProperty("start", out var s) ? (object?)s : null,
            je.TryGetProperty("end", out var e) ? (object?)e : null),
        string s when s.TrimStart().StartsWith('{') => TryParseRange(s),
        _ => (value, null),
    };

    private static (object? Start, object? End) TryParseRange(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement.Clone();
            return RangeParts(root);
        }
        catch (JsonException) { return (json, null); }
    }
}
