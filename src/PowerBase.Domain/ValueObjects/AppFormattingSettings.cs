namespace PowerBase.Domain.ValueObjects;

public class AppFormattingSettings
{
    public CurrencyFormatSettings Currency { get; set; } = new();
    public NumberFormatSettings Number { get; set; } = new();
    public DateFormatSettings Date { get; set; } = new();
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
