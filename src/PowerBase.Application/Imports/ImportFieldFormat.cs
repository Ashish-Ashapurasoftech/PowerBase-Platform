using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Imports;

/// <summary>The format rules a field's own settings define (a text's maximum length and pattern, a number's pattern). A normal
/// record save enforces them, so the import must too, or it could store a value the form would have refused. Blank values
/// are not checked, as on a normal save.</summary>
public sealed class ImportFieldFormat
{
    private static readonly JsonSerializerOptions SettingsJson = new() { PropertyNameCaseInsensitive = true };

    private readonly string _label;
    private readonly int? _maxLength;
    private readonly Regex? _pattern;

    private ImportFieldFormat(string label, int? maxLength, Regex? pattern)
    {
        _label = label;
        _maxLength = maxLength;
        _pattern = pattern;
    }

    /// <summary>The checks for a field, or null when its settings define none.</summary>
    public static ImportFieldFormat? For(AppField field)
    {
        if (string.IsNullOrWhiteSpace(field.Settings)) return null;
        ValidationSettings? validation;
        try
        {
            validation = field.TypeCode switch
            {
                "Text" or "TextMultiLine" => JsonSerializer.Deserialize<TextSettings>(field.Settings, SettingsJson)?.Validation,
                "Number" or "Currency" or "Percent" or "Rating" => JsonSerializer.Deserialize<NumericSettings>(field.Settings, SettingsJson)?.Validation,
                _ => null
            };
        }
        catch (JsonException) { return null; }
        if (validation is null) return null;

        // Only text has a maximum length here; a numeric field's settings carry a pattern only.
        var maxLength = field.TypeCode is "Text" or "TextMultiLine" ? validation.MaxLength : null;
        Regex? pattern = null;
        if (!string.IsNullOrEmpty(validation.Regex))
        {
            // A pattern saved in settings is free text. A broken one is no constraint (as in a normal save), and a slow one is
            // cut off so it cannot hold up the run.
            try { pattern = new Regex(validation.Regex, RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)); }
            catch (ArgumentException) { pattern = null; }
        }
        return maxLength is null && pattern is null ? null : new ImportFieldFormat(ImportTypeCompatibility.DisplayName(field), maxLength, pattern);
    }

    /// <summary>The reason the value breaks the field's format, or null when it is fine.</summary>
    public string? Check(object? value)
    {
        if (value is null or DBNull || (value is string blank && string.IsNullOrWhiteSpace(blank))) return null;
        var text = value as string ?? Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        if (_maxLength is { } max && text.Length > max) return $"'{_label}' must be {max} characters or fewer.";
        if (_pattern is null) return null;
        try { return _pattern.IsMatch(text) ? null : $"'{_label}' has an invalid format."; }
        catch (RegexMatchTimeoutException) { return $"'{_label}' has an invalid format."; }
    }
}
