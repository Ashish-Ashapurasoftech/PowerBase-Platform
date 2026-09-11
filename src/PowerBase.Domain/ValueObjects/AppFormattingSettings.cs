using System.Text.Json;

namespace PowerBase.Domain.ValueObjects;

public class AppFormattingSettings
{
    public CurrencyFormatSettings Currency { get; set; } = new();
    public NumberFormatSettings Number { get; set; } = new();
    public DateFormatSettings Date { get; set; } = new();

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>Reads the configured date format string out of an App's raw <c>Formatting</c>
    /// JSON column, falling back to <see cref="DateFormatSettings"/>'s own default when the
    /// column is null/blank or the JSON is malformed — a formatting-parse failure must never
    /// throw or block a record write (mirrors RecordConstraintValidator.ParseSettings's same
    /// defensive try/catch pattern for field Settings JSON).</summary>
    public static string GetDateFormatString(string? formattingJson)
    {
        if (string.IsNullOrWhiteSpace(formattingJson)) return new DateFormatSettings().FormatString;
        try
        {
            var settings = JsonSerializer.Deserialize<AppFormattingSettings>(formattingJson, JsonOptions);
            return settings?.Date?.FormatString ?? new DateFormatSettings().FormatString;
        }
        catch (JsonException)
        {
            return new DateFormatSettings().FormatString;
        }
    }
}

public class CurrencyFormatSettings
{
    public string Symbol { get; set; } = "$";
    /// <summary>Before, After</summary>
    public string Position { get; set; } = "Before";
}

public class NumberFormatSettings
{
    public int DecimalPlaces { get; set; } = 2;
    public string ThousandSeparator { get; set; } = ",";
    public string DisplayPattern { get; set; } = "Standard";
}

public class DateFormatSettings
{
    /// <summary>MM-DD-YYYY, DD-MM-YYYY, YYYY-MM-DD, etc.</summary>
    public string FormatString { get; set; } = "MM-DD-YYYY";
}
