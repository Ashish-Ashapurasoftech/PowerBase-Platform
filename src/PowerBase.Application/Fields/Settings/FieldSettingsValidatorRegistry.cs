namespace PowerBase.Application.Fields.Settings;

/// <summary>
/// Resolves the correct <see cref="IFieldSettingsValidator"/> for a given TypeCode
/// and runs it, returning a flat error dictionary suitable for throwing as a
/// <see cref="PowerBase.Domain.Exceptions.ValidationException"/>.
/// Returns an empty dictionary when the TypeCode has no registered validator (i.e. no
/// type-specific settings are defined for it).
/// </summary>
public sealed class FieldSettingsValidatorRegistry
{
    private readonly Dictionary<string, IFieldSettingsValidator> _map;

    public FieldSettingsValidatorRegistry(IEnumerable<IFieldSettingsValidator> validators)
    {
        _map = new Dictionary<string, IFieldSettingsValidator>(StringComparer.OrdinalIgnoreCase);
        foreach (var v in validators)
            foreach (var code in v.SupportedTypeCodes)
                _map[code] = v;
    }

    public IDictionary<string, string[]> Validate(string typeCode, string? settingsJson)
    {
        if (_map.TryGetValue(typeCode, out var validator))
            return validator.Validate(settingsJson);

        return new Dictionary<string, string[]>();
    }
}
