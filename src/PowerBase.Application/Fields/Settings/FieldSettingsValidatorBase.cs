using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace PowerBase.Application.Fields.Settings;

/// <summary>
/// Convenience base that deserialises the raw JSON and delegates to a typed validation method.
/// </summary>
public abstract class FieldSettingsValidatorBase<T> : IFieldSettingsValidator where T : class
{
    // UnmappedMemberHandling.Disallow is what makes "a field type only accepts its own
    // supported Behavior Settings" real: posting a property that doesn't exist on this
    // type's settings class (e.g. Currency's "symbol" against a Text field) now fails
    // deserialization instead of being silently ignored.
    private static readonly JsonSerializerOptions _opts =
        new() { PropertyNameCaseInsensitive = true, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow };

    public abstract IReadOnlyList<string> SupportedTypeCodes { get; }

    public IDictionary<string, string[]> Validate(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return new Dictionary<string, string[]>(); // null / empty settings are always valid

        T? parsed;
        try
        {
            parsed = JsonSerializer.Deserialize<T>(settingsJson, _opts);
        }
        catch (JsonException)
        {
            return Error("Settings", "Settings contains one or more properties not supported by this field type.");
        }

        if (parsed is null)
            return new Dictionary<string, string[]>();

        return ValidateTyped(parsed);
    }

    protected abstract IDictionary<string, string[]> ValidateTyped(T settings);

    protected static IDictionary<string, string[]> Ok() =>
        new Dictionary<string, string[]>();

    protected static IDictionary<string, string[]> Error(string field, string message) =>
        new Dictionary<string, string[]> { [field] = [message] };

    protected static void AddError(IDictionary<string, string[]> errors, string field, string message)
    {
        if (errors.TryGetValue(field, out var existing))
            errors[field] = [.. existing, message];
        else
            errors[field] = [message];
    }

    /// <summary>Checks that a regex string is syntactically valid.</summary>
    protected static bool IsValidRegex(string pattern)
    {
        try { _ = new Regex(pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); return true; }
        catch { return false; }
    }

    /// <summary>Shared range check for the "Default column width" Behavior Setting that
    /// recurs across nearly every field type (mirrors ReportLinkSettingsValidator's
    /// original rule — 20 to 2000 px).</summary>
    protected static void ValidateColumnWidth(int? columnWidth, IDictionary<string, string[]> errors)
    {
        if (columnWidth is < 20 or > 2000)
            AddError(errors, "Settings.ColumnWidth", "Column width must be between 20 and 2000.");
    }
}
