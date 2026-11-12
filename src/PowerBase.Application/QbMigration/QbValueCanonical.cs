using System.Globalization;

namespace PowerBase.Application.QbMigration;

/// <summary>One text form per value, so a value written by the loader and the same value read back from PowerBase compare
/// equal (decimal 1.5 and 1.5000, a date with or without a zero time, a user id as text or number).</summary>
public static class QbValueCanonical
{
    public static string? Of(object? value) => value switch
    {
        null or DBNull => null,
        string s => s.Length == 0 ? null : s,
        decimal d => d.ToString("0.############################", CultureInfo.InvariantCulture),
        double d => ((decimal)d).ToString("0.############################", CultureInfo.InvariantCulture),
        int or long or short => System.Convert.ToString(value, CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        DateTime dt => dt.TimeOfDay == TimeSpan.Zero ? dt.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : dt.ToString("yyyy-MM-ddTHH:mm:ss.fff", CultureInfo.InvariantCulture),
        TimeSpan ts => ts.ToString("c", CultureInfo.InvariantCulture),
        _ => System.Convert.ToString(value, CultureInfo.InvariantCulture),
    };

    /// <summary>QuickBase keeps some numbers as binary floats (99.98999999999999 for 99.99, 7.000000000000001 for 7), while
    /// PowerBase stores the decimal. Two numbers within one billionth of their size are the same value; a real difference
    /// (1.234 stored as 1.23) is far larger and still counts.</summary>
    public static bool SameNumber(string? expected, string? actual)
    {
        if (expected is null || actual is null
            || !decimal.TryParse(expected, NumberStyles.Number, CultureInfo.InvariantCulture, out var e)
            || !decimal.TryParse(actual, NumberStyles.Number, CultureInfo.InvariantCulture, out var a))
            return false;
        return Math.Abs(e - a) <= 0.000000001m * Math.Max(1m, Math.Abs(e));
    }
}
