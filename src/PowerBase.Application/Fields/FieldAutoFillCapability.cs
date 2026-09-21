namespace PowerBase.Application.Fields;

/// <summary>
/// Per-type allow-list for a field's Auto-fill setting ("Copy the value when using 'Copy this
/// [Singular name]' to add a new [Singular name]"). Unlike <see cref="FieldGeneralSettingsCapability"/>
/// and <see cref="FieldAdvancedSettingsCapability"/> — which treat an unlisted type as unrestricted —
/// this matrix is a strict allow-list: any type not listed here (Lookup, Summary, ReportLink, File,
/// every Formula_* variant, Reference, ActionButton*, or an unrecognized code) does not support
/// Auto-fill at all, matching the requirement that derived/computed/attachment/system values are
/// never eligible to be copied via this setting.
/// </summary>
public static class FieldAutoFillCapability
{
    private static readonly HashSet<string> SupportedTypeCodes = new(StringComparer.OrdinalIgnoreCase)
    {
        "Text", "RichText", "TextMultiLine", "MultiSelect", "SingleSelect",
        "Number", "Currency", "Percent", "Rating",
        "Date", "DateTime", "Time", "Duration",
        "Boolean",
        "User", "MultiUser",
        "Phone", "Email", "Url", "Address",
    };

    public static bool IsSupported(string typeCode) => SupportedTypeCodes.Contains(typeCode);

    /// <summary>Rejects turning Auto-fill ON for a type that doesn't support it. Leaving it off is
    /// always allowed, so an existing row carrying a legacy/inapplicable value is never broken by
    /// this check.</summary>
    public static IDictionary<string, string[]> Validate(string typeCode, string label, bool newIsAutoFill)
    {
        var errors = new Dictionary<string, string[]>();
        if (newIsAutoFill && !IsSupported(typeCode))
            errors["IsAutoFill"] = [$"'{label}' fields of this type do not support Auto-fill."];
        return errors;
    }
}
