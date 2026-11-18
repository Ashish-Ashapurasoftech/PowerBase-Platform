using System.Globalization;
using System.Text.RegularExpressions;

namespace PowerBase.API.QuickbaseApi.Common;

/// <summary>
/// Normalises Quickbase input values (developer.quickbase.com "Field type details") to what our record commands store.
/// </summary>
public static class QbInputValues
{
    private static readonly Regex DaysRelative = new(@"^\s*(\d+)\s+days?\s+(ago|from\s+now)\s*$", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    /// <summary>Date: YYYY-MM-DD, or the keywords "today", "N day(s) from now", "N day(s) ago". Anything else is passed on.</summary>
    public static string? Date(string? value, DateTime? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var today = (utcNow ?? System.DateTime.UtcNow).Date;
        var v = value.Trim();
        if (v.Equals("today", StringComparison.OrdinalIgnoreCase)) return today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var m = DaysRelative.Match(v);
        if (m.Success)
        {
            var n = int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
            var d = m.Groups[2].Value.StartsWith("ago", StringComparison.OrdinalIgnoreCase) ? today.AddDays(-n) : today.AddDays(n);
            return d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return v;
    }

    /// <summary>
    /// DateTime: ISO 8601 with optional .sss and an optional zone (Z / +hh:mm / -hh:mm), or "now" / "today". Values with a zone
    /// are converted to UTC. Without a zone Quickbase uses the app's time zone; we have no app time zone, so it is taken as UTC.
    /// </summary>
    public static string? DateTime(string? value, DateTime? utcNow = null)
    {
        if (string.IsNullOrWhiteSpace(value)) return value;
        var now = utcNow ?? System.DateTime.UtcNow;
        var v = value.Trim();
        if (v.Equals("now", StringComparison.OrdinalIgnoreCase)) return now.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        if (v.Equals("today", StringComparison.OrdinalIgnoreCase)) return now.Date.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        var hasZone = v.EndsWith("Z", StringComparison.OrdinalIgnoreCase) || Regex.IsMatch(v, @"[+-]\d{2}:?\d{2}$");
        if (hasZone && DateTimeOffset.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto))
            return dto.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        if (!hasZone && System.DateTime.TryParse(v, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
            return dt.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
        return v;
    }

    public static string ContentType(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".jpg" or ".jpeg" => "image/jpeg", ".png" => "image/png", ".gif" => "image/gif", ".pdf" => "application/pdf",
        ".txt" or ".log" => "text/plain", ".csv" => "text/csv", ".xml" => "application/xml", ".json" => "application/json",
        ".doc" => "application/msword", ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        ".xls" => "application/vnd.ms-excel", ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        _ => "application/octet-stream",
    };
}
