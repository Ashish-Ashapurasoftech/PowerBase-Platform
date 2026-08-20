using System;
using System.Collections.Generic;

namespace PowerBase.Infrastructure.Pipelines;

public static class TimeZoneMapper
{
    private static readonly Dictionary<string, string> IanaToWindowsMap = new(StringComparer.OrdinalIgnoreCase)
    {
        { "America/New_York", "Eastern Standard Time" },
        { "America/Detroit", "Eastern Standard Time" },
        { "America/Kentucky/Louisville", "Eastern Standard Time" },
        { "America/Indiana/Indianapolis", "Eastern Standard Time" },
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
        { "UTC", "UTC" }
    };

    public static TimeZoneInfo ResolveTimeZone(string timeZoneId)
    {
        if (string.IsNullOrWhiteSpace(timeZoneId))
        {
            return TimeZoneInfo.Utc;
        }

        // Try standard resolution first (handles native system IANA support if running on Linux/ICU Windows)
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch
        {
            // Fallback to manual map for Windows-only compatibility
            if (IanaToWindowsMap.TryGetValue(timeZoneId, out var windowsId))
            {
                try
                {
                    return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
                }
                catch
                {
                    // Fallback to UTC if even that fails
                    return TimeZoneInfo.Utc;
                }
            }
            return TimeZoneInfo.Utc;
        }
    }
}
