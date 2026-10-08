using System.Globalization;
using System.Text.RegularExpressions;

namespace PowerBase.Application.Imports;

/// <summary>Reads a number from text without guessing. A comma only counts as a thousands separator when it groups the
/// digits in threes ("1,234.50"); "1,5" is refused instead of being quietly read as 15, because it is far more likely a
/// decimal comma than a thousand-and-something.</summary>
public static partial class ImportNumber
{
    [GeneratedRegex(@"^[+-]?(\d+|\d{1,3}(,\d{3})+)?(\.\d+)?$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    public static bool TryParse(string? text, out decimal value)
    {
        value = 0;
        var trimmed = text?.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed is "+" or "-" or "." || !Shape().IsMatch(trimmed)) return false;
        return decimal.TryParse(trimmed, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowThousands,
            CultureInfo.InvariantCulture, out value);
    }
}
