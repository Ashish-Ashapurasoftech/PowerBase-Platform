using System.Globalization;

namespace PowerBase.Application.Fields;

/// <summary>
/// Which value types a Snapshot field can capture from which source, and how the value is converted. Works on the
/// <em>value type</em> of the source (a Lookup's saved result type, see <c>FieldValueTypes.Of</c>), so a Lookup of a Duration
/// is a Duration. Types are grouped into families: the text family (Text, Multi-line text, Rich text, Email, Phone, URL) and
/// the numeric family (Number, Currency, Percent, Rating) capture within themselves; everything else only from its own type.
/// </summary>
public static class SnapshotTypeCompatibility
{
    private static readonly HashSet<string> TextFamily = new(StringComparer.OrdinalIgnoreCase)
        { "Text", "TextMultiLine", "RichText", "Email", "Phone", "Url" };

    private static readonly HashSet<string> NumericFamily = new(StringComparer.OrdinalIgnoreCase)
        { "Number", "Currency", "Percent", "Rating" };

    /// <summary>The types a Snapshot can be set on: Text, Numeric, Date, Date/Time, Time of Day, Duration, Checkbox,
    /// Phone, Email, URL, User and List-User (Multi-user).</summary>
    private static readonly HashSet<string> Destinations = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "TextMultiLine", "RichText", "Email", "Phone", "Url",
        "Number", "Currency", "Percent", "Rating",
        "Date", "DateTime", "Time", "Duration",
        "Boolean",
        "User", "MultiUser",
    };

    public static bool IsSupportedDestination(string typeCode) => Destinations.Contains(typeCode);

    /// <summary>True when a value of <paramref name="sourceType"/> can be captured into a <paramref name="destType"/> field.</summary>
    public static bool IsCompatible(string? sourceType, string destType)
    {
        if (string.IsNullOrWhiteSpace(sourceType) || !IsSupportedDestination(destType)) return false;
        if (string.Equals(sourceType, destType, StringComparison.OrdinalIgnoreCase)) return true;

        if (TextFamily.Contains(sourceType) && TextFamily.Contains(destType)) return true;
        if (NumericFamily.Contains(sourceType) && NumericFamily.Contains(destType)) return true;
        // A number, or the text of the option chosen in a single-select, reads fine as plain text.
        if ((NumericFamily.Contains(sourceType) || sourceType.Equals("SingleSelect", StringComparison.OrdinalIgnoreCase))
            && destType is "Text" or "TextMultiLine") return true;
        // A date carries no time, so it widens to a date-time (midnight) without losing anything.
        if (sourceType.Equals("Date", StringComparison.OrdinalIgnoreCase) && destType.Equals("DateTime", StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Converts a captured value (already unwrapped from JSON) to the destination's stored form. A value that cannot be
    /// converted becomes blank — the capture never fails a record create.</summary>
    public static object? Convert(object value, string sourceType, string destType)
    {
        if (NumericFamily.Contains(sourceType) && NumericFamily.Contains(destType))
        {
            if (!TryDecimal(value, out var number)) return null;
            // Percent is stored as a whole number (115 = 115%); every other numeric type as the raw quantity (1.15).
            var fromPercent = sourceType.Equals("Percent", StringComparison.OrdinalIgnoreCase);
            var toPercent = destType.Equals("Percent", StringComparison.OrdinalIgnoreCase);
            if (fromPercent && !toPercent) return number / 100m;
            if (toPercent && !fromPercent) return number * 100m;
            return number;
        }
        if (NumericFamily.Contains(sourceType) && destType is "Text" or "TextMultiLine")
        {
            if (!TryDecimal(value, out var number)) return null;
            return number.ToString("0.############################", CultureInfo.InvariantCulture);
        }
        return value;
    }

    private static bool TryDecimal(object value, out decimal number)
    {
        number = 0;
        try
        {
            number = value is string s ? decimal.Parse(s, NumberStyles.Any, CultureInfo.InvariantCulture) : System.Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception e) when (e is FormatException or InvalidCastException or OverflowException) { return false; }
    }
}
