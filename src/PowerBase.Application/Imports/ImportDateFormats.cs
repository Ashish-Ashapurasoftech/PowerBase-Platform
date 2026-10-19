using System.Globalization;
using System.Text.RegularExpressions;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Imports;

/// <summary>The date and time formats a person chooses for a column of a file, and the strict reading of a cell in the chosen format. A file's date is only text, and
/// <c>02/10/2026</c> is 2 October or 10 February: only the person knows which, so each date or time column of a mapping is read in the format they chose, never guessed.
/// The date formats are the app settings' own; a time is 12-hour (with AM/PM) or 24-hour.
/// <para>A real Excel date or time cell does not depend on the format: it reaches the import in a machine form (<c>2026-07-30</c>, <c>2026-07-30T16:00:00</c>,
/// <c>16:00:00</c>), which is always read as it is.</para></summary>
public static class ImportDateFormats
{
    public static readonly string[] Date = ["MM-DD-YYYY", "MM-DD-YY", "DD-MM-YYYY", "DD-MM-YY", "YYYY-MM-DD"];
    public static readonly string[] Time = ["hh:mm AM/PM", "hh:mm:ss AM/PM", "HH:mm", "HH:mm:ss"];

    private static readonly string[] MachineDateTime =
    [
        "yyyy-MM-dd", "yyyy-MM-ddTHH:mm", "yyyy-MM-ddTHH:mm:ss", "yyyy-MM-ddTHH:mm:ssK", "yyyy-MM-ddTHH:mm:ss.fffK", "yyyy-MM-ddTHH:mm:ss.fffffffK",
        "yyyy-MM-ddTHH:mm:ss.fff", "yyyy-MM-ddTHH:mm:ss.fffffff", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss",
    ];

    private static readonly Regex MonthWord = new(@"[A-Za-z]{3,}", RegexOptions.Compiled | RegexOptions.CultureInvariant); // AM and PM are two letters

    private static readonly Regex DatePart = new(
        @"^(?<a>\d{1,4})[/.\-](?<b>\d{1,2})[/.\-](?<c>\d{1,4})(?:[ T]+(?<time>.+))?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex Clock = new(
        @"^(?<h>\d{1,2}):(?<m>\d{2})(?::(?<s>\d{2}))?(?:\.\d+)?\s*(?<ap>[AaPp])?\.?(?:[Mm]\.?)?$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsDate(string? format) => format is not null && Array.IndexOf(Date, format) >= 0;
    public static bool IsTime(string? format) => format is not null && Array.IndexOf(Time, format) >= 0;
    private static bool IsTwelveHour(string format) => format.EndsWith("AM/PM", StringComparison.Ordinal);

    /// <summary>The kinds of destination that read a date or a time from text, and so need a format for it.</summary>
    public static (bool Date, bool Time) Needs(AppField destination) => destination.TypeCode switch
    {
        "Date" => (true, false),
        "DateTime" => (true, true),
        "Time" => (false, true),
        _ => (false, false)
    };

    /// <summary>A source whose values are text (a file's column, a text virtual column or field): the only kind that needs a format. A source that is already a date
    /// or a time carries its own value.</summary>
    public static bool IsTextual(AppField source) =>
        source.TypeCode is not ("Date" or "DateTime" or "Time" or "Formula_Date" or "Formula_DateTime" or "Formula_Time");

    /// <summary>What is wrong with the formats chosen for a mapping into <paramref name="destination"/> from <paramref name="source"/>, in words; null when it is fine. A
    /// column read as text must say how its dates and times are written; a format that is not one of the lists is refused.</summary>
    public static string? Problem(string destinationName, AppField destination, AppField? source, string? dateFormat, string? timeFormat, string? timeZone = null)
    {
        var (needsDate, needsTime) = Needs(destination);
        if (!needsDate && !needsTime) return null;
        if (dateFormat is not null && !IsDate(dateFormat)) return $"'{dateFormat}' is not a date format. Choose the date format for '{destinationName}'.";
        if (timeFormat is not null && !IsTime(timeFormat)) return $"'{timeFormat}' is not a time format. Choose the time format for '{destinationName}'.";
        if (timeZone is not null && destination.TypeCode == "DateTime" && ZoneOf(timeZone) is null) return $"'{timeZone}' is not a time zone.";
        if (source is not null && !IsTextual(source)) return null;
        if (needsDate && dateFormat is null) return $"Choose the date format of the column that goes to '{destinationName}'.";
        if (needsTime && timeFormat is null) return $"Choose the time format of the column that goes to '{destinationName}'.";
        return null;
    }

    /// <summary>The time zone with this IANA (or system) id, or null when there is none.</summary>
    public static TimeZoneInfo? ZoneOf(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        try { return TimeZoneInfo.FindSystemTimeZoneById(id.Trim()); }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException) { return null; }
    }

    /// <summary>A date and time is stored in UTC, as one saved from the screen is, and shown in the viewer's zone. The clock reading in the file is in the zone of the
    /// person who set the import up, so converting it from that zone makes it show as written; with no zone it is stored as written, as UTC.</summary>
    private static DateTime ToUtc(DateTime wall, TimeZoneInfo? zone)
    {
        wall = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        if (zone is null) return DateTime.SpecifyKind(wall, DateTimeKind.Utc);
        if (zone.IsInvalidTime(wall)) wall = wall.AddHours(1); // a time the clocks skipped
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(wall, zone), DateTimeKind.Utc);
    }

    /// <summary>Reads a cell as a date (or date and time) in the chosen format. The time of a Date and Time is read in its own format; a Date ignores any time.</summary>
    public static bool TryParseDate(string text, string dateFormat, string? timeFormat, bool withTime, out DateTime result, out string error, TimeZoneInfo? zone = null)
    {
        result = default;
        error = "";
        text = text.Trim();

        if (DateTime.TryParseExact(text, MachineDateTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var machine))
        {
            // A machine form with no zone on it is the clock of the sheet; one with a Z or an offset is an exact moment.
            result = withTime ? (HasZone(text) ? DateTime.SpecifyKind(machine, DateTimeKind.Utc) : ToUtc(machine, zone)) : machine.Date;
            return true;
        }

        // A month written as a word cannot be mistaken for another order of day and month.
        if (MonthWord.IsMatch(text) && ImportDateParser.TryParse(text, dateFormat, withTime, out var named))
        {
            result = withTime ? ToUtc(named, zone) : named;
            return true;
        }

        var m = DatePart.Match(text);
        if (!m.Success)
        {
            error = $"'{text}' is not a date in the format {dateFormat}.";
            return false;
        }
        var tokens = dateFormat.Split('-');
        var parts = new[] { m.Groups["a"].Value, m.Groups["b"].Value, m.Groups["c"].Value };
        int year = 0, month = 0, day = 0;
        for (var i = 0; i < 3; i++)
        {
            var value = parts[i];
            switch (tokens[i])
            {
                case "YYYY": if (value.Length != 4) return Reject(text, dateFormat, out error); year = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "YY": if (value.Length != 2) return Reject(text, dateFormat, out error); year = 2000 + int.Parse(value, CultureInfo.InvariantCulture); break;
                case "MM": if (value.Length > 2) return Reject(text, dateFormat, out error); month = int.Parse(value, CultureInfo.InvariantCulture); break;
                case "DD": if (value.Length > 2) return Reject(text, dateFormat, out error); day = int.Parse(value, CultureInfo.InvariantCulture); break;
            }
        }
        if (month is < 1 or > 12 || day is < 1 or > 31 || year < 1) return Reject(text, dateFormat, out error);
        DateTime date;
        try { date = new DateTime(year, month, day); }
        catch (ArgumentOutOfRangeException) { return Reject(text, dateFormat, out error); }

        var time = TimeSpan.Zero;
        if (withTime && m.Groups["time"].Success)
        {
            if (timeFormat is null) { error = "The time format is missing."; return false; }
            if (!TryParseClock(m.Groups["time"].Value.Trim(), timeFormat, out time, out _, out error)) return false;
        }
        result = withTime ? ToUtc(date + time, zone) : date;
        return true;
    }

    /// <summary>Reads a cell as a time of day in the chosen format and gives it as the Time field stores it: <c>HH:mm</c>, or <c>HH:mm:ss</c> when the format has seconds or
    /// the cell has some.</summary>
    public static bool TryParseTime(string text, string timeFormat, out string result, out string error)
    {
        result = "";
        text = text.Trim();
        // A real Excel date-time cell used for a time: its time of day.
        if (DateTime.TryParseExact(text, MachineDateTime, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var machine))
        {
            result = Canonical(machine.TimeOfDay, timeFormat);
            error = "";
            return true;
        }
        if (!TryParseClock(text, timeFormat, out var time, out _, out error)) return false;
        result = Canonical(time, timeFormat);
        return true;
    }

    private static string Canonical(TimeSpan time, string format)
    {
        var seconds = format.Contains("ss", StringComparison.Ordinal) || time.Seconds != 0;
        return seconds ? $"{time.Hours:00}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Hours:00}:{time.Minutes:00}";
    }

    /// <summary>12-hour: a time with no AM/PM is refused (9 in the morning or the evening?), except the form <c>HH:mm:ss</c> a time cell is shown in. 24-hour: no AM/PM needed; one written is honoured.</summary>
    private static bool TryParseClock(string text, string timeFormat, out TimeSpan time, out bool hadSeconds, out string error)
    {
        time = default;
        hadSeconds = false;
        error = "";
        var m = Clock.Match(text);
        if (!m.Success)
        {
            error = $"'{text}' is not a time in the format {timeFormat}.";
            return false;
        }
        var hour = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        var minute = int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture);
        hadSeconds = m.Groups["s"].Success;
        var second = hadSeconds ? int.Parse(m.Groups["s"].Value, CultureInfo.InvariantCulture) : 0;
        var marker = m.Groups["ap"].Success;
        var twelve = IsTwelveHour(timeFormat);

        // 13:00 to 23:59 and 0:xx can only be 24-hour, so they need no AM/PM; 1:00 to 12:59 with none could be morning or evening.
        if (!marker && twelve && !hadSeconds && hour is >= 1 and <= 12) { error = $"'{text}' has no AM/PM but the time format is {timeFormat}."; return false; }
        if (minute > 59 || second > 59) { error = $"'{text}' is not a valid time."; return false; }
        if (marker)
        {
            // AM or PM written on the cell is never ambiguous, whichever format was chosen: 9:05 PM and 21:05 PM are both 21:05. Only a contradiction is refused (14:00 AM).
            var pm = char.ToUpperInvariant(m.Groups["ap"].Value[0]) == 'P';
            if (hour > 23 || (pm && hour == 0) || (!pm && hour > 12)) { error = $"'{text}' is not a valid time."; return false; }
            if (pm && hour is >= 1 and <= 11) hour += 12;
            else if (!pm && hour == 12) hour = 0;
        }
        else if (hour > 23) { error = $"'{text}' is not a valid time."; return false; }

        time = new TimeSpan(hour, minute, second);
        return true;
    }


    private static readonly Regex ZoneSuffix = new(@"([Zz]|[+-]\d{2}:?\d{2})$", RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private static bool HasZone(string text) => ZoneSuffix.IsMatch(text);

    private static bool Reject(string text, string format, out string error)
    {
        error = $"'{text}' is not a date in the format {format}.";
        return false;
    }
}
