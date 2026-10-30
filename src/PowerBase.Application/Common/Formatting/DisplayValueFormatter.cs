using System.Globalization;
using System.Text.Json;
using PowerBase.Domain.FieldSettings;
using PowerBase.Domain.ValueObjects;

namespace PowerBase.Application.Common.Formatting;

/// <summary>
/// Turns a stored field value into the text a user sees in the grid — server-side twin of the
/// Angular <c>AppFormattingService</c> (formatNumber / formatCurrency / formatDate) and
/// <c>formatDurationMinutes</c>, so text built on the server (e.g. a Combined Text summary over a
/// Currency or Date field) reads exactly like the cells it came from. Keep the two in step: a
/// formatting rule changed on one side should change on the other.
///
/// Field-level Behavior Settings override the app-level Formatting defaults, same precedence as
/// the frontend. Plain-text types (Text, SingleSelect, …) pass through. Types whose stored value
/// is an id or a structure read as the grid shows them: users by name, files by file name, Rich
/// Text without its markup, a range as "start – end", a Reference as its Record ID#.
/// </summary>
public static class DisplayValueFormatter
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary>The user ids a User / MultiUser value holds (numeric core.[User].Id) — to look their names up
    /// (<see cref="Format"/>'s userNames) before formatting. Nothing for any other type.</summary>
    public static IEnumerable<long> UserIds(object? raw, string typeCode) =>
        typeCode is "User" or "MultiUser"
            ? UserIdTexts(raw).Select(t => long.TryParse(t, NumberStyles.Integer, Inv, out var id) ? id : (long?)null)
                .Where(id => id.HasValue).Select(id => id!.Value)
            : [];

    /// <summary>Parses an App's raw Formatting JSON; defaults on null/blank/malformed — display
    /// formatting must never throw.</summary>
    public static AppFormattingSettings ParseAppFormatting(string? formattingJson)
    {
        if (string.IsNullOrWhiteSpace(formattingJson)) return new AppFormattingSettings();
        try { return JsonSerializer.Deserialize<AppFormattingSettings>(formattingJson, JsonOpts) ?? new AppFormattingSettings(); }
        catch (JsonException) { return new AppFormattingSettings(); }
    }

    /// <summary>Display text for <paramref name="raw"/> (null/blank ⇒ null).</summary>
    /// <param name="typeCode">The source field's TypeCode.</param>
    /// <param name="fieldSettingsJson">The source field's Settings JSON (its Behavior Settings).</param>
    /// <param name="today">"Now" for Hide-year-if-current; injectable for tests.</param>
    /// <param name="userNames">Display names by user id (see <see cref="UserIds"/>), for User / MultiUser; an id
    /// with no name shows as itself.</param>
    public static string? Format(object? raw, string typeCode, string? fieldSettingsJson, AppFormattingSettings app, DateTime? today = null,
        IReadOnlyDictionary<long, string>? userNames = null)
    {
        if (raw is null || raw is DBNull) return null;
        if (raw is string s && s.Length == 0) return null;

        switch (typeCode)
        {
            case "User":
            case "MultiUser":
                return Blank(string.Join(", ", UserIdTexts(raw).Select(t => UserName(t, userNames))));
            case "File":
                return Blank(FormatFiles(raw));
            case "RichText":
                return Blank(StripMarkup(Convert.ToString(raw, Inv)!));
            case "DateRange":
            case "NumericRange":
                return FormatRange(raw, typeCode == "DateRange", Parse<DateSettings>(fieldSettingsJson), app, today ?? DateTime.Today);
            case "Reference":
                // The parent's Record ID# — whole, though a Reference converted from a Number field is a DECIMAL column.
                return TryDecimal(raw, out var recordId) ? recordId.ToString("0.############", Inv) : raw.ToString();
            case "Number":
                return TryDecimal(raw, out var n) ? FormatNumber(n, Parse<NumericSettings>(fieldSettingsJson), app) : raw.ToString();
            case "Currency":
                return TryDecimal(raw, out var c) ? FormatCurrency(c, Parse<NumericSettings>(fieldSettingsJson), app) : raw.ToString();
            case "Percent":
                return TryDecimal(raw, out var p) ? FormatNumber(p, Parse<NumericSettings>(fieldSettingsJson), app) + "%" : raw.ToString();
            case "Rating":
                // Whole stars — the app's decimal-places default is for quantities, not star counts.
                return TryDecimal(raw, out var r) ? Math.Round(r).ToString("0", Inv) : raw.ToString();
            case "Duration":
                var ds = Parse<DurationSettings>(fieldSettingsJson);
                return TryDecimal(raw, out var minutes) ? FormatDurationMinutes(minutes, ds?.Display, ds?.Decimals) : raw.ToString();
            case "Date":
            case "DateTime":
                return TryDate(raw, out var d)
                    ? FormatDate(d, typeCode == "DateTime", Parse<DateSettings>(fieldSettingsJson), app, today ?? DateTime.Today)
                    : raw.ToString();
            case "Boolean":
                return raw is true || (raw is not bool && Convert.ToString(raw, Inv) is "1" or "true" or "True") ? "Yes" : "No";
            case "Address":
                return FormatAddress(Convert.ToString(raw, Inv)!);
            case "Phone":
                return FormatPhone(Convert.ToString(raw, Inv)!);
            case "MultiSelect":
                // A Multi-select formula gives its choices as a list; a stored field as JSON / comma-separated text.
                return raw is System.Collections.IEnumerable choices and not string
                    ? Blank(string.Join(", ", choices.Cast<object?>().Select(c => Convert.ToString(c, Inv)).Where(c => !string.IsNullOrWhiteSpace(c))))
                    : FormatMultiSelect(Convert.ToString(raw, Inv)!);
            case "Email":
                return FormatEmail(Convert.ToString(raw, Inv)!, Parse<EmailSettings>(fieldSettingsJson));
            case "Url":
                return FormatUrl(Convert.ToString(raw, Inv)!, Parse<UrlSettings>(fieldSettingsJson));
            case "Time":
                return FormatTime(Convert.ToString(raw, Inv)!, Parse<TimeSettings>(fieldSettingsJson));
            default:
                return Convert.ToString(raw, Inv);
        }
    }

    // ── Structured / text types (table-report-view.component.ts displayFieldValue) ──

    /// <summary>Address is stored as JSON; shown as its non-empty parts joined with ", ".</summary>
    public static string FormatAddress(string json)
    {
        if (!TryParseObject(json, out var root)) return json;
        var parts = new[] { "street1", "street2", "city", "state", "zip", "country" }
            .Select(k => root.TryGetProperty(k, out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() : null)
            .Where(s => !string.IsNullOrWhiteSpace(s));
        return string.Join(", ", parts);
    }

    /// <summary>Phone is stored as JSON {"number","ext"}; the grid shows just the number.</summary>
    public static string FormatPhone(string json)
    {
        if (!TryParseObject(json, out var root)) return json;
        return root.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() ?? "" : "";
    }

    /// <summary>MultiSelect is a JSON array (or, for older rows, comma-separated text); shown as "a, b".</summary>
    public static string FormatMultiSelect(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith('['))
        {
            try
            {
                var items = JsonSerializer.Deserialize<List<JsonElement>>(trimmed) ?? [];
                return string.Join(", ", items.Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() : i.GetRawText())
                    .Where(s => !string.IsNullOrWhiteSpace(s)));
            }
            catch (JsonException) { /* not an array after all — fall through */ }
        }
        return string.Join(", ", value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
    }

    /// <summary>Email Display settings: link text replaces the address; otherwise "before _" / "before @" trim it.</summary>
    public static string FormatEmail(string email, EmailSettings? field)
    {
        if (!string.IsNullOrEmpty(field?.LinkText)) return field!.LinkText!;
        if (field?.ShowBeforeUnderscore == true) return email.Split('_')[0];
        if (field?.ShowBeforeAt == true) return email.Split('@')[0];
        return email;
    }

    /// <summary>Url Display settings: link text replaces the URL; otherwise optionally drop "http(s)://".</summary>
    public static string FormatUrl(string url, UrlSettings? field)
    {
        if (!string.IsNullOrEmpty(field?.LinkText)) return field!.LinkText!;
        return field?.HideProtocol == true ? System.Text.RegularExpressions.Regex.Replace(url, "^https?://", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase) : url;
    }

    /// <summary>Time is stored as 24-hour "HH:mm[:ss]"; shown 12-hour unless "Use 24-hour" is on, with
    /// seconds only for the HH:MM:SS format (formatTimeValue).</summary>
    public static string FormatTime(string value, TimeSettings? field)
    {
        var m = System.Text.RegularExpressions.Regex.Match(value, @"(\d{1,2}):(\d{2})(?::(\d{2}))?");
        if (!m.Success) return value;
        var hours = int.Parse(m.Groups[1].Value, Inv);
        var minutes = m.Groups[2].Value;
        var seconds = m.Groups[3].Success ? m.Groups[3].Value : "00";
        var suffix = field?.Format == TimeFormats.HHMMSS ? $":{seconds}" : "";
        if (field?.Use24Hour == true) return $"{hours:00}:{minutes}{suffix}";
        var period = hours >= 12 ? "PM" : "AM";
        var hours12 = hours % 12 == 0 ? 12 : hours % 12;
        return $"{hours12}:{minutes}{suffix} {period}";
    }

    // ── Values the grid shows by resolving or unwrapping them (table-report-view displayFieldValue) ──

    /// <summary>The user ids a User / MultiUser value holds, as text: one id, a JSON array or a comma-separated list
    /// (older rows) — or, from a User List formula, a list of ids.</summary>
    private static IEnumerable<string> UserIdTexts(object? raw)
    {
        switch (raw)
        {
            case null or DBNull:
                return [];
            case string s:
                var trimmed = s.Trim();
                if (trimmed.StartsWith('['))
                {
                    try
                    {
                        var items = JsonSerializer.Deserialize<List<JsonElement>>(trimmed) ?? [];
                        return items.Select(i => i.ValueKind == JsonValueKind.String ? i.GetString() ?? "" : i.GetRawText())
                            .Where(t => !string.IsNullOrWhiteSpace(t)).ToList();
                    }
                    catch (JsonException) { /* not an array after all — fall through */ }
                }
                return trimmed.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            case System.Collections.IEnumerable list:
                return list.Cast<object?>().Select(v => Convert.ToString(v, Inv)?.Trim() ?? "").Where(t => t.Length > 0).ToList();
            default:
                return TryDecimal(raw, out var id) ? [id.ToString("0", Inv)] : [Convert.ToString(raw, Inv)!];
        }
    }

    /// <summary>A user's display name; an id with no known name (or a non-numeric one) shows as itself.</summary>
    private static string UserName(string idText, IReadOnlyDictionary<long, string>? userNames) =>
        long.TryParse(idText, NumberStyles.Integer, Inv, out var id) && userNames?.GetValueOrDefault(id) is { Length: > 0 } name
            ? name
            : idText;

    /// <summary>A File value is a JSON list of attachments (or one); shown as their names, "attachment" for one with none.
    /// A value that isn't JSON is one attachment.</summary>
    public static string FormatFiles(object raw)
    {
        var text = Convert.ToString(raw, Inv)!.Trim();
        try
        {
            using var doc = JsonDocument.Parse(text);
            var items = doc.RootElement.ValueKind == JsonValueKind.Array
                ? doc.RootElement.EnumerateArray().ToList()
                : [doc.RootElement];
            return string.Join(", ", items.Select(i =>
                i.ValueKind == JsonValueKind.Object && i.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
                    && !string.IsNullOrWhiteSpace(n.GetString())
                    ? n.GetString()
                    : "attachment"));
        }
        catch (JsonException)
        {
            return "attachment";
        }
    }

    /// <summary>Rich Text without its markup: tags dropped (a line break or block end as a space), entities decoded,
    /// whitespace collapsed.</summary>
    public static string StripMarkup(string html)
    {
        var text = System.Text.RegularExpressions.Regex.Replace(html, @"<\s*(br|/p|/div|/li|/h[1-6])\b[^>]*>", " ",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]*>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        return System.Text.RegularExpressions.Regex.Replace(text, @"\s+", " ").Trim();
    }

    /// <summary>A range read as {"start","end"} (the summary query's JSON, or a record read's value): "start – end",
    /// "…" for an open end; null when both are blank. Dates in the field's date format; numbers as they read
    /// ("10", "2.5").</summary>
    private static string? FormatRange(object raw, bool isDate, DateSettings? field, AppFormattingSettings app, DateTime today)
    {
        if (!TryParseObject(Convert.ToString(raw, Inv)!, out var root)) return Convert.ToString(raw, Inv);
        string? Part(string name)
        {
            if (!root.TryGetProperty(name, out var p) || p.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) return null;
            var text = p.ValueKind == JsonValueKind.String ? p.GetString() : p.GetRawText();
            if (string.IsNullOrWhiteSpace(text)) return null;
            if (isDate)
                return TryDate(text, out var d) ? FormatDate(d, false, field, app, today) : text;
            return decimal.TryParse(text, NumberStyles.Number, Inv, out var n) ? n.ToString("0.############", Inv) : text;
        }
        var start = Part("start");
        var end = Part("end");
        if (start is null && end is null) return null;
        return $"{start ?? "…"} – {end ?? "…"}";
    }

    private static string? Blank(string text) => string.IsNullOrWhiteSpace(text) ? null : text;

    private static bool TryParseObject(string json, out JsonElement root)
    {
        root = default;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            root = doc.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    // ── Number / Currency (AppFormattingService.formatNumber / formatCurrency) ──

    public static string FormatNumber(decimal value, NumericSettings? field, AppFormattingSettings app)
    {
        var decimals = Math.Clamp(field?.Decimals ?? app.Number.DecimalPlaces, 0, 10);
        // "" (the field-level "None" option) is an explicit choice, not "unset" — ?? not ||.
        var separator = field?.Separator ?? app.Number.ThousandSeparator ?? ",";
        var indian = string.Equals(app.Number.DisplayPattern, "Indian", StringComparison.OrdinalIgnoreCase);

        var fixedText = Math.Round(value, decimals, MidpointRounding.AwayFromZero).ToString("F" + decimals, Inv);
        var negative = fixedText.StartsWith('-');
        if (negative) fixedText = fixedText[1..];
        var parts = fixedText.Split('.');
        var intPart = GroupDigits(parts[0], separator, indian);
        var text = parts.Length > 1 ? $"{intPart}.{parts[1]}" : intPart;
        return negative ? "-" + text : text;
    }

    public static string FormatCurrency(decimal value, NumericSettings? field, AppFormattingSettings app)
    {
        var symbol = !string.IsNullOrEmpty(field?.Symbol) ? field!.Symbol! : !string.IsNullOrEmpty(app.Currency.Symbol) ? app.Currency.Symbol : "$";
        var position = !string.IsNullOrEmpty(field?.Position) ? field!.Position! : app.Currency.Position ?? "Before";
        var number = FormatNumber(value, field, app);
        if (string.Equals(position, "After", StringComparison.OrdinalIgnoreCase)) return number + symbol;
        if (string.Equals(position, "BetweenSignAndNumber", StringComparison.OrdinalIgnoreCase) && number.StartsWith('-'))
            return "-" + symbol + number[1..];
        return symbol + number;
    }

    /// <summary>Western: groups of 3 ("1,234,567"). Indian: last 3, then groups of 2 ("12,34,567").</summary>
    private static string GroupDigits(string digits, string separator, bool indian)
    {
        if (separator.Length == 0 || digits.Length <= 3) return digits;
        var head = digits[..^3];
        var tail = digits[^3..];
        var groupSize = indian ? 2 : 3;
        var groups = new List<string>();
        for (var end = head.Length; end > 0; end -= groupSize)
            groups.Insert(0, head[Math.Max(0, end - groupSize)..end]);
        return string.Join(separator, groups) + separator + tail;
    }

    // ── Date / DateTime (AppFormattingService.formatDate) ──

    public static string FormatDate(DateTime value, bool isDateTime, DateSettings? field, AppFormattingSettings app, DateTime today)
    {
        var formatString = string.IsNullOrWhiteSpace(app.Date.FormatString) ? "MM-DD-YYYY" : app.Date.FormatString;
        var separator = app.Date.Separator == "/" ? "/" : "-";

        var tokens = formatString.Split('-', '/', '.').Select(t => t.ToUpperInvariant()).ToList();
        if (field?.HideYearIfCurrent == true && value.Year == today.Year)
            tokens.RemoveAll(t => t is "YYYY" or "YY");

        // .NET custom format: each token mapped, separators quoted so they print literally.
        var netFormat = string.Join($"'{separator}'", tokens.Select(t => t switch
        {
            "YYYY" => "yyyy",
            "YY" => "yy",
            "DD" => "dd",
            "MM" => field?.ShowMonthName == true ? "MMM" : "MM",
            _ => $"'{t}'",
        }));
        if (field?.ShowDayOfWeek == true) netFormat = "ddd', '" + netFormat;
        // DateTime's "Show the time" defaults to on — only an explicit false hides it.
        if (isDateTime && field?.ShowTime != false) netFormat += " h:mm tt";

        return value.ToString(netFormat, Inv);
    }

    // ── Duration (duration-format.util.ts formatDurationMinutes) ──

    public static string FormatDurationMinutes(decimal minutes, string? display, int? decimals)
    {
        var d = decimals ?? 0;
        string Fmt(decimal n) => d == 0 ? Math.Round(n, MidpointRounding.AwayFromZero).ToString("0", Inv) : n.ToString("F" + d, Inv);
        string Plain(decimal n) => n.ToString("0.##########", Inv);
        // "1 hr", not "1 hrs": the singular unit when the shown number is exactly one.
        string Unit(string text, string one, string many) =>
            $"{text} {(decimal.TryParse(text, NumberStyles.Number, Inv, out var v) && v == 1 ? one : many)}";

        switch (display)
        {
            case "HHMM":
            {
                var total = (long)Math.Round(minutes, MidpointRounding.AwayFromZero);
                return $"{total / 60}:{total % 60:00}";
            }
            case "HHMMSS":
            {
                var total = (long)Math.Round(minutes * 60, MidpointRounding.AwayFromZero);
                return $"{total / 3600}:{total % 3600 / 60:00}:{total % 60:00}";
            }
            case "MM":
                return $"{Math.Round(minutes, MidpointRounding.AwayFromZero).ToString("0", Inv)} min";
            case "MMSS":
            {
                var total = (long)Math.Round(minutes * 60, MidpointRounding.AwayFromZero);
                return $"{total / 60}:{total % 60:00}";
            }
            case "Weeks": return Unit(Fmt(minutes / 10080), "wk", "wks");
            case "Days": return Unit(Fmt(minutes / 1440), "day", "days");
            case "Hours": return Unit(Fmt(minutes / 60), "hr", "hrs");
            case "Minutes": return Unit(Fmt(minutes), "min", "mins");
            case "Seconds": return Unit(Fmt(minutes * 60), "sec", "secs");
            default: // "Smart" (or unset)
                if (minutes >= 10080 && minutes % 10080 == 0) return Unit(Plain(minutes / 10080), "wk", "wks");
                if (minutes >= 1440 && minutes % 1440 == 0) return $"{Plain(minutes / 1440)} {(minutes == 1440 ? "day" : "days")}";
                if (minutes >= 60 && minutes % 60 == 0) return $"{Plain(minutes / 60)} {(minutes == 60 ? "hr" : "hrs")}";
                if (minutes >= 1) return $"{Plain(minutes)} {(minutes == 1 ? "min" : "mins")}";
                if (minutes > 0 && minutes < 1) return Unit(Plain(minutes * 60), "sec", "secs");
                return $"{Plain(minutes)} mins";
        }
    }

    // ── helpers ──

    private static T? Parse<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, JsonOpts); }
        catch (JsonException) { return null; }
    }

    private static bool TryDecimal(object raw, out decimal value)
    {
        switch (raw)
        {
            case decimal m: value = m; return true;
            case double or float or int or long or short or byte:
                value = Convert.ToDecimal(raw, Inv); return true;
            default:
                return decimal.TryParse(Convert.ToString(raw, Inv), NumberStyles.Number, Inv, out value);
        }
    }

    private static bool TryDate(object raw, out DateTime value)
    {
        switch (raw)
        {
            case DateTime dt: value = dt; return true;
            case DateTimeOffset dto: value = dto.DateTime; return true;
            case DateOnly d: value = d.ToDateTime(TimeOnly.MinValue); return true;
            default:
                return DateTime.TryParse(Convert.ToString(raw, Inv), Inv, DateTimeStyles.None, out value);
        }
    }
}
