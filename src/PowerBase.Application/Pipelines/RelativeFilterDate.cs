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
}
