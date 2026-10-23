using System;
using System.Globalization;

namespace PowerBase.Application.Pipelines;

/// <summary>Persist the calendar rule, resolve it in its saved timezone at execution time.</summary>
public static class RelativeFilterDate
{
    public static bool IsRelative(string? value) => value?.StartsWith("@relative-date|", StringComparison.Ordinal) == true;

    public static string Resolve(string value, DateTimeOffset? now = null)
    {
        if (!IsRelative(value)) return value;
        return Window(value, now).Start.ToString("O", CultureInfo.InvariantCulture);
    }

    public static (DateTime Start, DateTime End) Window(string value, DateTimeOffset? now = null)
    {
        var parts = value.Split('|');
        if (parts.Length != 4 || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var days) || days > 365000)
            throw new ArgumentException("Invalid relative date filter.");
        var offset = parts[1] switch
        {
            "today" => 0, "yesterday" => -1, "tomorrow" => 1,
            "past" => -days, "future" => days,
            _ => throw new ArgumentException("Unknown relative date filter mode.")
        };
        var zone = TimeZoneInfo.FindSystemTimeZoneById(parts[3]);
        var day = TimeZoneInfo.ConvertTime(now ?? DateTimeOffset.UtcNow, zone).Date.AddDays(offset);
        return (TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day, DateTimeKind.Unspecified), zone),
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(day.AddDays(1), DateTimeKind.Unspecified), zone));
    }

    /// <summary>"is during" / "is not during" value: <c>@relative-period|current|1|week|India Standard Time</c> — the current, previous
    /// or next N days / weeks / months / quarters / years, as the report filter's "is during" offers them.</summary>
    public static bool IsPeriod(string? value) => value?.StartsWith("@relative-period|", StringComparison.Ordinal) == true;

    /// <summary>The UTC instants a period covers, start included and end excluded, worked out in its saved timezone. Periods follow the
    /// report filter (RunReportQueryHandler.ComputeDuringRange): weeks start on Monday, and the previous / next N periods leave out the
    /// current one.</summary>
    public static (DateTime Start, DateTime End) PeriodWindow(string value, DateTimeOffset? now = null)
    {
        var parts = value.Split('|');
        if (parts.Length != 5 || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count < 1 || count > 10000)
            throw new ArgumentException("Invalid relative period filter.");
        var unit = parts[3];
        if (unit is not ("day" or "week" or "month" or "quarter" or "year"))
            throw new ArgumentException("Unknown relative period unit.");
        var zone = TimeZoneInfo.FindSystemTimeZoneById(parts[4]);
        var today = TimeZoneInfo.ConvertTime(now ?? DateTimeOffset.UtcNow, zone).Date;
        var current = StartOfPeriod(today, unit);
        var (start, end) = parts[1] switch
        {
            "current" => (current, AddPeriods(current, unit, 1)),
            "previous" => (AddPeriods(current, unit, -count), current),
            "next" => (AddPeriods(current, unit, 1), AddPeriods(current, unit, count + 1)),
            _ => throw new ArgumentException("Unknown relative period direction.")
        };
        return (TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(start, DateTimeKind.Unspecified), zone),
            TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(end, DateTimeKind.Unspecified), zone));
    }

    private static DateTime StartOfPeriod(DateTime day, string unit) => unit switch
    {
        "day" => day,
        "month" => new DateTime(day.Year, day.Month, 1),
        "quarter" => new DateTime(day.Year, (day.Month - 1) / 3 * 3 + 1, 1),
        "year" => new DateTime(day.Year, 1, 1),
        _ => day.AddDays(day.DayOfWeek == DayOfWeek.Sunday ? -6 : 1 - (int)day.DayOfWeek)
    };

    private static DateTime AddPeriods(DateTime day, string unit, int amount) => unit switch
    {
        "day" => day.AddDays(amount),
        "month" => day.AddMonths(amount),
        "quarter" => day.AddMonths(amount * 3),
        "year" => day.AddYears(amount),
        _ => day.AddDays(amount * 7)
    };
}
