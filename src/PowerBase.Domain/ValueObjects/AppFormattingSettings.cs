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

    /// <summary>The app's time zone id from its raw <c>Formatting</c> JSON; "UTC" when unset or unreadable.</summary>
    public static string GetTimeZone(string? formattingJson)
    {
        if (string.IsNullOrWhiteSpace(formattingJson)) return new DateFormatSettings().TimeZone;
        try
        {
            var settings = JsonSerializer.Deserialize<AppFormattingSettings>(formattingJson, JsonOptions);
            return string.IsNullOrWhiteSpace(settings?.Date?.TimeZone) ? new DateFormatSettings().TimeZone : settings!.Date.TimeZone;
        }
        catch (JsonException)
        {
            return new DateFormatSettings().TimeZone;
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

    /// <summary>Display separator only: "-" (default) or "/". Never affects parsing — typed
    /// dates accept '-', '/' and '.' either way.</summary>
    public string Separator { get; set; } = "-";

    /// <summary>The app's time zone (IANA or Windows id). Date-times sent as wall-clock text through the Quickbase
    /// compatible API are read in this zone, as Quickbase reads them in the app's zone. "UTC" when not chosen.</summary>
    public string TimeZone { get; set; } = "UTC";
}
