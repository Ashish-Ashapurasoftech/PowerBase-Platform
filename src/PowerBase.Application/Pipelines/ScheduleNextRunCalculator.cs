using System;
using System.Collections.Generic;
using System.Linq;
using NCrontab;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Pipelines;

public static class ScheduleNextRunCalculator
{
    private static readonly Dictionary<string, string> IanaToWindowsMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "America/New_York", "Eastern Standard Time" },
        { "America/Detroit", "Eastern Standard Time" },
        { "America/Kentucky/Louisville", "Eastern Standard Time" },
        { "America/Indiana/Indianapolis", "Eastern Standard Time" },
        { "America/Toronto", "Eastern Standard Time" },
        { "America/Chicago", "Central Standard Time" },
        { "America/Indiana/Knox", "Central Standard Time" },
        { "America/North_Dakota/Center", "Central Standard Time" },
        { "America/North_Dakota/New_Salem", "Central Standard Time" },
        { "America/Denver", "Mountain Standard Time" },
        { "America/Boise", "Mountain Standard Time" },
        { "America/Phoenix", "US Mountain Standard Time" },
        { "America/Los_Angeles", "Pacific Standard Time" },
        { "America/Anchorage", "Alaskan Standard Time" },
        { "America/Adak", "Hawaiian Standard Time" },
        { "Pacific/Honolulu", "Hawaiian Standard Time" },
        { "Europe/London", "GMT Standard Time" },
        { "Europe/Paris", "Romance Standard Time" },
        { "Europe/Berlin", "W. Europe Standard Time" },
        { "Asia/Kolkata", "India Standard Time" },
        { "Asia/Calcutta", "India Standard Time" },
        { "Asia/Tokyo", "Tokyo Standard Time" },
        { "Asia/Singapore", "Singapore Standard Time" },
        { "Asia/Dubai", "Arabian Standard Time" },
        { "America/Sao_Paulo", "E. South America Standard Time" },
        { "Australia/Sydney", "AUS Eastern Standard Time" },
        { "Pacific/Auckland", "New Zealand Standard Time" },
        { "UTC", "UTC" }
    };

    public static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch
        {
            if (IanaToWindowsMap.TryGetValue(timeZoneId, out var windowsId))
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                }
                catch
                {
                    return TimeZoneInfo.Utc;
                }
            }
            return TimeZoneInfo.Utc;
        }
    }

    public static DateTime CalculateNextRun(PipelineSchedule schedule, DateTime fromUtc)
    {
        var timeZoneInfo = ResolveTimeZone(schedule.TimeZone);
        var fromLocal = TimeZoneInfo.ConvertTimeFromUtc(fromUtc, timeZoneInfo);
        
        if (string.Equals(schedule.ScheduleType, "custom", StringComparison.OrdinalIgnoreCase))
        {
            var cron = CrontabSchedule.Parse(schedule.CronExpression);
            var nextLocal = cron.GetNextOccurrence(fromLocal);
            return ConvertToUtcSafe(nextLocal, timeZoneInfo);
        }

        var anchorUtc = schedule.CreatedOn == default ? fromUtc : schedule.CreatedOn;
        var anchorLocal = TimeZoneInfo.ConvertTimeFromUtc(anchorUtc, timeZoneInfo);
        var timeOfDay = schedule.TimeOfDay ?? new TimeSpan(9, 0, 0);
        var interval = schedule.Interval ?? 1;

        if (string.Equals(schedule.ScheduleType, "hourly", StringComparison.OrdinalIgnoreCase))
        {
            var anchorHour = new DateTime(anchorLocal.Year, anchorLocal.Month, anchorLocal.Day, anchorLocal.Hour, 0, 0);
            var hourDiff = (long)(fromLocal - anchorHour).TotalHours;
            var intervals = hourDiff / interval;
            var nextLocal = anchorHour.AddHours(intervals * interval);
            while (nextLocal <= fromLocal)
            {
                nextLocal = nextLocal.AddHours(interval);
            }
            return ConvertToUtcSafe(nextLocal, timeZoneInfo);
        }

        if (string.Equals(schedule.ScheduleType, "daily", StringComparison.OrdinalIgnoreCase))
        {
            var anchorDate = anchorLocal.Date.Add(timeOfDay);
            if (fromLocal < anchorDate)
            {
                return ConvertToUtcSafe(anchorDate, timeZoneInfo);
            }
            var daysDiff = (fromLocal.Date - anchorLocal.Date).Days;
            var nextLocal = anchorLocal.Date.AddDays((daysDiff / interval) * interval).Add(timeOfDay);
            while (nextLocal <= fromLocal)
            {
                nextLocal = nextLocal.AddDays(interval);
            }
            return ConvertToUtcSafe(nextLocal, timeZoneInfo);
        }

        if (string.Equals(schedule.ScheduleType, "weekly", StringComparison.OrdinalIgnoreCase))
        {
            var days = (schedule.Weekdays ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(int.Parse)
                .ToList();
            if (days.Count == 0)
            {
                days.Add((int)anchorLocal.DayOfWeek);
            }

            var anchorWeekStart = anchorLocal.Date.AddDays(-(int)anchorLocal.DayOfWeek);
            DateTime nextCandidate = DateTime.MaxValue;

            for (int w = 0; w < 52; w++)
            {
                if (w % interval != 0) continue;

                var weekStart = anchorWeekStart.AddDays(w * 7);
                foreach (var day in days)
                {
                    var candidate = weekStart.AddDays(day).Add(timeOfDay);
                    if (candidate > fromLocal && candidate < nextCandidate)
                    {
                        nextCandidate = candidate;
                    }
                }

                if (nextCandidate != DateTime.MaxValue)
                {
                    break;
                }
            }
            return ConvertToUtcSafe(nextCandidate, timeZoneInfo);
        }

        if (string.Equals(schedule.ScheduleType, "monthly", StringComparison.OrdinalIgnoreCase))
        {
            var dayTokens = (schedule.MonthDay ?? "")
                .Split(',', StringSplitOptions.RemoveEmptyEntries)
                .Select(t => t.Trim())
                .ToList();
            if (dayTokens.Count == 0)
            {
                dayTokens.Add("1");
            }

            DateTime nextCandidate = DateTime.MaxValue;

            for (int m = 0; m < 24; m++)
            {
                if (m % interval != 0) continue;

                var targetMonthLocal = anchorLocal.Date.AddMonths(m);
                var daysInMonth = DateTime.DaysInMonth(targetMonthLocal.Year, targetMonthLocal.Month);

                var candidatesInMonth = new List<DateTime>();
                foreach (var token in dayTokens)
                {
                    if (string.Equals(token, "last", StringComparison.OrdinalIgnoreCase))
                    {
                        candidatesInMonth.Add(new DateTime(targetMonthLocal.Year, targetMonthLocal.Month, daysInMonth).Add(timeOfDay));
                    }
                    else if (int.TryParse(token, out int dayNum) && dayNum >= 1 && dayNum <= 31)
                    {
                        if (dayNum <= daysInMonth)
                        {
                            candidatesInMonth.Add(new DateTime(targetMonthLocal.Year, targetMonthLocal.Month, dayNum).Add(timeOfDay));
                        }
                    }
                }

                var futureDays = candidatesInMonth
                    .Distinct()
                    .Where(c => c > fromLocal)
                    .OrderBy(c => c)
                    .ToList();

                if (futureDays.Count > 0)
                {
                    nextCandidate = futureDays[0];
                    break;
                }
            }
            return ConvertToUtcSafe(nextCandidate, timeZoneInfo);
        }

        if (string.Equals(schedule.ScheduleType, "yearly", StringComparison.OrdinalIgnoreCase))
        {
            var month = schedule.MonthOfYear ?? 1;
            var dayToken = (schedule.MonthDay ?? "1").Trim();

            DateTime nextCandidate = DateTime.MaxValue;

            for (int y = 0; y < 10; y++)
            {
                if (y % interval != 0) continue;

                var targetYear = anchorLocal.Year + y;
                var daysInMonth = DateTime.DaysInMonth(targetYear, month);

                int targetDay;
                if (string.Equals(dayToken, "last", StringComparison.OrdinalIgnoreCase))
                {
                    targetDay = daysInMonth;
                }
                else
                {
                    int.TryParse(dayToken, out targetDay);
                    if (targetDay < 1) targetDay = 1;
                }

                if (month == 2 && targetDay == 29 && !DateTime.IsLeapYear(targetYear))
                {
                    continue;
                }

                if (targetDay <= daysInMonth)
                {
                    var candidate = new DateTime(targetYear, month, targetDay).Add(timeOfDay);
                    if (candidate > fromLocal)
                    {
                        nextCandidate = candidate;
                        break;
                    }
                }
            }
            return ConvertToUtcSafe(nextCandidate, timeZoneInfo);
        }

        return fromUtc.AddHours(1);
    }

    private static DateTime ConvertToUtcSafe(DateTime localTime, TimeZoneInfo tz)
    {
        if (localTime == DateTime.MaxValue) return DateTime.MaxValue;

        if (tz.IsInvalidTime(localTime))
        {
            var nextValidInstant = localTime;
            foreach (var rule in tz.GetAdjustmentRules())
            {
                if (rule.DateStart <= localTime && rule.DateEnd >= localTime)
                {
                    nextValidInstant = localTime.Add(rule.DaylightDelta);
                    break;
                }
            }
            if (nextValidInstant == localTime)
            {
                nextValidInstant = localTime.AddHours(1);
            }
            return TimeZoneInfo.ConvertTimeToUtc(nextValidInstant, tz);
        }

        if (tz.IsAmbiguousTime(localTime))
        {
            var offsets = tz.GetAmbiguousTimeOffsets(localTime);
            var earliestOffset = offsets.OrderByDescending(o => o.TotalMinutes).First();
            var utcInstant = DateTime.SpecifyKind(localTime - earliestOffset, DateTimeKind.Utc);
            return utcInstant;
        }

        return TimeZoneInfo.ConvertTimeToUtc(localTime, tz);
    }
}
