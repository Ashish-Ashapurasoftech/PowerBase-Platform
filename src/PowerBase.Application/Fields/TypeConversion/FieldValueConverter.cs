using System.Globalization;
using System.Text.Json;
using PowerBase.Application.Common.Formatting;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Fields.TypeConversion;

/// <summary>
/// Converts one stored (or formula-evaluated) value from a field's old type into the new type's storage form,
/// following docs/field-type-conversion.md. Pure and stateless: the Change Type handler builds a
/// <see cref="FieldValueConversionContext"/> once and runs every record through <see cref="Convert"/>.
/// Formula types are converted as their scalar twin (Formula_Text as Text, …). Values that can't be typecast
/// become null with <see cref="ConvertedValue.Lost"/> set — records themselves are never touched.
/// </summary>
public static class FieldValueConverter
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };
    // Stored as data, never emitted into HTML — keep "+" and "()" readable in the phone JSON.
    private static readonly JsonSerializerOptions StoreJsonOpts = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static ConvertedValue Convert(object? raw, string sourceTypeCode, string targetTypeCode, FieldValueConversionContext? context = null)
    {
        context ??= FieldValueConversionContext.Default;
        if (IsBlank(raw)) return ConvertedValue.Empty;

        var source = FieldTypeConversionMatrix.ValueTypeOf(sourceTypeCode);
        var target = FieldTypeConversionMatrix.ValueTypeOf(targetTypeCode);

        // Formula → its own scalar twin (Formula_Bool → Boolean, …): only the storage form changes.
        if (source == target && FieldTypeConversionMatrix.IsFormula(sourceTypeCode))
        {
            var same = ToStorageForm(raw!, target, context);
            return new ConvertedValue(same, same is null);
        }

        var value = target switch
        {
            "Text" => ToText(raw!, source, context, singleLine: true),
            "TextMultiLine" => ToText(raw!, source, context, singleLine: false),
            "RichText" => ToRichText(raw!, source, context),
            "SingleSelect" => ToText(raw!, source, context, singleLine: true),
            "Number" or "Currency" or "Rating" => ToNumber(raw!, source, target, context),
            "Percent" => ToNumber(raw!, source, target, context),
            "Boolean" => ToBoolean(raw!, source),
            "Date" => ToDate(raw!, source, context, withTime: false),
            "DateTime" => ToDate(raw!, source, context, withTime: true),
            "Time" => ToTime(raw!, source),
            "Duration" => ToDuration(raw!, source, context),
            "Email" => ToEmail(raw!, source, context),
            "Phone" => ToPhone(raw!, source),
            "Url" => ToUrl(raw!, source, context),
            "User" => ToUser(raw!, source, context),
            _ => null, // File and anything unsupported: nothing can be carried over
        };

        return new ConvertedValue(value, value is null);
    }

    /// <summary>A formula value (FormulaRawValue form: ISO date strings, "HH:mm:ss", decimal minutes, user Id
    /// text, plain phone text) as the storage form of its scalar twin.</summary>
    private static object? ToStorageForm(object raw, string type, FieldValueConversionContext context)
    {
        switch (type)
        {
            case "Number" or "Currency" or "Percent" or "Rating":
                return TryDecimal(raw, out var n) && NumberText.TryFitStorage(n, out var stored) ? stored : null;
            case "Boolean":
                return TryBool(raw, out var b) ? b : null;
            case "Date" or "DateTime":
                return TryDate(raw, out var d) ? (type == "Date" ? d.Date : d) : null;
            case "Time":
                return raw is TimeSpan t ? t : TimeText.TryExtractFirst(TimeString(raw), out var parsed) ? parsed : null;
            case "Duration":
                if (!TryDurationMinutes(raw, out var minutes)) return null;
                var whole = Math.Round(minutes, MidpointRounding.AwayFromZero);
                return whole is >= int.MinValue and <= int.MaxValue ? (long)whole : null;
            case "User":
                return TryUserId(raw, out var id) ? id.ToString(Inv) : ToUser(raw, "Text", context);
            case "Phone":
                return ToPhone(PhoneText(AsString(raw)) ?? "", "Text");
            default:
                return AsString(raw);
        }
    }

    // ── → Text family ────────────────────────────────────────────────────────

    /// <summary>The text form of a value of <paramref name="source"/> type. Any → Text never loses the value.</summary>
    public static string? ToText(object raw, string source, FieldValueConversionContext context, bool singleLine)
    {
        string? text = source switch
        {
            "Number" or "Currency" or "Percent" or "Rating" =>
                TryDecimal(raw, out var n) ? n.ToString("0.##########", Inv) : AsString(raw),
            "Date" or "DateTime" =>
                TryDate(raw, out var d)
                    ? DisplayValueFormatter.FormatDate(d, source == "DateTime", null, context.AppFormatting, DateTime.Today)
                    : AsString(raw),
            "Time" => DisplayValueFormatter.FormatTime(TimeString(raw), null),
            "Duration" =>
                TryDurationMinutes(raw, out var minutes) ? DisplayValueFormatter.FormatDurationMinutes(minutes, "Smart", null) : AsString(raw),
            "Boolean" => TryBool(raw, out var b) ? (b ? "Yes" : "No") : null,
            "Phone" => PhoneText(AsString(raw)),
            "File" => FileNames(AsString(raw)),
            "User" => TryUserId(raw, out var id)
                ? context.UserEmailsById.TryGetValue(id, out var email) ? email : id.ToString(Inv)
                : AsString(raw),
            _ => AsString(raw),
        };

        if (text is null) return null;
        if (singleLine) text = string.Join(' ', text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return text.Length == 0 ? null : text;
    }

    /// <summary>RichText is rendered as trusted HTML, so plain text is HTML-encoded on the way in — markup typed
    /// into a Text field shows literally instead of executing (deliberate deviation from QB, see the spec).</summary>
    private static string? ToRichText(object raw, string source, FieldValueConversionContext context)
    {
        if (source == "RichText") return AsString(raw);
        var text = ToText(raw, source, context, singleLine: false);
        return text is null ? null : System.Net.WebUtility.HtmlEncode(text).ReplaceLineEndings("<br>");
    }

    // ── → Numeric family ─────────────────────────────────────────────────────

    private static object? ToNumber(object raw, string source, string target, FieldValueConversionContext context)
    {
        decimal number;
        switch (source)
        {
            case "Number" or "Currency" or "Percent" or "Rating":
                if (!TryDecimal(raw, out number)) return null;
                // Same rule as the Display-As switch: Percent is stored whole (115 = 115 %).
                if (target == "Percent" && source != "Percent") number *= 100;
                else if (source == "Percent" && target != "Percent") number /= 100;
                break;
            case "Boolean":
                if (!TryBool(raw, out var b)) return null;
                number = b ? 1 : 0;
                break;
            case "Duration":
                if (!TryDurationMinutes(raw, out var minutes)) return null;
                number = minutes / DurationText.MinutesPerDisplayUnit(DurationDisplay(context.SourceSettingsJson));
                break;
            case "Date" or "DateTime" or "Time" or "User" or "File":
                // First number in the value as it is shown (date in the app's format, user as email, file
                // name): 10-02-2026 → 10, 1:30 PM → 1, invoice 2024.pdf → 2024.
                var shown = ToText(raw, source, context, singleLine: true);
                if (shown is null || !NumberText.TryExtractFirst(shown, out number)) return null;
                break;
            default:
                var text = AsString(raw);
                if (source == "RichText") text = HtmlText.ToPlainText(text);
                if (source == "Phone") text = PhoneText(text) ?? "";
                if (!NumberText.TryExtractFirst(text, out number)) return null;
                break;
        }
        return NumberText.TryFitStorage(number, out var stored) ? stored : null;
    }

    // ── → Boolean ────────────────────────────────────────────────────────────

    private static object? ToBoolean(object raw, string source)
    {
        switch (source)
        {
            case "Number" or "Currency" or "Percent" or "Rating":
                if (!TryDecimal(raw, out var n)) return null;
                return n == 1 ? true : n == 0 ? false : null;
            case "Text" or "TextMultiLine" or "SingleSelect":
                return BooleanText.TryParse(AsString(raw), out var b) ? b : null;
            case "RichText":
                return BooleanText.TryParse(HtmlText.ToPlainText(AsString(raw)), out var rb) ? rb : null;
            default:
                return null;
        }
    }

    // ── → Date / DateTime / Time / Duration ──────────────────────────────────

    private static object? ToDate(object raw, string source, FieldValueConversionContext context, bool withTime)
    {
        switch (source)
        {
            case "Date" or "DateTime":
                if (!TryDate(raw, out var d)) return null;
                return withTime ? d : d.Date;
            case "Text" or "TextMultiLine" or "SingleSelect" or "RichText":
                var text = source == "RichText" ? HtmlText.ToPlainText(AsString(raw)) : AsString(raw);
                return DateText.TryExtractFirst(text, context.AppFormatting, withTime, out var parsed) ? parsed : null;
            default:
                return null;
        }
    }

    private static object? ToTime(object raw, string source)
    {
        switch (source)
        {
            case "DateTime":
                return TryDate(raw, out var d) ? d.TimeOfDay : null;
            case "Text" or "TextMultiLine" or "SingleSelect" or "RichText":
                var text = source == "RichText" ? HtmlText.ToPlainText(AsString(raw)) : AsString(raw);
                return TimeText.TryExtractFirst(text, out var t) ? t : null;
            default:
                return null;
        }
    }

    /// <summary>Duration is stored as whole minutes in an INT column.</summary>
    private static object? ToDuration(object raw, string source, FieldValueConversionContext context)
    {
        var display = DurationDisplay(context.TargetSettingsJson);
        decimal minutes;
        switch (source)
        {
            case "Number" or "Currency" or "Percent" or "Rating":
                if (!TryDecimal(raw, out var n)) return null;
                minutes = n * DurationText.MinutesPerDisplayUnit(display);
                break;
            case "Text" or "TextMultiLine" or "SingleSelect" or "RichText":
                var text = source == "RichText" ? HtmlText.ToPlainText(AsString(raw)) : AsString(raw);
                if (!DurationText.TryParseMinutes(text, display, out minutes)) return null;
                break;
            default:
                return null;
        }
        var whole = Math.Round(minutes, MidpointRounding.AwayFromZero);
        return whole is >= int.MinValue and <= int.MaxValue ? (long)whole : null;
    }

    // ── → Email / Phone / Url / User ─────────────────────────────────────────

    private static string? ToEmail(object raw, string source, FieldValueConversionContext context)
    {
        if (source == "User")
            return TryUserId(raw, out var id) && context.UserEmailsById.TryGetValue(id, out var email) ? email : null;
        var text = ToText(raw, source, context, singleLine: true);
        if (source == "RichText" && text != null) text = HtmlText.ToPlainText(text);
        return text != null && ContactText.TryExtractEmail(text, out var found) ? found : null;
    }

    private static string? ToPhone(object raw, string source)
    {
        if (source is not ("Text" or "TextMultiLine" or "SingleSelect" or "RichText" or "Number")) return null;
        var text = source switch
        {
            "RichText" => HtmlText.ToPlainText(AsString(raw)),
            "Number" => TryDecimal(raw, out var n) && n == Math.Truncate(n) ? n.ToString("0", Inv) : "",
            _ => AsString(raw),
        };
        if (!ContactText.TryExtractPhone(text, out var number, out var ext)) return null;
        return ext is null
            ? JsonSerializer.Serialize(new { number }, StoreJsonOpts)
            : JsonSerializer.Serialize(new { number, ext }, StoreJsonOpts);
    }

    private static string? ToUrl(object raw, string source, FieldValueConversionContext context)
    {
        if (source is "Boolean" or "File") return null;
        var text = ToText(raw, source, context, singleLine: true);
        if (source == "RichText" && text != null) text = HtmlText.ToPlainText(text);
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }

    /// <summary>User values are stored as the user's numeric Id (as text). Only this app's users match.</summary>
    private static string? ToUser(object raw, string source, FieldValueConversionContext context)
    {
        if (source is not ("Text" or "TextMultiLine" or "SingleSelect" or "RichText" or "Email")) return null;
        var text = source == "RichText" ? HtmlText.ToPlainText(AsString(raw)) : AsString(raw);
        if (!ContactText.TryExtractEmail(text, out var email)) return null;
        return context.UserIdsByEmail.TryGetValue(email, out var id) ? id.ToString(Inv) : null;
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private static bool IsBlank(object? raw) =>
        raw is null || raw is DBNull || raw is string s && string.IsNullOrWhiteSpace(s);

    private static string AsString(object raw) => raw switch
    {
        string s => s,
        IFormattable f => f.ToString(null, Inv),
        _ => raw.ToString() ?? "",
    };

    private static bool TryDecimal(object raw, out decimal value)
    {
        switch (raw)
        {
            case decimal m: value = m; return true;
            case double or float or int or long or short or byte:
                value = System.Convert.ToDecimal(raw, Inv); return true;
            case bool b: value = b ? 1 : 0; return true;
            default:
                return decimal.TryParse(AsString(raw).Trim(), NumberStyles.Number, Inv, out value);
        }
    }

    private static bool TryBool(object raw, out bool value)
    {
        switch (raw)
        {
            case bool b: value = b; return true;
            case int or long or short or byte or decimal:
                var n = System.Convert.ToDecimal(raw, Inv);
                value = n != 0;
                return n is 0 or 1;
            default:
                return BooleanText.TryParse(AsString(raw), out value);
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
                return DateTime.TryParse(AsString(raw), Inv, DateTimeStyles.None, out value);
        }
    }

    /// <summary>Duration values arrive as stored minutes (number) or, from a formula, as a TimeSpan.</summary>
    private static bool TryDurationMinutes(object raw, out decimal minutes)
    {
        if (raw is TimeSpan ts)
        {
            minutes = (decimal)ts.TotalMinutes;
            return true;
        }
        return TryDecimal(raw, out minutes);
    }

    private static bool TryUserId(object raw, out long id)
    {
        if (raw is long l) { id = l; return true; }
        if (raw is int i) { id = i; return true; }
        var text = AsString(raw).Trim();
        // Some reads hand back the resolved "{id}|{name}" pair.
        var pipe = text.IndexOf('|');
        if (pipe > 0) text = text[..pipe];
        return long.TryParse(text, NumberStyles.Integer, Inv, out id);
    }

    private static string TimeString(object raw) => raw switch
    {
        TimeSpan t => t.ToString(@"hh\:mm\:ss", Inv),
        TimeOnly t => t.ToString("HH:mm:ss", Inv),
        DateTime d => d.ToString("HH:mm:ss", Inv),
        _ => AsString(raw),
    };

    /// <summary>Phone is stored as JSON {"number","ext"} (plain text for older rows / formulas).</summary>
    private static string? PhoneText(string stored)
    {
        try
        {
            using var doc = JsonDocument.Parse(stored);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return stored;
            var number = doc.RootElement.TryGetProperty("number", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
            var ext = doc.RootElement.TryGetProperty("ext", out var e) && e.ValueKind == JsonValueKind.String ? e.GetString() : null;
            if (string.IsNullOrWhiteSpace(number)) return null;
            return string.IsNullOrWhiteSpace(ext) ? number : $"{number} x{ext}";
        }
        catch (JsonException)
        {
            return stored;
        }
    }

    /// <summary>File is stored as a JSON {name,…} object or an array of them.</summary>
    private static string? FileNames(string stored)
    {
        try
        {
            using var doc = JsonDocument.Parse(stored);
            var names = new List<string>();
            void Add(JsonElement item)
            {
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String)
                    names.Add(n.GetString()!);
            }
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
                foreach (var item in doc.RootElement.EnumerateArray()) Add(item);
            else
                Add(doc.RootElement);
            return names.Count == 0 ? null : string.Join(", ", names);
        }
        catch (JsonException)
        {
            return stored;
        }
    }

    private static string? DurationDisplay(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try { return JsonSerializer.Deserialize<DurationSettings>(settingsJson, JsonOpts)?.Display; }
        catch (JsonException) { return null; }
    }
}
