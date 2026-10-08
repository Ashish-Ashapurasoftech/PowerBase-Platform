using System.Globalization;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Imports;

/// <summary>Type-safety rules for what an import may put into a field. Only safe implicit casts are allowed: same kind,
/// number → text, text → number. Everything else must be exactly the same field type.</summary>
public static class ImportTypeCompatibility
{
    private enum Kind { Text, Number, Boolean, Other }

    /// <summary>Field types the import engine cannot write yet (values need resolving or multi-column storage).</summary>
    private static readonly HashSet<string> UnsupportedDestination = new(StringComparer.OrdinalIgnoreCase)
    {
        "Reference", "User", "MultiUser", "File", "Address", "DateRange", "NumericRange"
    };

    public static bool IsWritable(AppField f) =>
        f.Fid.HasValue && !f.IsDeleted && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)
        && (!f.IsSystem || IsRecordId(f)) && !UnsupportedDestination.Contains(f.TypeCode);

    public static bool IsRecordId(AppField f) =>
        f.IsSystem && string.Equals(f.PhysicalColumnName, "Id", StringComparison.OrdinalIgnoreCase);

    /// <summary>A merge key must be unique (or the Record ID#) on the destination.</summary>
    public static bool IsKeyCandidate(AppField f) => f.IsUnique || f.IsPrimary || IsRecordId(f);

    public static string DisplayName(AppField f) => string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label;

    /// <summary>Null when a source field may be mapped into the destination; otherwise the reason it is blocked.</summary>
    public static string? CheckMapping(AppField source, AppField destination)
    {
        var from = KindOf(source);
        return Allowed(from, KindOf(destination))
            ? null
            : $"'{DisplayName(source)}' ({source.TypeCode}) cannot be imported into '{DisplayName(destination)}' ({destination.TypeCode}).";
    }

    /// <summary>Null when a formula's result may be written to the destination; otherwise the reason it cannot. Only the
    /// result types an import can write are accepted; a formula returning a user, a duration or a list is blocked.</summary>
    public static string? CheckFormulaResult(FormulaType resultType, AppField destination)
    {
        // A formula that is just the blank literal can go anywhere: it produces no value.
        if (resultType == FormulaType.Null) return null;
        var code = resultType switch
        {
            FormulaType.Text => "Text",
            FormulaType.Number => "Number",
            FormulaType.Bool => "Boolean",
            FormulaType.Date => "Date",
            FormulaType.DateTime => "DateTime",
            _ => null
        };
        if (code is null) return $"A formula that returns {resultType} cannot be imported yet. Make it return text, a number, a checkbox, a date or a date and time.";
        return Allowed(KindOfCode(code), KindOf(destination))
            ? null
            : $"The formula returns {code}, which cannot be imported into '{DisplayName(destination)}' ({destination.TypeCode}).";
    }

    /// <summary>Parses the fixed value typed for a field into the type that field stores. The same value is then written to
    /// every row, so a value the field cannot hold is refused when the import is saved, not row by row.</summary>
    public static bool TryParseStatic(string text, AppField destination, out object? value, out string? error)
    {
        value = null;
        error = null;
        var (kind, code) = KindOf(destination);
        switch (kind)
        {
            case Kind.Text:
                if (destination.MaxLength is > 0 && text.Length > destination.MaxLength)
                {
                    error = $"The fixed value is {text.Length} characters long, but '{DisplayName(destination)}' holds at most {destination.MaxLength}.";
                    return false;
                }
                value = text;
                return true;
            case Kind.Number:
                if (ImportNumber.TryParse(text, out var number)) { value = number; return true; }
                error = $"'{text}' is not a number. Use digits, with a '.' for decimals and no other punctuation.";
                return false;
            case Kind.Boolean:
                switch (text.Trim().ToLowerInvariant())
                {
                    case "true" or "yes" or "1": value = true; return true;
                    case "false" or "no" or "0": value = false; return true;
                    default: error = $"'{text}' is not a checkbox value. Use true or false."; return false;
                }
        }
        if (code == "Date")
        {
            if (DateTime.TryParseExact(text.Trim(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) { value = date; return true; }
            error = $"'{text}' is not a date. Use the form 2026-03-09.";
            return false;
        }
        if (code == "DateTime")
        {
            if (DateTime.TryParse(text.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dateTime)) { value = dateTime; return true; }
            error = $"'{text}' is not a date and time. Use the form 2026-03-09 14:30.";
            return false;
        }
        error = $"A fixed value is not available for {destination.TypeCode} fields.";
        return false;
    }

    /// <summary>Converts a value to the destination's type. Returns false with an error message when the value cannot be
    /// converted safely (the row is then reported, never written as NULL). A blank stays blank.</summary>
    public static bool TryConvert(object? value, AppField destination, out object? result, out string? error)
    {
        result = null;
        error = null;
        if (value is null or DBNull) return true;

        switch (KindOf(destination).Kind)
        {
            case Kind.Number:
                if (value is byte or short or int or long or float or double or decimal) { result = value; return true; }
                if (value is string blank && string.IsNullOrWhiteSpace(blank)) return true; // an empty text cell is a blank, not a bad number
                if (value is string s && ImportNumber.TryParse(s, out var d)) { result = d; return true; }
                error = $"'{Convert.ToString(value, CultureInfo.InvariantCulture)}' is not a valid number for '{DisplayName(destination)}'.";
                return false;
            case Kind.Text:
                result = Convert.ToString(value, CultureInfo.InvariantCulture);
                return true;
            case Kind.Boolean when value is string flag:
                // A file (or a formula) hands over a checkbox as text.
                switch (flag.Trim().ToLowerInvariant())
                {
                    case "": return true;
                    case "true" or "yes" or "y" or "1": result = true; return true;
                    case "false" or "no" or "n" or "0": result = false; return true;
                    default: error = $"'{flag}' is not a checkbox value for '{DisplayName(destination)}'. Use true or false."; return false;
                }
            default:
                // A date or date-time calculated by a formula arrives as ISO text: store a real date, not text the database parses
                // in whatever culture it happens to run in.
                if (value is string text && KindOf(destination).Code is "Date" or "DateTime")
                {
                    if (string.IsNullOrWhiteSpace(text)) return true;
                    if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
                    {
                        result = parsed;
                        return true;
                    }
                    error = $"'{text}' is not a valid date for '{DisplayName(destination)}'.";
                    return false;
                }
                result = value;
                return true;
        }
    }

    private static bool Allowed((Kind Kind, string Code) from, (Kind Kind, string Code) to)
    {
        var sameKind = from.Kind == to.Kind && from.Kind != Kind.Other;
        var safeCast = (from.Kind, to.Kind) is (Kind.Number, Kind.Text) or (Kind.Text, Kind.Number);
        // A single-select's value is the text of the option chosen, so it goes into a text field as it is.
        var optionToText = from.Kind == Kind.Other && string.Equals(from.Code, "SingleSelect", StringComparison.OrdinalIgnoreCase) && to.Kind == Kind.Text;
        return sameKind || safeCast || optionToText || (from.Kind == Kind.Other && from.Code == to.Code);
    }

    private static (Kind Kind, string Code) KindOf(AppField f)
    {
        var code = f.TypeCode;
        if (PhysicalNaming.IsComputedTypeCode(code))
        {
            var t = FormulaTypeMap.FieldType(code, f.Settings)?.ToString();
            if (t is not null) code = t == "Bool" ? "Boolean" : t;
        }
        return KindOfCode(code);
    }

    private static (Kind Kind, string Code) KindOfCode(string code) => code.ToUpperInvariant() switch
    {
        "NUMBER" or "CURRENCY" or "PERCENT" or "RATING" => (Kind.Number, code),
        "TEXT" or "TEXTMULTILINE" or "RICHTEXT" or "EMAIL" or "PHONE" or "URL" => (Kind.Text, code),
        "BOOLEAN" => (Kind.Boolean, code),
        _ => (Kind.Other, code)
    };
}
