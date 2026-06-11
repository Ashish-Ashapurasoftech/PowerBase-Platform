namespace PowerBase.Domain.FieldSettings;

/// <summary>
/// Typed contract for the per-field-type configuration stored in
/// <c>AppField.Settings</c> (an otherwise free-form JSON column). These POCOs
/// describe the canonical shape for each field-type group so the value can be
/// validated on the backend and rendered consistently on the frontend.
///
/// All members are nullable: settings are optional, and an absent member means
/// "use the platform default". Property names are serialized as camelCase to
/// match the Angular client (see <see cref="FieldSettingsJson"/>).
/// </summary>
public sealed class ValidationSettings
{
    /// <summary>UI-soft regex the value must match (format validation only).</summary>
    public string? Regex { get; set; }

    /// <summary>UI-soft maximum character length.</summary>
    public int? MaxLength { get; set; }

    /// <summary>UI-soft inclusive minimum (numeric types).</summary>
    public decimal? Min { get; set; }

    /// <summary>UI-soft inclusive maximum (numeric types).</summary>
    public decimal? Max { get; set; }
}

public sealed class NumberSettings
{
    public int? Decimals { get; set; }
    public string? Separator { get; set; }
    public ValidationSettings? Validation { get; set; }
}

public sealed class CurrencySettings
{
    public string? Symbol { get; set; }
    /// <summary>"before" | "after".</summary>
    public string? Position { get; set; }
    public int? Decimals { get; set; }
    public string? Separator { get; set; }
    public ValidationSettings? Validation { get; set; }
}

public sealed class PercentSettings
{
    public int? Decimals { get; set; }
    public ValidationSettings? Validation { get; set; }
}

public sealed class RatingSettings
{
    public int? Max { get; set; }
}

public sealed class DateSettings
{
    public string? Format { get; set; }
    public bool? DefaultToday { get; set; }
}

public sealed class DurationSettings
{
    /// <summary>One of <see cref="DurationDisplays"/>.</summary>
    public string? Display { get; set; }
    public int? Decimals { get; set; }
}

public sealed class UrlSettings
{
    /// <summary>"plain" | "formula" (formula variant deferred).</summary>
    public string? Variant { get; set; }
    public string? Template { get; set; }
    public ValidationSettings? Validation { get; set; }
}

public sealed class TextSettings
{
    public ValidationSettings? Validation { get; set; }
}

public static class CurrencyPositions
{
    public const string Before = "before";
    public const string After = "after";
    public static readonly string[] All = [Before, After];
}

public static class UrlVariants
{
    public const string Plain = "plain";
    public const string Formula = "formula";
    public static readonly string[] All = [Plain, Formula];
}

public static class DurationDisplays
{
    public static readonly string[] All =
    [
        "HHMM", "HHMMSS", "MM", "MMSS", "Smart",
        "Weeks", "Days", "Hours", "Minutes", "Seconds",
    ];
}

public sealed class DateRangeSettings
{
    /// <summary>Display format applied to both start and end dates (same options as DateSettings).</summary>
    public string? Format { get; set; }
}

public sealed class NumericRangeSettings
{
    public int? Decimals { get; set; }
    public string? Separator { get; set; }
}

/// <summary>
/// Configuration for a Formula field: the declared result type and the expression
/// text. A Formula field is computed at read time and stores no physical column.
/// </summary>
public sealed class FormulaSettings
{
    /// <summary>One of <see cref="FormulaResultTypes"/>; maps to the engine's FormulaType.</summary>
    public string? ResultType { get; set; }

    /// <summary>The Quickbase-style formula expression text.</summary>
    public string? Expression { get; set; }
}

public static class FormulaResultTypes
{
    public const string Text = "Text";
    public const string Number = "Number";
    public const string Date = "Date";
    public const string DateTime = "DateTime";
    public const string Duration = "Duration";
    public const string Bool = "Bool";
    public const string User = "User";

    public static readonly string[] All = [Text, Number, Date, DateTime, Duration, Bool, User];
}
