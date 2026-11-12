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
}
