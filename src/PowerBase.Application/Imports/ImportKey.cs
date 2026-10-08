using System.Globalization;

namespace PowerBase.Application.Imports;

/// <summary>Canonical string form of a value for duplicate detection, identical on the file/row side and the
/// database side so "12.5" and 12.5000 compare equal.</summary>
public static class ImportKey
{
    public static string? Normalize(object? value) => value switch
    {
        null or DBNull => null,
        string s => string.IsNullOrWhiteSpace(s) ? null : s.Trim(),
        decimal d => decimal.Round(d, 4).ToString("0.####", CultureInfo.InvariantCulture),
        double or float => decimal.Round(Convert.ToDecimal(value, CultureInfo.InvariantCulture), 4).ToString("0.####", CultureInfo.InvariantCulture),
        DateTime dt => dt.ToString("O", CultureInfo.InvariantCulture),
        bool b => b ? "1" : "0",
        _ => Convert.ToString(value, CultureInfo.InvariantCulture)
    };

    public static StringComparer Comparer => StringComparer.OrdinalIgnoreCase;
}
