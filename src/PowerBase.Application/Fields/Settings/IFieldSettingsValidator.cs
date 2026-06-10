namespace PowerBase.Application.Fields.Settings;

/// <summary>
/// Validates the JSON content of <c>AppField.Settings</c> for a specific field type group.
/// Implementations are registered in <see cref="FieldSettingsValidatorRegistry"/>.
/// </summary>
public interface IFieldSettingsValidator
{
    /// <summary>
    /// The TypeCode (or TypeCodes) this validator handles.
    /// </summary>
    IReadOnlyList<string> SupportedTypeCodes { get; }

    /// <summary>
    /// Validates the raw settings JSON string.
    /// Returns a dictionary of field-path → error messages, or an empty dictionary on success.
    /// The caller converts this to a <see cref="PowerBase.Domain.Exceptions.ValidationException"/>.
    /// </summary>
    IDictionary<string, string[]> Validate(string? settingsJson);
}
