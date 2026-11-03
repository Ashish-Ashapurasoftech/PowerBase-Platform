using System;
using System.Collections.Generic;
using System.Linq;

namespace PowerBase.Application.Import.Qbl;

/// <summary>A Quickbase pipeline's <c>Crontab</c> as the PowerFlows schedule editor would save it. A crontab that matches one
/// of the editor's own repeat options (every hour, day at a time, weekdays, days of the month, a day of the year) becomes that
/// option, with the cron the editor itself writes for it, so the schedule opens as e.g. "every day at 01:30" instead of a raw
/// cron. Anything else stays a custom cron, which runs exactly as Quickbase ran it.</summary>
public static class QblCrontabSchedule
{
    public sealed record Schedule(
        string ScheduleType,
        int? Interval,
        TimeSpan? TimeOfDay,
        string? Weekdays,
        string? MonthDay,
        int? MonthOfYear,
        string CronExpression);

    public static Schedule From(string crontab)
    {
        var cron = crontab.Trim();
        var custom = new Schedule("custom", null, null, null, null, null, cron);
        var parts = cron.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 5)
            return custom;
        var (minuteText, hourText, dayText, monthText, weekdayText) = (parts[0], parts[1], parts[2], parts[3], parts[4]);
        if (!TryNumber(minuteText, 0, 59, out var minute))
            return custom;

        // Every hour on the hour: the editor's hourly option always runs at minute 0.
        if (hourText == "*" && dayText == "*" && monthText == "*" && weekdayText == "*")
            return minute == 0 ? new Schedule("hourly", 1, null, null, null, null, "0 */1 * * *") : custom;

        if (!TryNumber(hourText, 0, 23, out var hour))
            return custom;
        var time = new TimeSpan(hour, minute, 0);
        var prefix = $"{minute} {hour}";

        if (dayText == "*" && monthText == "*")
        {
            if (weekdayText == "*")
                return new Schedule("daily", 1, time, null, null, null, $"{prefix} * * *");
            if (!TryList(weekdayText, 0, 7, out var days))
                return custom;
            var weekdays = days.Select(d => d % 7).Distinct().OrderBy(d => d).ToList();
            if (weekdays.Count == 7)
                return new Schedule("daily", 1, time, null, null, null, $"{prefix} * * *");
            var list = string.Join(",", weekdays);
            return new Schedule("weekly", 1, time, list, null, null, $"{prefix} * * {list}");
        }

        if (weekdayText != "*" || !TryList(dayText, 1, 31, out var monthDays))
            return custom;
        var dayList = string.Join(",", monthDays.Distinct().OrderBy(d => d));
        if (monthText == "*")
            return new Schedule("monthly", 1, time, null, dayList, null, $"{prefix} {dayList} * *");
        if (monthDays.Count == 1 && TryNumber(monthText, 1, 12, out var month))
            return new Schedule("yearly", 1, time, null, dayList, month, $"{prefix} {dayList} {month} *");
        return custom;
    }

    private static bool TryNumber(string text, int min, int max, out int value) =>
        int.TryParse(text, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out value)
        && value >= min && value <= max;

    /// <summary>A comma list of single numbers (<c>1,3,5</c>); ranges and steps are left to a custom cron.</summary>
    private static bool TryList(string text, int min, int max, out List<int> values)
    {
        values = [];
        foreach (var item in text.Split(','))
        {
            if (!TryNumber(item, min, max, out var value))
                return false;
            values.Add(value);
        }
        return values.Count > 0;
    }
}
