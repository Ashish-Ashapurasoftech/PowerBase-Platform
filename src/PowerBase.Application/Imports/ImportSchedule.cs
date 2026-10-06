using System.Globalization;
using NCrontab;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Imports;

public static class ImportScheduleType
{
    public const string Hourly = "hourly";
    public const string Daily = "daily";
    public const string Weekly = "weekly";
    public const string Monthly = "monthly";
    public const string Custom = "custom";
    public static readonly string[] All = [Hourly, Daily, Weekly, Monthly, Custom];
}

/// <summary>When a saved import runs by itself. The times are in <see cref="TimeZone"/>, so "9:00 every day" stays 9:00 across
/// daylight-saving changes. An interval greater than 1 (every 2 days) counts from <see cref="AnchorOn"/>, which is set by the server
/// when the schedule is created or changed.</summary>
public sealed class ImportSchedule
{
    /// <summary>A paused schedule keeps its settings but never runs.</summary>
    public bool Enabled { get; set; } = true;
    public string Type { get; set; } = ImportScheduleType.Daily;
    /// <summary>Every N hours / days / weeks / months. Not used by a custom schedule.</summary>
    public int Interval { get; set; } = 1;
    /// <summary>"HH:mm" in the schedule's time zone (daily, weekly and monthly).</summary>
    public string TimeOfDay { get; set; } = "09:00";
    /// <summary>Weekly: 0 = Sunday … 6 = Saturday.</summary>
    public List<int> Weekdays { get; set; } = new();
    /// <summary>Monthly: day numbers 1–31 and/or "last", comma separated ("1,15,last").</summary>
    public string MonthDays { get; set; } = "1";
    /// <summary>Custom: a five-field cron expression (minute hour day month weekday).</summary>
    public string Cron { get; set; } = "";
    public string TimeZone { get; set; } = "UTC";
    public DateTime AnchorOn { get; set; }
}

/// <summary>Checks a schedule and works out when it next runs. The calendar maths is the platform's existing schedule calculator
/// (also used by pipelines), so an import's "every second Monday at 9" means exactly what it means everywhere else.</summary>
public static class ImportScheduling
{
    /// <summary>No import may run more often than this, however the schedule is written.</summary>
    public static readonly TimeSpan MinimumGap = TimeSpan.FromMinutes(15);

    private static readonly Dictionary<string, int> MaxInterval = new()
    {
        [ImportScheduleType.Hourly] = 168, [ImportScheduleType.Daily] = 365, [ImportScheduleType.Weekly] = 52, [ImportScheduleType.Monthly] = 12
    };

    private static ValidationException Invalid(string message) =>
        new(new Dictionary<string, string[]> { ["Schedule"] = [message] });

    /// <summary>Returns the schedule cleaned up (trimmed, sorted, de-duplicated), or throws with the first thing wrong with it.</summary>
    public static ImportSchedule Validate(ImportSchedule input)
    {
        var type = input.Type?.Trim().ToLowerInvariant() ?? "";
        if (!ImportScheduleType.All.Contains(type)) throw Invalid("Choose how often the import runs.");
        if (!ScheduleNextRunCalculator.TryResolveTimeZone(input.TimeZone?.Trim(), out _)) throw Invalid("The time zone is not recognised.");

        var schedule = new ImportSchedule
        {
            Enabled = input.Enabled, Type = type, TimeZone = input.TimeZone!.Trim(), AnchorOn = input.AnchorOn,
            Interval = 1, TimeOfDay = "09:00", Weekdays = [], MonthDays = "1", Cron = ""
        };

        if (type != ImportScheduleType.Custom)
        {
            if (input.Interval < 1 || input.Interval > MaxInterval[type])
                throw Invalid($"Repeat every 1 to {MaxInterval[type]} {Unit(type)}.");
            schedule.Interval = input.Interval;
        }
        if (type is ImportScheduleType.Daily or ImportScheduleType.Weekly or ImportScheduleType.Monthly)
        {
            if (!TryParseTime(input.TimeOfDay, out _)) throw Invalid("The time of day must look like 09:30.");
            schedule.TimeOfDay = input.TimeOfDay.Trim();
        }
        if (type == ImportScheduleType.Weekly)
        {
            var days = (input.Weekdays ?? []).Distinct().OrderBy(d => d).ToList();
            if (days.Count == 0) throw Invalid("Choose at least one day of the week.");
            if (days.Any(d => d is < 0 or > 6)) throw Invalid("A day of the week must be 0 (Sunday) to 6 (Saturday).");
            schedule.Weekdays = days;
        }
        if (type == ImportScheduleType.Monthly) schedule.MonthDays = NormalizeMonthDays(input.MonthDays);
        if (type == ImportScheduleType.Custom) schedule.Cron = ValidateCron(input.Cron);
        return schedule;
    }

    /// <summary>When the schedule next runs after <paramref name="afterUtc"/> (UTC), counting from now rather than from a missed
    /// time, so a server that was down runs the import once on return, never once per missed occurrence.</summary>
    public static DateTime NextRun(ImportSchedule schedule, DateTime afterUtc)
    {
        TryParseTime(schedule.TimeOfDay, out var time);
        var transient = new PipelineSchedule
        {
            ScheduleType = schedule.Type, Interval = schedule.Interval, TimeOfDay = time, Weekdays = string.Join(',', schedule.Weekdays),
            MonthDay = schedule.MonthDays, TimeZone = schedule.TimeZone, CronExpression = schedule.Cron, CreatedOn = schedule.AnchorOn
        };
        return ScheduleNextRunCalculator.CalculateNextRun(transient, afterUtc);
    }

    /// <summary>Cleans a schedule submitted with a definition and works out its next run. The anchor that "every N …" counts from is
    /// kept while the schedule's shape is unchanged (so editing the import does not shift the cadence) and reset when it changes.
    /// A paused schedule keeps its settings and has no next run; no schedule at all clears both.</summary>
    public static (ImportSchedule? Schedule, DateTime? NextRunOn) Prepare(ImportSchedule? submitted, string? existingJson, DateTime nowUtc)
    {
        if (submitted is null) return (null, null);
        var schedule = Validate(submitted);
        var existing = ImportJson.Deserialize<ImportSchedule>(existingJson);
        schedule.AnchorOn = existing is not null && SameShape(existing, schedule) && existing.AnchorOn != default ? existing.AnchorOn : nowUtc;
        if (!schedule.Enabled) return (schedule, null);

        var next = NextRun(schedule, nowUtc);
        if (next == DateTime.MaxValue) throw Invalid("This schedule never runs again.");
        return (schedule, next);
    }

    /// <summary>A short plain-language description, for emails and logs.</summary>
    public static string Describe(ImportSchedule s) => s.Type switch
    {
        ImportScheduleType.Hourly => s.Interval == 1 ? "every hour" : $"every {s.Interval} hours",
        ImportScheduleType.Daily => $"{(s.Interval == 1 ? "every day" : $"every {s.Interval} days")} at {s.TimeOfDay} ({s.TimeZone})",
        ImportScheduleType.Weekly => $"{(s.Interval == 1 ? "every week" : $"every {s.Interval} weeks")} on {string.Join(", ", s.Weekdays.Select(d => ((DayOfWeek)d).ToString()))} at {s.TimeOfDay} ({s.TimeZone})",
        ImportScheduleType.Monthly => $"{(s.Interval == 1 ? "every month" : $"every {s.Interval} months")} on day {s.MonthDays} at {s.TimeOfDay} ({s.TimeZone})",
        _ => $"cron {s.Cron} ({s.TimeZone})"
    };

    private static bool SameShape(ImportSchedule a, ImportSchedule b) =>
        a.Type == b.Type && a.Interval == b.Interval && a.TimeOfDay == b.TimeOfDay && a.Weekdays.SequenceEqual(b.Weekdays)
        && a.MonthDays == b.MonthDays && a.Cron == b.Cron && string.Equals(a.TimeZone, b.TimeZone, StringComparison.OrdinalIgnoreCase);

    private static string Unit(string type) => type switch
    {
        ImportScheduleType.Hourly => "hours", ImportScheduleType.Daily => "days", ImportScheduleType.Weekly => "weeks", _ => "months"
    };

    private static bool TryParseTime(string? text, out TimeSpan time)
    {
        time = default;
        return text is not null && TimeSpan.TryParseExact(text.Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out time) && time < TimeSpan.FromDays(1);
    }

    private static string NormalizeMonthDays(string? text)
    {
        var tokens = new List<string>();
        foreach (var raw in (text ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.ToLowerInvariant();
            if (token != "last" && !(int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out var day) && day is >= 1 and <= 31))
                throw Invalid($"'{raw}' is not a day of the month. Use 1 to 31, or \"last\".");
            if (!tokens.Contains(token)) tokens.Add(token);
        }
        if (tokens.Count == 0) throw Invalid("Choose at least one day of the month.");
        return string.Join(',', tokens);
    }

    /// <summary>A five-field cron expression that never fires more often than <see cref="MinimumGap"/>. Every occurrence in the next
    /// fortnight (counted from a fixed date, so the answer does not depend on today) is checked, not just the next two.</summary>
    private static string ValidateCron(string? text)
    {
        var cron = (text ?? "").Trim();
        if (cron.Length is 0 or > 100) throw Invalid("Enter a cron expression such as 0 9 * * 1-5.");
        if (cron.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length != 5)
            throw Invalid("A cron expression has five fields: minute, hour, day of month, month, day of week.");
        var parsed = CrontabSchedule.TryParse(cron);
        if (parsed is null) throw Invalid("The cron expression is not valid.");

        var start = new DateTime(2026, 1, 5, 0, 0, 0);
        DateTime? previous = null;
        foreach (var occurrence in parsed.GetNextOccurrences(start, start.AddDays(14)))
        {
            if (previous is { } p && occurrence - p < MinimumGap)
                throw Invalid($"The import would run more often than every {(int)MinimumGap.TotalMinutes} minutes, which is the most often it may.");
            previous = occurrence;
        }
        return cron;
    }
}
