using System.Globalization;
using PowerBase.Domain.ValueObjects;

namespace PowerBase.API.QuickbaseApi.Common;

/// <summary>
/// The app's time zone (App Properties &gt; Date Formatting &gt; Time Zone) and how Quickbase's XML calls read a date-time
/// with it, measured on a live realm (app in "(UTC-05:00) Eastern Time (US &amp; Canada)"):
/// <list type="bullet">
/// <item>"MM-DD-YYYY HH:mm[ AM/PM]" and "YYYY-MM-DD HH:mm" are wall-clock times in the app's zone (DST aware);</item>
/// <item>ISO text with a 'T' ("2026-03-15T10:00:00", with or without Z) is UTC, an explicit offset is honoured;</item>
/// <item>milliseconds since the epoch are app-zone wall clock as well, unless the call sends <c>msInUTC=1</c>.</item>
/// </list>
/// </summary>
public static class QbTimeZone
{
    /// <summary>The zone named by the app's Formatting JSON (IANA id, Windows id, or a Quickbase display name such as
    /// "(UTC-05:00) Eastern Time (US &amp; Canada)"); UTC when none is set or it is not a known zone.</summary>
    public static TimeZoneInfo OfApp(string? formattingJson) => Resolve(AppFormattingSettings.GetTimeZone(formattingJson)) ?? TimeZoneInfo.Utc;

    public static TimeZoneInfo? Resolve(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var name = id.Trim();
        if (TimeZoneInfo.TryFindSystemTimeZoneById(name, out var zone)) return zone;
        // Quickbase display names: "(UTC-05:00) Eastern Time (US & Canada)" → the zone whose display name / standard name matches.
        var label = name.StartsWith('(') && name.IndexOf(')') is var close and > 0 ? name[(close + 1)..].Trim() : name;
        foreach (var z in TimeZoneInfo.GetSystemTimeZones())
        {
            var display = z.DisplayName.StartsWith('(') && z.DisplayName.IndexOf(')') is var c and > 0 ? z.DisplayName[(c + 1)..].Trim() : z.DisplayName;
            if (string.Equals(display, label, StringComparison.OrdinalIgnoreCase) || string.Equals(z.StandardName, label, StringComparison.OrdinalIgnoreCase))
                return z;
        }
        return KNOWN.TryGetValue(label, out var iana) && TimeZoneInfo.TryFindSystemTimeZoneById(iana, out var known) ? known : null;
    }

    /// <summary>Quickbase labels whose system display name differs between Windows and Linux.</summary>
    private static readonly Dictionary<string, string> KNOWN = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Eastern Time (US & Canada)"] = "America/New_York",
        ["Central Time (US & Canada)"] = "America/Chicago",
        ["Mountain Time (US & Canada)"] = "America/Denver",
        ["Arizona"] = "America/Phoenix",
        ["Pacific Time (US & Canada)"] = "America/Los_Angeles",
        ["Alaska"] = "America/Anchorage",
        ["Hawaii"] = "Pacific/Honolulu",
        ["Coordinated Universal Time"] = "UTC",
        ["Chennai, Kolkata, Mumbai, New Delhi"] = "Asia/Kolkata",
        ["Dublin, Edinburgh, Lisbon, London"] = "Europe/London",
    };

    private static readonly string[] WALL_FORMATS =
    {
        "MM-dd-yyyy", "M-d-yyyy", "MM/dd/yyyy", "M/d/yyyy",
        "MM-dd-yyyy HH:mm", "MM-dd-yyyy H:mm", "MM-dd-yyyy HH:mm:ss", "MM-dd-yyyy hh:mm tt", "MM-dd-yyyy h:mm tt",
        "M-d-yyyy H:mm", "M-d-yyyy h:mm tt", "MM/dd/yyyy HH:mm", "MM/dd/yyyy hh:mm tt", "M/d/yyyy H:mm", "M/d/yyyy h:mm tt",
        "yyyy-MM-dd", "yyyy-MM-dd HH:mm", "yyyy-MM-dd HH:mm:ss", "yyyy-MM-dd hh:mm tt",
    };

    /// <summary>Reads a Quickbase XML date-time value into UTC (rules above); null when it is not a date-time.</summary>
    public static DateTime? ParseToUtc(string text, TimeZoneInfo zone, bool msInUtc = false)
    {
        var s = text.Trim();
        if (s.Length == 0) return null;
        if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var ms) && s.Length >= 9)
        {
            var instant = DateTimeOffset.FromUnixTimeMilliseconds(ms).UtcDateTime;
            return msInUtc ? instant : FromWall(DateTime.SpecifyKind(instant, DateTimeKind.Unspecified), zone);
        }
        if (s.Contains('T'))
        {
            if (DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso)) return iso.UtcDateTime;
            return null;
        }
        if (DateTime.TryParseExact(s, WALL_FORMATS, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var wall))
            return FromWall(wall, zone);
        if (DateTime.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out var other))
            return other.Kind == DateTimeKind.Unspecified ? FromWall(other, zone) : other.ToUniversalTime();
        return null;
    }

    private static DateTime FromWall(DateTime wall, TimeZoneInfo zone)
    {
        var unspecified = DateTime.SpecifyKind(wall, DateTimeKind.Unspecified);
        // A wall time skipped by a DST change moves forward an hour, as clocks do.
        if (zone.IsInvalidTime(unspecified)) unspecified = unspecified.AddHours(1);
        return DateTime.SpecifyKind(TimeZoneInfo.ConvertTimeToUtc(unspecified, zone), DateTimeKind.Utc);
    }
}
