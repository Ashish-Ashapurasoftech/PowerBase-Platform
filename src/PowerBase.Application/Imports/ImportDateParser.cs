using System.Globalization;
using System.Text.RegularExpressions;

namespace PowerBase.Application.Imports;

/// <summary>Reads a date from the text of a file cell in whatever form the file has it, so an import never refuses a date only for how it is written. Where
/// the order is ambiguous (05-04-2026) the app's own date format decides, as it does for a date typed into a record; a date that cannot be that order (25-04-2026
/// under month-first) is read the other way round. The value read is a real date: it is stored as one and shown in the app's format, never kept as text.
/// <para>Understood, in this order: ISO (2026-04-05, 2026-04-05T14:30:00Z, 2026-04-05 14:30); numbers with / - . between them in the app's order, year
/// first or last, a two-digit year meaning 20yy; month names (5-Apr-2026, 5 April 2026, April 5, 2026), each with an optional time (14:30, 2:30 PM).</para></summary>
public static class ImportDateParser
{
    private static readonly string[] Iso =
    [
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss",
        "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.fK", "yyyy-MM-ddTHH:mm:ss.ffK", "yyyy-MM-ddTHH:mm:ss.fffK",
        "yyyy-MM-ddTHH:mm:ss.ffffK", "yyyy-MM-ddTHH:mm:ss.fffffK", "yyyy-MM-ddTHH:mm:ss.ffffffK", "yyyy-MM-ddTHH:mm:ss.fffffffK",
        "yyyy-MM-ddTHH:mm:ss.f", "yyyy-MM-ddTHH:mm:ss.ff", "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss.ffff", "yyyy-MM-ddTHH:mm:ss.fffff",
        "yyyy-MM-ddTHH:mm:ss.ffffff", "yyyy-MM-ddTHH:mm:ss.fffffff",
    ];

    private static readonly string[] Named =
    [
        "d-MMM-yyyy", "dd-MMM-yyyy", "d-MMM-yy", "dd-MMM-yy", "d MMM yyyy", "dd MMM yyyy", "d MMMM yyyy", "dd MMMM yyyy", "d MMM yy",
        "d/MMM/yyyy", "dd/MMM/yyyy", "d.MMM.yyyy", "MMM d, yyyy", "MMM d yyyy", "MMMM d, yyyy", "MMMM d yyyy", "MMM-dd-yyyy", "MMM dd, yyyy",
        "ddd, d MMM yyyy", "ddd, dd MMM yyyy", "dddd, d MMMM yyyy", "dddd, MMMM d, yyyy", "yyyy-MMM-dd", "yyyy MMM d",
    ];

    private static readonly string[] Times = ["", " H:mm", " HH:mm", " H:mm:ss", " HH:mm:ss", " h:mm tt", " hh:mm tt", " h:mm:ss tt", " hh:mm:ss tt", " h:mmtt", " hh:mmtt"];

    private static readonly Regex Numeric = new(
        @"^(?<a>\d{1,4})[/.\-](?<b>\d{1,2})[/.\-](?<c>\d{1,4})(?:[ T](?<time>\d{1,2}:\d{2}(?::\d{2})?(?:\.\d+)?\s*(?:[AaPp][Mm])?))?\s*(?<zone>Z)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <param name="appFormat">The app's date format ("MM-DD-YYYY", "DD-MM-YYYY", "YYYY-MM-DD"...), or null for the default month-first.</param>
    /// <param name="withTime">A date and time keeps its time (and is UTC); a date drops it.</param>
    public static bool TryParse(string? text, string? appFormat, bool withTime, out DateTime result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(text)) return false;
        text = text.Trim();

        if (DateTime.TryParseExact(text, Iso, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var iso))
            return Done(iso, withTime, out result);

        if (TryNumeric(text, appFormat, out var numeric)) return Done(numeric, withTime, out result);

        if (text.Any(char.IsLetter))
        {
            foreach (var time in Times)
            {
                var formats = time.Length == 0 ? Named : Named.Select(f => f + time).ToArray();
                if (DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal | DateTimeStyles.AllowWhiteSpaces, out var named))
                    return Done(named, withTime, out result);
            }
            if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var loose))
                return Done(loose, withTime, out result);
        }
        return false;
    }

    private static bool Done(DateTime value, bool withTime, out DateTime result)
    {
        result = withTime ? DateTime.SpecifyKind(value, DateTimeKind.Utc) : value.Date;
        return true;
    }

    private static bool TryNumeric(string text, string? appFormat, out DateTime result)
    {
        result = default;
        var m = Numeric.Match(text);
        if (!m.Success) return false;
        var a = m.Groups["a"].Value;
        var b = int.Parse(m.Groups["b"].Value, CultureInfo.InvariantCulture);
        var c = m.Groups["c"].Value;

        if (a.Length == 4)
        {
            // Year first: 2026/04/05. The month comes before the day, unless that cannot be a date.
            if (c.Length > 2) return false;
            var year = int.Parse(a, CultureInfo.InvariantCulture);
            var last = int.Parse(c, CultureInfo.InvariantCulture);
            if (!TryDate(year, b, last, out result) && !TryDate(year, last, b, out result)) return false;
        }
        else
        {
            // Year last: 05-04-2026 or 05-04-26. The app's format says which of the first two numbers is the day; when that cannot be a date, the other way.
            if (c.Length is not (2 or 4) || a.Length > 2) return false;
            var year = int.Parse(c, CultureInfo.InvariantCulture) + (c.Length == 2 ? 2000 : 0);
            var first = int.Parse(a, CultureInfo.InvariantCulture);
            var dayFirst = DayFirst(appFormat);
            var ok = dayFirst
                ? TryDate(year, b, first, out result) || TryDate(year, first, b, out result)
                : TryDate(year, first, b, out result) || TryDate(year, b, first, out result);
            if (!ok) return false;
        }

        if (m.Groups["time"].Success)
        {
            if (!DateTime.TryParse("2000-01-01 " + m.Groups["time"].Value.Trim(), CultureInfo.InvariantCulture, DateTimeStyles.None, out var t)) return false;
            result = result.Date + t.TimeOfDay;
        }
        return true;
    }

    private static bool TryDate(int year, int month, int day, out DateTime result)
    {
        result = default;
        if (year < 1 || year > 9999 || month is < 1 or > 12 || day is < 1 or > 31) return false;
        try { result = new DateTime(year, month, day); return true; }
        catch (ArgumentOutOfRangeException) { return false; }
    }

    /// <summary>Whether the app writes the day before the month ("DD-MM-YYYY", "DD/MM/YY"): DD comes before MM in the format.</summary>
    private static bool DayFirst(string? appFormat)
    {
        if (string.IsNullOrWhiteSpace(appFormat)) return false;
        var day = appFormat.IndexOf("DD", StringComparison.OrdinalIgnoreCase);
        var month = appFormat.IndexOf("MM", StringComparison.OrdinalIgnoreCase);
        return day >= 0 && month >= 0 && day < month;
    }
}
