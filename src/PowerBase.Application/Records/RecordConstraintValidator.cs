using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Records;

/// <summary>
/// Enforces field-level Required, Unique, and format (Max Length / Regex Validation) constraints
/// at record write time, mirroring Quickbase's behavior: a required field can't be left blank, a
/// unique field can't collide with another record's value, and Text/TextMultiLine/Numeric-family
/// values must fit the length/pattern configured in the field's Behavior Settings. Runs against the
/// effective (post-default, post-reference-override) values a write is about to persist — shared by
/// CreateRecordCommandHandler, RecordWriteService (record edits + Action Button writes), and
/// MassUpdateRecordsCommandHandler so every write path enforces identical rules against the same
/// source of truth (meta.AppField.IsRequired/IsUnique/Settings) — never hard-coded, and never
/// bypassable by a client that skips the UI and calls the API directly.
/// </summary>
public static class RecordConstraintValidator
{
    /// <param name="isCreate">On create, a Required field missing from <paramref name="effectiveValues"/>
    /// entirely (no submitted value, no default) is itself a violation. On update, only fields actually
    /// present in the write are checked — untouched fields are left alone.</param>
    /// <param name="excludeRecordId">The record's internal row Id, excluded from Unique collision checks
    /// so a record doesn't collide with its own current value. Null on create.</param>
    /// <param name="appDateFormat">The owning App's configured Date Formatting (e.g. "DD-MM-YYYY"),
    /// used to validate Date/DateTime text values written by a client that bypasses the UI's own
    /// format-aware input. Optional/trailing so existing callers/tests compile unchanged; defaults
    /// to "MM-DD-YYYY" (see <see cref="ValidateFormat"/>) when omitted.</param>
    public static async Task ValidateAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<long, object?> effectiveValues,
        IRecordRepository recordRepo,
        bool isCreate,
        long? excludeRecordId,
        CancellationToken ct,
        string? appDateFormat = null)
    {
        var violations = await CollectViolationsAsync(table, fields, effectiveValues, recordRepo, isCreate, excludeRecordId, ct, appDateFormat: appDateFormat);
        if (violations.Count > 0)
            // Grouped (not a plain ToDictionary) because a single field can now fail more than one
            // check in the same write (e.g. both Unique and Max Length) — a flat ToDictionary would
            // throw "an item with the same key has already been added" the first time that happened.
            throw new ValidationException(
                violations.GroupBy(v => v.FieldId.ToString())
                    .ToDictionary(g => g.Key, g => g.Select(v => v.Message).ToArray()));
    }

    /// <summary>Same rules as <see cref="ValidateAsync"/>, but collects every violation instead of
    /// throwing on the first — used by callers that need to validate several records before deciding
    /// whether to write anything (e.g. mass update's all-or-nothing pre-flight).</summary>
    /// <param name="recordId">Echoed back on each violation for the caller's own reporting; not used
    /// for lookup. Defaults to <see cref="Guid.Empty"/> for single-record callers that don't need it.</param>
    public static async Task<List<RecordConstraintViolation>> CollectViolationsAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        IReadOnlyDictionary<long, object?> effectiveValues,
        IRecordRepository recordRepo,
        bool isCreate,
        long? excludeRecordId,
        CancellationToken ct,
        Guid recordId = default,
        string? appDateFormat = null)
    {
        var violations = new List<RecordConstraintViolation>();

        foreach (var field in fields)
        {
            if (field.IsSystem || !field.Fid.HasValue || PhysicalNaming.IsComputedTypeCode(field.TypeCode))
                continue;

            var fid = (long)field.Fid.Value;
            var label = field.Label ?? field.Name;

            if (!effectiveValues.TryGetValue(fid, out var value))
            {
                if (isCreate && field.IsRequired)
                    violations.Add(new RecordConstraintViolation(recordId, fid, "Required", $"'{label}' is required."));
                continue;
            }

            var isBlank = value is null || (value is string s && string.IsNullOrWhiteSpace(s));

            if (field.IsRequired && isBlank)
            {
                violations.Add(new RecordConstraintViolation(recordId, fid, "Required", $"'{label}' is required."));
                continue; // No point checking uniqueness of a value we just rejected as blank.
            }

            if (field.IsUnique && !isBlank && !PhysicalNaming.IsRangeTypeCode(field.TypeCode))
            {
                if (await recordRepo.HasValueDuplicateAsync(table, field, value!, excludeRecordId, ct))
                    violations.Add(new RecordConstraintViolation(recordId, fid, "Unique", $"'{label}' must be unique — this value is already in use."));
            }

            if (!isBlank)
            {
                var formatViolation = ValidateFormat(field, fid, label, value!, recordId, appDateFormat);
                if (formatViolation is not null)
                    violations.Add(formatViolation);
            }
        }

        return violations;
    }

    private static readonly JsonSerializerOptions SettingsJsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// Enforces the Text/TextMultiLine "Max Length" and "Regex Validation" Behavior Settings, and
    /// the Number/Currency/Percent/Rating family's "Regex Validation" — these were previously
    /// UI-only (a client-side Angular Validators.maxLength/pattern with no backend counterpart, per
    /// the settings panel's own "(UI only)"/"API accepts any value" notices), so a request that
    /// bypassed the form could persist a value the UI would have rejected outright. Min/Max on the
    /// numeric family stays UI-only by design — only the two format constraints are enforced here.
    /// </summary>
    /// <summary>ISO 8601 patterns accepted ahead of the app's configured display format — every
    /// one starts with a 4-digit year, so none can ever be mistaken for an ambiguous 2-digit
    /// month/day-first date like "05-04-2026".</summary>
    private static readonly string[] IsoDateFormats =
    {
        "yyyy-MM-dd",
        "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssK",
        "yyyy-MM-ddTHH:mm:ss.fK", "yyyy-MM-ddTHH:mm:ss.ffK", "yyyy-MM-ddTHH:mm:ss.fffK",
        "yyyy-MM-ddTHH:mm:ss.ffffK", "yyyy-MM-ddTHH:mm:ss.fffffK", "yyyy-MM-ddTHH:mm:ss.ffffffK", "yyyy-MM-ddTHH:mm:ss.fffffffK",
    };

    private static RecordConstraintViolation? ValidateFormat(AppField field, long fid, string label, object value, Guid recordId, string? appDateFormat = null)
    {
        switch (field.TypeCode)
        {
            case "Date":
            case "DateTime":
            {
                // Already a typed date (e.g. built internally rather than deserialized from a
                // JSON API request) — nothing ambiguous to parse.
                if (value is DateTime || value is DateOnly) return null;
                var text = value as string;
                if (string.IsNullOrWhiteSpace(text)) return null;

                // 1) ISO 8601 first — the UI's own JSON serialization of a picked date is ISO
                //    regardless of the app's configured *display* format, so it must never be
                //    rejected just for not matching that format. Deliberately TryParseExact
                //    against these specific patterns (not a loose DateTime.TryParse) — a loose
                //    parse under InvariantCulture also accepts ambiguous "M-d-yyyy"-shaped text
                //    like "05-04-2026", which would silently defeat step 2's app-format-aware
                //    day/month parsing below for exactly the ambiguous case it exists to fix.
                //    Every real ISO string starts with a 4-digit year, so this can never collide
                //    with a 2-digit-month/day-first canonical format.
                if (DateTime.TryParseExact(text, IsoDateFormats, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _))
                    return null;

                // 2) The app's configured format, accepting either '/' or '-' as the separator —
                //    parsed explicitly against THAT format (never the ambient/invariant culture),
                //    which is what fixes the day/month-swap bug: invariant-culture DateTime.TryParse
                //    assumes MM/dd/yyyy-ish ordering regardless of the app's actual DD-MM-YYYY
                //    setting, so a direct API caller's "05-04-2026" could silently persist as May 4
                //    instead of April 5. Mirrors AppFormattingService.parseStrict on the frontend —
                //    keep both in sync if the algorithm changes.
                var fmt = string.IsNullOrWhiteSpace(appDateFormat) ? "MM-DD-YYYY" : appDateFormat;
                if (TryParseAppFormat(text, fmt, out _)) return null;

                return new RecordConstraintViolation(recordId, fid, "InvalidDate", $"'{label}' must be a valid date in {fmt} format.");
            }

            case "Text":
            case "TextMultiLine":
            {
                var validation = ParseSettings<TextSettings>(field.Settings)?.Validation;
                if (validation is null) return null;
                var text = value as string ?? value.ToString() ?? string.Empty;

                if (validation.MaxLength is int maxLength && text.Length > maxLength)
                    return new RecordConstraintViolation(recordId, fid, "MaxLength", $"'{label}' must be {maxLength} characters or fewer.");

                if (!string.IsNullOrEmpty(validation.Regex) && !TryIsMatch(validation.Regex, text))
                    return new RecordConstraintViolation(recordId, fid, "Pattern", $"'{label}' has an invalid format.");

                return null;
            }

            case "Number":
            case "Currency":
            case "Percent":
            case "Rating":
            {
                var regex = ParseSettings<NumericSettings>(field.Settings)?.Validation?.Regex;
                if (string.IsNullOrEmpty(regex)) return null;
                var text = Convert.ToString(value, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
                return TryIsMatch(regex, text)
                    ? null
                    : new RecordConstraintViolation(recordId, fid, "Pattern", $"'{label}' has an invalid format.");
            }

            default:
                return null;
        }
    }

    private static T? ParseSettings<T>(string? json) where T : class
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<T>(json, SettingsJsonOptions); }
        catch (JsonException) { return null; }
    }

    /// <summary>A malformed regex saved in Settings (the settings panel takes free text, so this
    /// isn't otherwise validated) must never fail-closed and block every write to the field —
    /// treated as "no constraint", the same fallback the frontend's try/catch around
    /// Validators.pattern already uses.</summary>
    private static bool TryIsMatch(string pattern, string input)
    {
        try { return Regex.IsMatch(input, pattern); }
        catch (ArgumentException) { return true; }
    }

    /// <summary>Strictly parses <paramref name="text"/> against a canonical app date format (e.g.
    /// "DD-MM-YYYY", "MM-DD-YY") — accepting EITHER '/' or '-' as the separator regardless of which
    /// one the format itself uses. Mirrors the frontend's AppFormattingService.parseStrict; keep
    /// both in sync. Returns false (never throws) for anything that isn't a real, unambiguous date
    /// in that format — including out-of-range or impossible dates (e.g. "02-30-2026").</summary>
    private static bool TryParseAppFormat(string text, string format, out DateTime result)
    {
        result = default;
        var tokens = format.Split('-', '/');
        var match = Regex.Match(text.Trim(), @"^(\d{1,4})[/-](\d{1,4})[/-](\d{1,4})$");
        if (!match.Success || tokens.Length != 3) return false;

        var parts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < 3; i++)
        {
            if (!int.TryParse(match.Groups[i + 1].Value, out var num)) return false;
            parts[tokens[i].ToUpperInvariant()] = num;
        }

        if (!parts.TryGetValue("MM", out var month) || !parts.TryGetValue("DD", out var day)) return false;
        int year;
        if (parts.TryGetValue("YYYY", out var yyyy)) year = yyyy;
        else if (parts.TryGetValue("YY", out var yy)) year = 2000 + yy;
        else return false;

        if (month is < 1 or > 12 || day is < 1 or > 31 || year < 100) return false;

        try
        {
            var candidate = new DateTime(year, month, day);
            result = candidate;
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            // e.g. day 30 in February — DateTime's constructor itself rejects impossible dates
            // (unlike JS's Date, it never silently rolls over), so this is the round-trip check.
            return false;
        }
    }
}
