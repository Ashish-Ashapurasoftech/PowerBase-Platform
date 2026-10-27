namespace PowerBase.Application.Fields;

/// <summary>
/// Rules for the Shared values setting ("Values from this field may be a source for dropdown lists"): a strict
/// allow-list of stored, plain-value types — derived, structured, attachment and system values are never offered
/// as another field's choices.
/// </summary>
public static class FieldSharedValuesCapability
{
    private static readonly HashSet<string> SupportedTypeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "SingleSelect", "Email", "Url",
        "Number", "Currency", "Percent", "Rating",
    };

    public static bool IsSupported(string typeCode) => SupportedTypeCodes.Contains(typeCode);

    /// <summary>Rejects turning Shared values ON for an unsupported type. Leaving it off is always allowed.</summary>
    public static IDictionary<string, string[]> Validate(string typeCode, string label, bool isSystem, bool newIsSharedValues)
    {
        var errors = new Dictionary<string, string[]>();
        if (newIsSharedValues && (isSystem || !IsSupported(typeCode)))
            errors["IsSharedValues"] = [$"'{label}' fields of this type cannot share their values."];
        return errors;
    }
}
