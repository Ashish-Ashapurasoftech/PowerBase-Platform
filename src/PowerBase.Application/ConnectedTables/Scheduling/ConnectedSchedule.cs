using System.Text.Json;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.ConnectedTables.Scheduling;

public static class ConnectedScheduleTypes
{
    public const string Manual = "manual";
    public const string Monthly = "monthly";
    public const string Weekly = "weekly";
    public const string Daily = "daily";
    public const string Hourly = "hourly";
    public static readonly string[] All = [Manual, Monthly, Weekly, Daily, Hourly];
}

/// <summary>When a connected table refreshes by itself, the way the Refresh step offers it: Manual only, every month on day N,
/// every week on a weekday between H and H+1, every day between H and H+1, or every hour starting between H and H+1 and
/// ending at E. The exact minute inside the hour is picked at random once (<see cref="Minute"/>) to spread the load.
/// Hours are in the table's time zone.</summary>
public sealed class ConnectedSchedule
{
    public string Type { get; set; } = ConnectedScheduleTypes.Manual;
    /// <summary>Monthly: day 1–31 (a shorter month runs on its last day).</summary>
    public int Day { get; set; } = 1;
    /// <summary>Weekly: 0 = Sunday … 6 = Saturday.</summary>
    public int Weekday { get; set; }
    /// <summary>Hour 0–23 the run happens in (hourly: the first hour).</summary>
    public int Hour { get; set; }
    /// <summary>Hourly: the last hour 0–23 (equal to <see cref="Hour"/> means every hour of the day).</summary>
    public int EndHour { get; set; }
    /// <summary>Minute 0–59 inside the hour, set by the server.</summary>
    public int Minute { get; set; }
    /// <summary>False when the schedule was switched to Manual after repeated failures; the other settings are kept so it can be resumed.</summary>
    public bool Enabled { get; set; } = true;

    public bool IsAutomatic => Enabled && Type != ConnectedScheduleTypes.Manual;
}

public static class ConnectedScheduling
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public static ConnectedSchedule? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return JsonSerializer.Deserialize<ConnectedSchedule>(json, JsonOptions); }
        catch (JsonException) { return null; }
    }

    public static string Serialize(ConnectedSchedule schedule) => JsonSerializer.Serialize(schedule);

    /// <summary>Checks a submitted schedule and returns it cleaned up. A new (or re-shaped) schedule gets a fresh random minute;
    /// an unchanged one keeps its minute so saving other settings doesn't move the run time.</summary>
    public static ConnectedSchedule Validate(ConnectedSchedule input, ConnectedSchedule? existing, Random? random = null)
    {
        var type = (input.Type ?? string.Empty).Trim().ToLowerInvariant();
        if (!ConnectedScheduleTypes.All.Contains(type)) throw Invalid("Choose when the table refreshes.");
        if (input.Hour is < 0 or > 23 || input.EndHour is < 0 or > 23) throw Invalid("Hours must be between 0 and 23.");
        if (type == ConnectedScheduleTypes.Monthly && input.Day is < 1 or > 31) throw Invalid("Choose a day of the month between 1 and 31.");
        if (type == ConnectedScheduleTypes.Weekly && input.Weekday is < 0 or > 6) throw Invalid("Choose a day of the week.");

        var cleaned = new ConnectedSchedule
        {
            Type = type,
            Day = type == ConnectedScheduleTypes.Monthly ? input.Day : 1,
            Weekday = type == ConnectedScheduleTypes.Weekly ? input.Weekday : 0,
            Hour = type == ConnectedScheduleTypes.Manual ? 0 : input.Hour,
            EndHour = type == ConnectedScheduleTypes.Hourly ? input.EndHour : 0,
            Enabled = true,
        };
        cleaned.Minute = existing is not null && SameShape(existing, cleaned) ? existing.Minute : (random ?? Random.Shared).Next(0, 60);
        return cleaned;
    }

    public static bool TryResolveTimeZone(string? id, out TimeZoneInfo zone)
    {
        zone = TimeZoneInfo.Utc;
        if (string.IsNullOrWhiteSpace(id)) return false;
        if (string.Equals(id, "UTC", StringComparison.OrdinalIgnoreCase)) return true;
        return TimeZoneInfo.TryFindSystemTimeZoneById(id.Trim(), out zone!);
    }

    /// <summary>The first run strictly after <paramref name="afterUtc"/>, in UTC; null for Manual (or a paused schedule).</summary>
    public static DateTime? NextRun(ConnectedSchedule? schedule, string? timeZone, DateTime afterUtc)
    {
        if (schedule is null || !schedule.IsAutomatic) return null;
        if (!TryResolveTimeZone(timeZone, out var zone)) zone = TimeZoneInfo.Utc;
        var afterLocal = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(afterUtc, DateTimeKind.Utc), zone);

        // Walk forward candidate local times; at most 32 days (monthly) × 24 hours covers every shape.
        var day = afterLocal.Date;
        for (var d = 0; d < 400; d++, day = day.AddDays(1))
        {
            foreach (var hour in HoursOn(schedule, day))
            {
                var local = day.AddHours(hour).AddMinutes(schedule.Minute);
                if (zone.IsInvalidTime(local)) local = local.AddHours(1);   // skipped by a DST jump: run just after it
                var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone);
                if (utc > afterUtc) return DateTime.SpecifyKind(utc, DateTimeKind.Utc);
            }
        }
        return null;
    }

    /// <summary>Plain-words description, for lists and emails.</summary>
    public static string Describe(ConnectedSchedule? schedule, string? timeZone)
    {
        if (schedule is null || schedule.Type == ConnectedScheduleTypes.Manual) return "Manual only";
        var prefix = schedule.Enabled ? "" : "Paused: ";
        var tz = string.IsNullOrWhiteSpace(timeZone) ? "UTC" : timeZone;
        string Window(int h) => $"between {Clock(h)} and {Clock((h + 1) % 24)}";
        return prefix + schedule.Type switch
        {
            ConnectedScheduleTypes.Monthly => $"Every month on day {schedule.Day}, {Window(schedule.Hour)} ({tz})",
            ConnectedScheduleTypes.Weekly => $"Every week on {(DayOfWeek)schedule.Weekday}, {Window(schedule.Hour)} ({tz})",
            ConnectedScheduleTypes.Daily => $"Every day {Window(schedule.Hour)} ({tz})",
            _ => schedule.EndHour == schedule.Hour
                ? $"Every hour ({tz})"
                : $"Every hour from {Clock(schedule.Hour)} to {Clock(schedule.EndHour)} ({tz})",
        };
    }

    private static IEnumerable<int> HoursOn(ConnectedSchedule s, DateTime day)
    {
        switch (s.Type)
        {
            case ConnectedScheduleTypes.Monthly:
                var last = DateTime.DaysInMonth(day.Year, day.Month);
                if (day.Day == Math.Min(s.Day, last)) yield return s.Hour;
                break;
            case ConnectedScheduleTypes.Weekly:
                if ((int)day.DayOfWeek == s.Weekday) yield return s.Hour;
                break;
            case ConnectedScheduleTypes.Daily:
                yield return s.Hour;
                break;
            case ConnectedScheduleTypes.Hourly:
                for (var h = 0; h < 24; h++)
                    if (InHourlyWindow(s.Hour, s.EndHour, h)) yield return h;
                break;
        }
    }

    /// <summary>Start equal to end means the whole day; an end before the start wraps past midnight.</summary>
    public static bool InHourlyWindow(int start, int end, int hour) =>
        start == end || (start < end ? hour >= start && hour <= end : hour >= start || hour <= end);

    private static bool SameShape(ConnectedSchedule a, ConnectedSchedule b) =>
        a.Type == b.Type && a.Day == b.Day && a.Weekday == b.Weekday && a.Hour == b.Hour && a.EndHour == b.EndHour;

    private static string Clock(int hour) => $"{(hour % 12 == 0 ? 12 : hour % 12)} {(hour < 12 ? "AM" : "PM")}";

    private static ValidationException Invalid(string message) =>
        new(new Dictionary<string, string[]> { ["Schedule"] = [message] });
}
