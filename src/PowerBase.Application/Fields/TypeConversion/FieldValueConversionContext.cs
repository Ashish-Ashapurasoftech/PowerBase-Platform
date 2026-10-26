using PowerBase.Domain.ValueObjects;

namespace PowerBase.Application.Fields.TypeConversion;

/// <summary>
/// Everything a single-value conversion needs beyond the value itself. Built once per Change Type run by the
/// handler (which owns the repository calls) and shared by every record, so <see cref="FieldValueConverter"/>
/// stays pure and unit-testable.
/// </summary>
public sealed class FieldValueConversionContext
{
    /// <summary>The app's Formatting settings — date order (MM-DD vs DD-MM) for parsing, date text for → Text.</summary>
    public AppFormattingSettings AppFormatting { get; init; } = new();

    /// <summary>Source field's Settings JSON (e.g. the Duration display unit when converting Duration → Number).</summary>
    public string? SourceSettingsJson { get; init; }

    /// <summary>Target field's Settings JSON (e.g. the Duration display unit for a bare number → Duration).</summary>
    public string? TargetSettingsJson { get; init; }

    /// <summary>App users by email (case-insensitive) → user Id, for Text/Email → User.</summary>
    public IReadOnlyDictionary<string, long> UserIdsByEmail { get; init; } =
        new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);

    /// <summary>User Id → email, for User → Text/Email.</summary>
    public IReadOnlyDictionary<long, string> UserEmailsById { get; init; } = new Dictionary<long, string>();

    public static FieldValueConversionContext Default { get; } = new();
}

/// <summary>One converted value. <see cref="Value"/> is in the target column's storage form
/// (string, decimal, bool, DateTime, TimeSpan, long minutes) or null.</summary>
/// <param name="Lost">The source held a value but the target couldn't keep it (it became blank).</param>
public readonly record struct ConvertedValue(object? Value, bool Lost)
{
    public static ConvertedValue Empty => new(null, false);
}
