using System.Collections;
using System.Globalization;
using System.Text.Json;

namespace PowerBase.API.QuickbaseApi.Common;

/// <summary>
/// Converts a record value as our report/record handlers return it into the shape Quickbase puts inside
/// <c>{"value": ...}</c> for the given Quickbase field type (developer.quickbase.com "Field types").
/// Unknown / empty values become null.
/// </summary>
public static class QbValueFormatter
{
    public const long MS_PER_MINUTE = 60_000;

    /// <param name="fileDownloadBase">For file fields: the app's download endpoint (<c>/apps/{appId}/files/download</c>).</param>
    public static object? Format(object? value, string qbFieldType, string? fileDownloadBase = null)
    {
        if (value is DBNull) value = null;
        // Our NumericRange / DateRange fields (no Quickbase type): {"start": .., "end": ..}, each part in the numeric/date format.
        if (qbFieldType is "numeric" or "date" && AsRange(value) is { } range)
        {
            // A range (no Quickbase type) keeps null for a missing part; an empty range is null.
            var start = Format(range.Start, qbFieldType) is "" ? null : Format(range.Start, qbFieldType);
            var end = Format(range.End, qbFieldType) is "" ? null : Format(range.End, qbFieldType);
            return start is null && end is null ? null : new Dictionary<string, object?> { ["start"] = start, ["end"] = end };
        }
        // An empty text (e.g. a formula over a blank field) is "no value", like every other empty field.
        if (value is string { Length: 0 }) value = null;
        if (value is JsonElement je) value = qbFieldType == "file" ? je.GetRawText() : FromJson(je);
        if (value is null) return Empty(qbFieldType);

        switch (qbFieldType)
        {
            case "file":
                return ToFile(value, fileDownloadBase);
            case "checkbox":
                return ToBool(value) ?? false;
            case "percent":
                // We store a percent as the shown number (75 = 75%); Quickbase uses the fraction (0.75).
                return Float(ToNumber(value) switch { long pl => (decimal)pl / 100m, decimal pd => pd / 100m, var other => other });
            case "address":
                return ToAddress(value);
            case "recordid":
                return ToNumber(value);
            case "numeric" or "currency" or "rating":
                return Float(ToNumber(value)); // Quickbase writes these as floating-point numbers (10.0)
            case "duration":
                // We store durations in minutes; Quickbase exchanges them in milliseconds.
                return ToNumber(value) is long m ? m * MS_PER_MINUTE : ToNumber(value) is decimal dm ? dm * MS_PER_MINUTE : null;
            case "date":
                return ToDateTime(value) is { } d ? d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : AsText(value);
            case "timestamp":
                return ToDateTime(value) is { } t
                    ? DateTime.SpecifyKind(t, t.Kind == DateTimeKind.Unspecified ? DateTimeKind.Utc : t.Kind).ToUniversalTime()
                        .ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture)
                    : AsText(value);
            case "timeofday":
                return value switch
                {
                    TimeSpan ts => ts.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
                    TimeOnly to => to.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    DateTime dt => dt.ToString("HH:mm:ss", CultureInfo.InvariantCulture),
                    string s when TimeSpan.TryParse(s, CultureInfo.InvariantCulture, out var p) && p >= TimeSpan.Zero && p < TimeSpan.FromDays(1)
                        => p.ToString(@"hh\:mm\:ss", CultureInfo.InvariantCulture),
                    _ => AsText(value),
                };
            case "multitext":
                return ToStringList(value);
            case "user":
                return ToUser(value);
            case "multiuser":
                return ToStringList(value)?.Select(n => ToUser(n)).ToList();
            default:
                return AsText(value);
        }
    }

    /// <summary>Quickbase user value. Our report pipeline hands us the display name; id/email/userName are filled
    /// only when the value itself carries them.</summary>
    /// <summary>
    /// What Quickbase returns for a field with no value (measured on a live realm): "" for text-like, date and time fields,
    /// [] for multi-select text and multi-user, an empty file object, false for a checkbox, null for numbers and users.
    /// </summary>
    public static object? Empty(string qbFieldType) => qbFieldType switch
    {
        "checkbox" => false,
        "text" or "rich-text" or "text-multi-line" or "text-multiple-choice" or "email" or "phone" or "url" or "date" or "timestamp" or "timeofday"
            or "address" or "dblink" => "",
        "multitext" or "multiuser" => new List<object>(),
        "file" => new Dictionary<string, object?> { ["url"] = "", ["versions"] = new List<object>() },
        _ => null,
    };

    /// <summary>A number in Quickbase's float form: whole values keep one decimal (10 → 10.0), trailing zeros dropped (999.9900 → 999.99).</summary>
    public static object? Float(object? number)
    {
        if (number is long l) return l + 0.0m;
        if (number is not decimal d) return number;
        d /= 1.000000000000000000000000000000000m;                   // drop trailing zeros (999.9900 → 999.99)
        return ((decimal.GetBits(d)[3] >> 16) & 0xFF) == 0 ? d + 0.0m : d;
    }

    private static object? ToUser(object? value)
    {
        if (value is null) return null;
        if (value is IDictionary<string, object?> map)
            return new Dictionary<string, object?>
            {
                ["email"] = map.TryGetValue("email", out var e) ? e : null,
                ["id"] = map.TryGetValue("id", out var i) ? i?.ToString() : null,
                ["name"] = map.TryGetValue("name", out var n) ? n : null,
                ["userName"] = map.TryGetValue("userName", out var u) ? u : map.TryGetValue("email", out var ue) ? ue : null,
            };
        var text = AsText(value) as string;
        if (string.IsNullOrWhiteSpace(text)) return null;
        return new Dictionary<string, object?> { ["email"] = text.Contains('@') ? text : null, ["id"] = null, ["name"] = text,
            ["userName"] = text.Contains('@') ? text : null };
    }

    /// <summary>
    /// Quickbase file attachment value: <c>{"url": ..., "versions": [{"versionNumber","fileName","uploaded","creator"}]}</c>.
    /// Built from our stored attachment JSON (current file + its revisions).
    /// </summary>
    public static object? ToFile(object value, string? downloadBase)
    {
        var text = value as string ?? value.ToString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        JsonDocument doc;
        try { doc = JsonDocument.Parse(text); } catch (JsonException) { return new Dictionary<string, object?> { ["url"] = null, ["versions"] = new List<object>() }; }
        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;
            string? S(JsonElement e, string n) => e.TryGetProperty(n, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null;
            string? Url(JsonElement e) => downloadBase is null || S(e, "path") is not { } path ? null
                : $"{downloadBase}?path={Uri.EscapeDataString(path)}&fileName={Uri.EscapeDataString(S(e, "name") ?? "file")}";
            Dictionary<string, object?> Version(JsonElement e, int number) => new()
            {
                ["versionNumber"] = e.TryGetProperty("revisionNumber", out var n) && n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out var rn) ? rn : number,
                ["fileName"] = S(e, "name"),
                ["uploaded"] = Instant(S(e, "uploadedOn")),
                ["creator"] = S(e, "uploadedBy") is { } by
                    ? new Dictionary<string, object?> { ["email"] = by.Contains('@') ? by : null, ["id"] = null, ["name"] = by, ["userName"] = by.Contains('@') ? by : null }
                    : null,
            };
            var versions = new List<object>();
            if (root.TryGetProperty("revisions", out var revs) && revs.ValueKind == JsonValueKind.Array)
            {
                var i = 0;
                foreach (var r in revs.EnumerateArray()) versions.Add(Version(r, ++i));
            }
            versions.Add(Version(root, versions.Count + 1));
            var file = new Dictionary<string, object?> { ["url"] = Url(root) };
            // Quickbase adds reservedBy only while someone has the file reserved.
            if (root.TryGetProperty("reservation", out var res) && res.ValueKind == JsonValueKind.Object)
                file["reservedBy"] = new Dictionary<string, object?>
                {
                    ["email"] = null,
                    ["id"] = null,
                    ["name"] = S(res, "userName") ?? S(res, "UserName"),
                    ["userName"] = null,
                };
            file["versions"] = versions;
            return file;
        }
    }

    /// <summary>Quickbase date-time text: YYYY-MM-DDThh:mm:ssZ (no fractional seconds).</summary>
    private static string? Instant(string? value) =>
        value is not null && DateTime.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d)
            ? d.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture) : value;

    /// <summary>Quickbase returns an address as one string, e.g. "10 main street, Cambridge, MA 02139".</summary>
    public static string? ToAddress(object value)
    {
        var text = value as string ?? value.ToString();
        if (string.IsNullOrWhiteSpace(text)) return null;
        if (!text.TrimStart().StartsWith('{')) return text;
        try
        {
            using var doc = JsonDocument.Parse(text);
            string P(string n) => doc.RootElement.TryGetProperty(n, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
            // Quickbase's combined value (live realm): "street1, street2, city, state zip" — country left out, and with no state
            // the postal code follows the city after a space ("1 Main Street, Boston 02110").
            var stateZip = string.Join(" ", new[] { P("state"), P("zip") }.Where(x => x.Length > 0));
            var city = P("city");
            var tail = P("state").Length == 0 ? string.Join(" ", new[] { city, P("zip") }.Where(x => x.Length > 0))
                : string.Join(", ", new[] { city, stateZip }.Where(x => x.Length > 0));
            var parts = new[] { P("street1"), P("street2"), tail }.Where(x => x.Length > 0).ToList();
            return parts.Count == 0 ? null : string.Join(", ", parts);
        }
        catch (JsonException) { return text; }
    }

    /// <summary>A stored range value — {"start","end"} as a dictionary, a JSON element or JSON text — else null.</summary>
    private static (object? Start, object? End)? AsRange(object? value)
    {
        switch (value)
        {
            case IDictionary<string, object?> d when d.Count == 2 && d.ContainsKey("start") && d.ContainsKey("end"):
                return (d["start"], d["end"]);
            case System.Collections.IDictionary nd when nd.Count == 2 && nd.Contains("start") && nd.Contains("end"):
                return (nd["start"], nd["end"]);
            case string s when s.TrimStart().StartsWith('{'):
                try { using var doc = JsonDocument.Parse(s); return AsRange(doc.RootElement.Clone()); }
                catch (JsonException) { return null; }
            case JsonElement { ValueKind: JsonValueKind.Object } o:
                object? start = null, end = null; var n = 0;
                foreach (var p in o.EnumerateObject())
                {
                    n++;
                    if (p.Name.Equals("start", StringComparison.OrdinalIgnoreCase)) start = FromJson(p.Value);
                    else if (p.Name.Equals("end", StringComparison.OrdinalIgnoreCase)) end = FromJson(p.Value);
                    else return null;
                }
                return n == 0 ? null : (start, end);
            default:
                return null;
        }
    }

    private static object? FromJson(JsonElement je) => je.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => je.GetString(),
        JsonValueKind.Number => je.TryGetInt64(out var l) ? l : je.GetDecimal(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Array => je.EnumerateArray().Select(FromJson).ToList(),
        _ => je.GetRawText(),
    };

    private static bool? ToBool(object value) => value switch
    {
        bool b => b,
        string s when bool.TryParse(s, out var b) => b,
        string s when s is "1" or "yes" or "Yes" => true,
        string s when s is "0" or "no" or "No" or "" => false,
        IConvertible c => c.ToDecimal(CultureInfo.InvariantCulture) != 0,
        _ => null,
    };

    private static object? ToNumber(object value) => value switch
    {
        byte or short or int or long => Convert.ToInt64(value, CultureInfo.InvariantCulture),
        decimal d => d == decimal.Truncate(d) && Math.Abs(d) < long.MaxValue ? (object)(long)d : d,
        double or float => Convert.ToDecimal(value, CultureInfo.InvariantCulture),
        string s when decimal.TryParse(s, NumberStyles.Any, CultureInfo.InvariantCulture, out var d) => ToNumber(d),
        string s when string.IsNullOrWhiteSpace(s) => null,
        _ => AsText(value),
    };

    private static DateTime? ToDateTime(object value) => value switch
    {
        DateTime d => d,
        DateTimeOffset o => o.UtcDateTime,
        DateOnly d => d.ToDateTime(TimeOnly.MinValue),
        string s when DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var d) => d,
        _ => null,
    };

    private static List<string>? ToStringList(object value)
    {
        if (value is string s)
        {
            if (string.IsNullOrWhiteSpace(s)) return null;
            var trimmed = s.Trim();
            if (trimmed.StartsWith('['))
            {
                try { return JsonSerializer.Deserialize<List<string>>(trimmed); } catch (JsonException) { }
            }
            return trimmed.Split(new[] { ';', ',' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
        }
        if (value is IEnumerable list)
            return list.Cast<object?>().Where(x => x is not null).Select(x => AsText(x) as string ?? "").ToList();
        return new List<string> { AsText(value) as string ?? "" };
    }

    private static object? AsText(object? value) => value switch
    {
        null => null,
        string s => s,
        DateTime d => d.ToString("o", CultureInfo.InvariantCulture),
        IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
        IDictionary => value,
        IEnumerable e => e.Cast<object?>().ToList(),
        _ => value.ToString(),
    };
}
