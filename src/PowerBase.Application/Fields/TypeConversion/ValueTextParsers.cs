using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using PowerBase.Domain.ValueObjects;

namespace PowerBase.Application.Fields.TypeConversion;

// Text → typed value extractors for Change Type. Each follows the "first occurrence" rule from
// docs/field-type-conversion.md: the first candidate found in the text wins, the rest is ignored, and
// nothing found means no value (never a made-up default such as 0 or 01-01-1970).

/// <summary>Strips HTML so RichText converts by its visible text.</summary>
public static partial class HtmlText
{
    [GeneratedRegex("<[^>]*>")]
    private static partial Regex Tag();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    public static string ToPlainText(string html) =>
        Spaces().Replace(WebUtility.HtmlDecode(Tag().Replace(html, " ")), " ").Trim();
}

public static partial class NumberText
{
    // Optional sign directly before the digits; "1,200" style grouping only with full 3-digit groups.
    [GeneratedRegex(@"(?<![\d.])-?(?:\d{1,3}(?:,\d{3})+|\d+)(?:\.\d+)?|(?<!\d)-?\.\d+")]
    private static partial Regex FirstNumber();

    /// <summary>Largest magnitude a DECIMAL(18,4) column holds.</summary>
    public const decimal MaxStorable = 99_999_999_999_999.9999m;

    /// <summary><c>test12test12</c> → 12, <c>$1,200.50</c> → 1200.50, <c>Mahesh - 4000</c> → 4000, <c>test</c> → none.</summary>
    public static bool TryExtractFirst(string text, out decimal value)
    {
        value = 0;
        var match = FirstNumber().Match(text);
        if (!match.Success) return false;
        if (!decimal.TryParse(match.Value.Replace(",", ""), NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture, out var parsed)) return false;
        return TryFitStorage(parsed, out value);
    }

    /// <summary>Rounds to the column's 4 decimals; false when the value can't be stored at all.</summary>
    public static bool TryFitStorage(decimal number, out decimal value)
    {
        value = Math.Round(number, 4, MidpointRounding.AwayFromZero);
        return Math.Abs(value) <= MaxStorable;
    }
}

public static class BooleanText
{
    /// <summary>true/false, yes/no, 1/0 — case-insensitive, trimmed. Anything else is not a boolean.</summary>
    public static bool TryParse(string text, out bool value)
    {
        switch (text.Trim().ToLowerInvariant())
        {
            case "true" or "yes" or "1": value = true; return true;
            case "false" or "no" or "0": value = false; return true;
            default: value = false; return false;
        }
    }
}

public static partial class DateText
{
    private const string Months = "jan(?:uary)?|feb(?:ruary)?|mar(?:ch)?|apr(?:il)?|may|june?|july?|aug(?:ust)?|sep(?:t(?:ember)?)?|oct(?:ober)?|nov(?:ember)?|dec(?:ember)?";

    [GeneratedRegex(@"(?<!\d)(\d{4})[-/.](\d{1,2})[-/.](\d{1,2})(?!\d)")]
    private static partial Regex IsoDate();

    [GeneratedRegex(@"(?<!\d)(\d{1,2})[-/.](\d{1,2})[-/.](\d{4}|\d{2})(?!\d)")]
    private static partial Regex NumericDate();

    [GeneratedRegex(@"(?<!\d)(\d{1,2})(?:st|nd|rd|th)?\s+(" + Months + @")\.?,?\s+(\d{4})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex DayMonthNameYear();

    [GeneratedRegex(@"\b(" + Months + @")\.?\s+(\d{1,2})(?:st|nd|rd|th)?,?\s+(\d{4})(?!\d)", RegexOptions.IgnoreCase)]
    private static partial Regex MonthNameDayYear();

    [GeneratedRegex(@"^(?:\s*,)?\s*T?\s*(\d{1,2}):(\d{2})(?::(\d{2}))?(?:\.\d+)?\s*([ap]\.?m\.?)?", RegexOptions.IgnoreCase)]
    private static partial Regex TrailingTime();

    /// <summary>First date in <paramref name="text"/>. Numeric dates are read in the app's date order
    /// (MM-DD-YYYY or DD-MM-YYYY), falling back to the other order only when the preferred one is impossible.
    /// With <paramref name="withTime"/>, a time right after the date is kept.</summary>
    public static bool TryExtractFirst(string text, AppFormattingSettings app, bool withTime, out DateTime value)
    {
        var dayFirst = IsDayFirst(app.Date.FormatString);
        var candidates = new List<(int Index, int End, DateTime Date)>();

        foreach (Match m in IsoDate().Matches(text))
            if (TryBuild(Int(m, 1), Int(m, 2), Int(m, 3), out var d)) candidates.Add((m.Index, m.Index + m.Length, d));

        foreach (Match m in NumericDate().Matches(text))
        {
            int a = Int(m, 1), b = Int(m, 2), year = ExpandYear(m.Groups[3].Value);
            var (month, day) = dayFirst ? (b, a) : (a, b);
            if (TryBuild(year, month, day, out var d) || TryBuild(year, day, month, out d))
                candidates.Add((m.Index, m.Index + m.Length, d));
        }

        foreach (Match m in DayMonthNameYear().Matches(text))
            if (TryBuild(Int(m, 3), MonthNumber(m.Groups[2].Value), Int(m, 1), out var d)) candidates.Add((m.Index, m.Index + m.Length, d));

        foreach (Match m in MonthNameDayYear().Matches(text))
            if (TryBuild(Int(m, 3), MonthNumber(m.Groups[1].Value), Int(m, 2), out var d)) candidates.Add((m.Index, m.Index + m.Length, d));

        if (candidates.Count == 0)
        {
            value = default;
            return false;
        }

        var first = candidates.OrderBy(c => c.Index).ThenByDescending(c => c.End).First();
        value = first.Date;
        if (withTime)
        {
            var time = TrailingTime().Match(text[first.End..]);
            if (time.Success && TimeText.TryBuild(Int(time, 1), Int(time, 2), time.Groups[3].Success ? Int(time, 3) : 0,
                    time.Groups[4].Success ? time.Groups[4].Value : null, out var tod))
                value = value.Add(tod);
        }
        return true;
    }

    private static bool IsDayFirst(string? formatString)
    {
        var f = (formatString ?? "").ToUpperInvariant();
        var dayIndex = f.IndexOf("DD", StringComparison.Ordinal);
        var monthIndex = f.IndexOf("MM", StringComparison.Ordinal);
        return dayIndex >= 0 && monthIndex >= 0 && dayIndex < monthIndex;
    }

    private static int ExpandYear(string year) =>
        year.Length == 2 ? (int.Parse(year, CultureInfo.InvariantCulture) is var y && y < 50 ? 2000 + y : 1900 + y)
                         : int.Parse(year, CultureInfo.InvariantCulture);

    private static int MonthNumber(string name) => name[..3].ToLowerInvariant() switch
    {
        "jan" => 1, "feb" => 2, "mar" => 3, "apr" => 4, "may" => 5, "jun" => 6,
        "jul" => 7, "aug" => 8, "sep" => 9, "oct" => 10, "nov" => 11, _ => 12,
    };

    private static bool TryBuild(int year, int month, int day, out DateTime value)
    {
        value = default;
        if (year is < 1 or > 9999 || month is < 1 or > 12 || day < 1 || day > DateTime.DaysInMonth(year, month)) return false;
        value = new DateTime(year, month, day);
        return true;
    }

    private static int Int(Match m, int group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
}

public static partial class TimeText
{
    [GeneratedRegex(@"(?<!\d)(\d{1,2}):(\d{2})(?::(\d{2}))?(?!\d)\s*([ap]\.?m\.?(?![a-z]))?", RegexOptions.IgnoreCase)]
    private static partial Regex ClockTime();

    [GeneratedRegex(@"(?<!\d)(\d{1,2})\s*([ap]\.?m\.?)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex HourOnly();

    /// <summary>First <c>h:mm[:ss] [am/pm]</c> or <c>h am/pm</c> in the text.</summary>
    public static bool TryExtractFirst(string text, out TimeSpan value)
    {
        var candidates = new List<(int Index, TimeSpan Time)>();
        foreach (Match m in ClockTime().Matches(text))
            if (TryBuild(Int(m, 1), Int(m, 2), m.Groups[3].Success ? Int(m, 3) : 0, m.Groups[4].Success ? m.Groups[4].Value : null, out var t))
                candidates.Add((m.Index, t));
        foreach (Match m in HourOnly().Matches(text))
            if (TryBuild(Int(m, 1), 0, 0, m.Groups[2].Value, out var t))
                candidates.Add((m.Index, t));

        if (candidates.Count == 0)
        {
            value = default;
            return false;
        }
        value = candidates.OrderBy(c => c.Index).First().Time;
        return true;
    }

    internal static bool TryBuild(int hour, int minute, int second, string? meridiem, out TimeSpan value)
    {
        value = default;
        if (minute is < 0 or > 59 || second is < 0 or > 59) return false;
        if (meridiem != null)
        {
            if (hour is < 1 or > 12) return false;
            var pm = char.ToLowerInvariant(meridiem[0]) == 'p';
            hour = hour % 12 + (pm ? 12 : 0);
        }
        else if (hour is < 0 or > 23) return false;
        value = new TimeSpan(hour, minute, second);
        return true;
    }

    private static int Int(Match m, int group) => int.Parse(m.Groups[group].Value, CultureInfo.InvariantCulture);
}

public static partial class DurationText
{
    [GeneratedRegex(@"^\s*(-)?(\d+):(\d{1,2})(?::(\d{1,2}))?\s*$")]
    private static partial Regex Clock();

    [GeneratedRegex(@"(-?\d+(?:\.\d+)?)\s*(weeks?|wks?|w|days?|d|hours?|hrs?|h|minutes?|mins?|m|seconds?|secs?|s)(?![a-z])", RegexOptions.IgnoreCase)]
    private static partial Regex UnitPart();

    /// <summary>Minutes per unit of a Duration field's display setting; minutes for clock/Smart displays.</summary>
    public static decimal MinutesPerDisplayUnit(string? display) => display switch
    {
        "Weeks" => 10080m,
        "Days" => 1440m,
        "Hours" => 60m,
        "Seconds" => 1m / 60m,
        _ => 1m,
    };

    /// <summary><c>3 days</c>, <c>2h 30m</c>, <c>1 wk 2 days</c>, <c>1:30</c> (h:mm), <c>1:30:15</c>; a bare number
    /// is read in <paramref name="bareNumberDisplay"/>'s unit. Result in minutes.</summary>
    public static bool TryParseMinutes(string text, string? bareNumberDisplay, out decimal minutes)
    {
        minutes = 0;
        var clock = Clock().Match(text);
        if (clock.Success)
        {
            var hours = decimal.Parse(clock.Groups[2].Value, CultureInfo.InvariantCulture);
            var mins = decimal.Parse(clock.Groups[3].Value, CultureInfo.InvariantCulture);
            var secs = clock.Groups[4].Success ? decimal.Parse(clock.Groups[4].Value, CultureInfo.InvariantCulture) : 0;
            if (mins > 59 || secs > 59) return false;
            minutes = (hours * 60 + mins + secs / 60) * (clock.Groups[1].Success ? -1 : 1);
            return true;
        }

        var parts = UnitPart().Matches(text);
        if (parts.Count > 0)
        {
            foreach (Match part in parts)
                minutes += decimal.Parse(part.Groups[1].Value, CultureInfo.InvariantCulture) * UnitMinutes(part.Groups[2].Value);
            return true;
        }

        if (!NumberText.TryExtractFirst(text, out var bare)) return false;
        minutes = bare * MinutesPerDisplayUnit(bareNumberDisplay);
        return true;
    }

    private static decimal UnitMinutes(string unit) => char.ToLowerInvariant(unit[0]) switch
    {
        'w' => 10080m,
        'd' => 1440m,
        'h' => 60m,
        'm' => 1m,
        _ => 1m / 60m,
    };
}

public static partial class ContactText
{
    [GeneratedRegex(@"[A-Za-z0-9._%+'\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex Email();

    [GeneratedRegex(@"\+?\(?\d[\d\s().\-]*\d(?:\s*(?:x|ext\.?|extension)\s*(\d{1,6}))?", RegexOptions.IgnoreCase)]
    private static partial Regex Phone();

    public static bool TryExtractEmail(string text, out string email)
    {
        var m = Email().Match(text);
        email = m.Success ? m.Value.Trim('.', '\'') : "";
        return m.Success;
    }

    /// <summary>First phone-like run with 7–15 digits, plus an optional x/ext extension.</summary>
    public static bool TryExtractPhone(string text, out string number, out string? ext)
    {
        foreach (Match m in Phone().Matches(text))
        {
            var numberPart = m.Groups[1].Success ? m.Value[..m.Value.LastIndexOf(m.Groups[1].Value, StringComparison.Ordinal)] : m.Value;
            numberPart = Regex.Replace(numberPart, @"\s*(?:x|ext\.?|extension)\s*$", "", RegexOptions.IgnoreCase).Trim();
            var digits = numberPart.Count(char.IsDigit);
            if (digits is < 7 or > 15) continue;
            number = numberPart;
            ext = m.Groups[1].Success ? m.Groups[1].Value : null;
            return true;
        }
        number = "";
        ext = null;
        return false;
    }
}
