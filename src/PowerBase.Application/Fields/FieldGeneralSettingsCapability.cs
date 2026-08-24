using System.Text.Json;

namespace PowerBase.Application.Fields;

/// <summary>
/// Per-type capability matrix for a field's General settings (Required / Unique / Default Value).
/// Mirrors the frontend's <c>field-general-settings.ts</c> helper so the Field Detail page and the API
/// agree on which of these three a given field type supports. Types not covered here (Reference, Lookup,
/// Summary, ReportLink, ActionButton*, or anything unrecognized) are left unrestricted — <see cref="Resolve"/>
/// returns null and <see cref="Validate"/> is a no-op for them, preserving their pre-existing behavior.
/// </summary>
public static class FieldGeneralSettingsCapability
{
    public readonly record struct Capabilities(bool Required, bool Unique, bool Default);

    private static readonly Dictionary<string, Capabilities> Base = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Text"] = new Capabilities(true, true, true),
        ["SingleSelect"] = new Capabilities(true, true, true),
        ["TextMultiLine"] = new Capabilities(true, false, true),
        ["RichText"] = new Capabilities(true, false, true),
        ["MultiSelect"] = new Capabilities(true, false, true),
        ["Number"] = new Capabilities(true, true, true),
        ["Currency"] = new Capabilities(true, true, true),
        ["Percent"] = new Capabilities(true, true, true),
        ["Rating"] = new Capabilities(true, true, true),
        ["NumericRange"] = new Capabilities(true, false, true),
        ["Date"] = new Capabilities(true, true, true),
        ["DateTime"] = new Capabilities(true, true, true),
        ["Time"] = new Capabilities(true, true, true),
        ["Duration"] = new Capabilities(true, false, true),
        ["DateRange"] = new Capabilities(true, false, true),
        ["Boolean"] = new Capabilities(true, false, true),
        ["Email"] = new Capabilities(true, true, true),
        ["Phone"] = new Capabilities(true, false, true),
        ["Url"] = new Capabilities(true, false, true),
        ["File"] = new Capabilities(true, false, false),
        ["Address"] = new Capabilities(false, false, false),
        ["User"] = new Capabilities(true, true, true),
        ["MultiUser"] = new Capabilities(true, false, true),
    };

    private static readonly HashSet<string> FormulaUniqueOnlyResultTypes =
        new(StringComparer.OrdinalIgnoreCase) { "Text", "Number", "Date", "DateTime", "Duration" };

    private static readonly HashSet<string> FormulaNoneResultTypes =
        new(StringComparer.OrdinalIgnoreCase) { "Bool", "User" };

    /// <summary>Resolves the capability set for a field, or null if the type isn't covered by the
    /// matrix — callers should treat null as "unrestricted".</summary>
    public static Capabilities? Resolve(string typeCode, string? settingsJson)
    {
        if (Base.TryGetValue(typeCode, out var cap)) return cap;

        var resultType = ResolveFormulaResultType(typeCode, settingsJson);
        if (resultType is null) return null;

        if (FormulaUniqueOnlyResultTypes.Contains(resultType)) return new Capabilities(false, true, false);
        if (FormulaNoneResultTypes.Contains(resultType)) return new Capabilities(false, false, false);
        return null;
    }

    /// <summary>Normalizes both Formula type-code conventions — typed <c>Formula_Text</c>/<c>Formula_Number</c>/...
    /// rows, and a generic <c>Formula</c> row with <c>Settings.resultType</c> — into a single result-type
    /// string, or null if <paramref name="typeCode"/> isn't a Formula field at all.</summary>
    private static string? ResolveFormulaResultType(string typeCode, string? settingsJson)
    {
        const string prefix = "Formula_";
        if (typeCode.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return typeCode[prefix.Length..];

        if (!string.Equals(typeCode, "Formula", StringComparison.OrdinalIgnoreCase))
            return null;

        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try
        {
            var dict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(settingsJson);
            var key = dict?.Keys.FirstOrDefault(k => string.Equals(k, "resultType", StringComparison.OrdinalIgnoreCase));
            if (key is null || dict is null) return null;
            return dict[key].ValueKind == JsonValueKind.String ? dict[key].GetString() : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// Validates a proposed Required/Unique/DefaultValue combination against the field's type. Only
    /// rejects a capability being turned ON — leaving it off/blank is always allowed, so an existing row
    /// carrying a legacy/inapplicable value is never broken by this check. Also does light shape
    /// validation of the structured DefaultValue encodings (Boolean/Range/User/MultiUser) it understands.
    /// <paramref name="newIsUnique"/> is null where Unique isn't settable at all (field creation).
    /// </summary>
    public static IDictionary<string, string[]> Validate(
        string typeCode, string? settingsJson, string label,
        bool newIsRequired, bool? newIsUnique, string? newDefaultValue)
    {
        var errors = new Dictionary<string, string[]>();
        var cap = Resolve(typeCode, settingsJson);
        var hasDefault = !string.IsNullOrWhiteSpace(newDefaultValue);

        if (cap is { } c)
        {
            if (newIsRequired && !c.Required)
                errors["IsRequired"] = [$"'{label}' fields of this type cannot be marked Required."];
            if (newIsUnique == true && !c.Unique)
                errors["IsUnique"] = [$"'{label}' fields of this type cannot be marked Unique."];
            if (hasDefault && !c.Default)
                errors["DefaultValue"] = [$"'{label}' fields of this type do not support a default value."];
        }

        if (hasDefault && !errors.ContainsKey("DefaultValue"))
        {
            var shapeError = ValidateDefaultValueShape(typeCode, newDefaultValue!);
            if (shapeError is not null)
                errors["DefaultValue"] = [shapeError];
        }

        return errors;
    }

    private static string? ValidateDefaultValueShape(string typeCode, string value) => typeCode switch
    {
        "Boolean" => value is "true" or "false" ? null : "Default value must be true or false.",
        "NumericRange" or "DateRange" => ValidateJsonObjectShape(value),
        "User" => ValidateUserDefaultShape(value, allowSpecificUser: true),
        "MultiUser" => ValidateUserDefaultShape(value, allowSpecificUser: false),
        _ => null,
    };

    private static string? ValidateJsonObjectShape(string value)
    {
        try
        {
            using var doc = JsonDocument.Parse(value);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? null : "Default value must be a JSON object.";
        }
        catch (JsonException) { return "Default value is not valid JSON."; }
    }

    private static string? ValidateUserDefaultShape(string value, bool allowSpecificUser)
    {
        try
        {
            using var doc = JsonDocument.Parse(value);
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("mode", out var modeEl)
                || modeEl.ValueKind != JsonValueKind.String)
                return "Default value must be a JSON object with a 'mode' property.";

            var mode = modeEl.GetString();
            var allowed = allowSpecificUser
                ? new[] { "None", "CurrentUser", "SpecificUser" }
                : new[] { "None", "CurrentUser" };
            if (mode is null || !allowed.Contains(mode, StringComparer.OrdinalIgnoreCase))
                return $"Default value 'mode' must be one of: {string.Join(", ", allowed)}.";

            if (string.Equals(mode, "SpecificUser", StringComparison.OrdinalIgnoreCase))
            {
                if (!doc.RootElement.TryGetProperty("userPublicId", out var idEl)
                    || idEl.ValueKind != JsonValueKind.String
                    || !Guid.TryParse(idEl.GetString(), out _))
                    return "Default value must include a valid 'userPublicId' when mode is SpecificUser.";
            }
            return null;
        }
        catch (JsonException) { return "Default value is not valid JSON."; }
    }
}
