using PowerBase.Application.Fields;
using PowerBase.Application.Fields.Settings;

namespace PowerBase.Application.Import.Services;

public sealed record SanitizedFieldProperties(string? Settings, string? DefaultValue, bool IsRequired, bool IsUnique, List<string> Notes);

/// <summary>
/// Makes imported field settings/flags safe to hand to the field-creation commands. Those commands validate every
/// field of a table together, so one value PowerBase rejects (an out-of-range width, a default value on a type that
/// has none, a settings property the type does not have) would fail the whole table — and with it the import. Each
/// offending piece is dropped on its own and reported, never the field, never the table.
/// </summary>
public class ImportFieldSanitizer
{
    private readonly FieldSettingsValidatorRegistry _settingsRegistry;

    public ImportFieldSanitizer(FieldSettingsValidatorRegistry settingsRegistry)
    {
        _settingsRegistry = settingsRegistry;
    }

    public SanitizedFieldProperties Sanitize(string typeCode, string label, string? settings, string? defaultValue, bool isRequired, bool isUnique)
    {
        var notes = new List<string>();

        var settingsErrors = _settingsRegistry.Validate(typeCode, settings);
        if (settingsErrors.Count > 0)
        {
            notes.Add($"Field settings not applied: {string.Join(" ", settingsErrors.Values.SelectMany(v => v))}");
            settings = null;
        }

        var errors = FieldGeneralSettingsCapability.Validate(typeCode, settings, label, isRequired, isUnique, defaultValue);
        if (errors.ContainsKey("IsRequired")) { notes.Add("Required is not supported for this field type; not applied."); isRequired = false; }
        if (errors.ContainsKey("IsUnique")) { notes.Add("Unique is not supported for this field type; not applied."); isUnique = false; }
        if (errors.ContainsKey("DefaultValue")) { notes.Add($"Default value '{defaultValue}' is not valid for this field type; not applied."); defaultValue = null; }

        return new SanitizedFieldProperties(settings, defaultValue, isRequired, isUnique, notes);
    }
}
