using System.Text.Json;

namespace PowerBase.Application.Fields;

/// <summary>
/// Per-type default matrix for a field's Advanced settings (Searchable / Sortable / Reportable /
/// Filterable / Auditable), applied when a field is first created. All five stay togglable for
/// every field type after creation (Update never restricts them) — only their *starting* value
/// depends on TypeCode, replacing the single fixed set of defaults every type used to get.
/// Types not covered here (Reference/Lookup/Summary, or anything unrecognized) resolve to null —
/// callers should fall back to the pre-existing fixed defaults for those.
/// </summary>
public static class FieldAdvancedSettingsCapability
{
    public readonly record struct Defaults(bool Searchable, bool Sortable, bool Reportable, bool Filterable, bool Auditable);

    private static readonly Dictionary<string, Defaults> Base = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Text"] = new Defaults(true, true, true, true, false),
        ["TextMultiLine"] = new Defaults(false, false, true, false, false),
        ["RichText"] = new Defaults(true, false, true, true, false),
        ["SingleSelect"] = new Defaults(true, true, true, true, false),
        ["MultiSelect"] = new Defaults(true, false, true, true, false),
        ["Number"] = new Defaults(false, true, true, false, false),
        ["NumericRange"] = new Defaults(false, false, true, false, false),
        ["Currency"] = new Defaults(false, true, true, false, false),
        ["Percent"] = new Defaults(false, true, true, false, false),
        ["Rating"] = new Defaults(false, true, true, false, false),
        ["Date"] = new Defaults(true, true, true, true, false),
        ["DateRange"] = new Defaults(false, false, true, false, false),
        ["Time"] = new Defaults(false, true, true, false, false),
        ["DateTime"] = new Defaults(false, true, true, false, false),
        ["Duration"] = new Defaults(false, false, true, false, false),
        ["Boolean"] = new Defaults(true, true, true, true, false),
        ["Url"] = new Defaults(false, false, true, false, false),
        ["File"] = new Defaults(false, false, true, false, false),
        ["Address"] = new Defaults(true, false, true, true, false),
        ["Phone"] = new Defaults(false, false, true, false, false),
        ["Email"] = new Defaults(true, true, true, true, false),
        ["User"] = new Defaults(true, true, true, true, false),
        ["MultiUser"] = new Defaults(true, false, true, true, false),
        ["ReportLink"] = new Defaults(false, false, true, false, false),
        ["ActionButton_File"] = new Defaults(false, false, true, false, false),
        ["ActionButton_Signature"] = new Defaults(false, false, true, false, false),
        ["ActionButton_Prompt"] = new Defaults(false, false, true, false, false),
        ["ActionButton_Data"] = new Defaults(false, false, true, false, false),

        // Formula_{ResultType} variants — keyed directly since Resolve() below normalizes both a
        // typed "Formula_Text" TypeCode and a generic "Formula" TypeCode + Settings.resultType down
        // to the same "Formula_{ResultType}" lookup key.
        ["Formula_Text"] = new Defaults(true, true, true, true, false),
        ["Formula_Number"] = new Defaults(false, true, true, false, false),
        ["Formula_Date"] = new Defaults(true, true, true, true, false),
        ["Formula_DateTime"] = new Defaults(false, true, true, false, false),
        ["Formula_Time"] = new Defaults(false, true, true, false, false),
        ["Formula_Duration"] = new Defaults(false, false, true, false, false),
        ["Formula_Bool"] = new Defaults(true, true, true, true, false),
        ["Formula_Phone"] = new Defaults(false, false, true, false, false),
        ["Formula_Email"] = new Defaults(true, true, true, true, false),
        ["Formula_User"] = new Defaults(true, true, true, true, false),
        ["Formula_RichText"] = new Defaults(true, false, true, true, false),
        ["Formula_Url"] = new Defaults(false, false, true, false, false),
    };

    /// <summary>Resolves the creation-time defaults for a field, or null if the type isn't covered
    /// by the matrix — callers should fall back to their own pre-existing default behavior.</summary>
    public static Defaults? Resolve(string typeCode, string? settingsJson)
    {
        if (Base.TryGetValue(typeCode, out var defaults)) return defaults;

        var resultType = ResolveFormulaResultType(typeCode, settingsJson);
        if (resultType is null) return null;

        return Base.TryGetValue($"Formula_{resultType}", out var formulaDefaults) ? formulaDefaults : null;
    }

    /// <summary>Normalizes both Formula type-code conventions — typed <c>Formula_Text</c>/<c>Formula_Number</c>/...
    /// rows, and a generic <c>Formula</c> row with <c>Settings.resultType</c> — into a single result-type
    /// string, or null if <paramref name="typeCode"/> isn't a Formula field at all. Mirrors the equivalent
    /// helper in <see cref="FieldGeneralSettingsCapability"/>.</summary>
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
}
