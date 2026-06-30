using System.Text.Json;
using PowerBase.Domain.Constants;
using PowerBase.Domain.FieldSettings;
using PowerBase.Formula.Types;

namespace PowerBase.Application.Formulas;

/// <summary>
/// Maps PowerBase field metadata onto the engine's <see cref="FormulaType"/> system.
/// Lives in the host (not the Domain-pure engine) because it knows about TypeCodes
/// and field settings.
/// </summary>
internal static class FormulaTypeMap
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNameCaseInsensitive = true };

    /// <summary>
    /// The <see cref="FormulaType"/> a field's values present to a formula, or null
    /// when the field type cannot be referenced in a scalar formula (File, Address,
    /// ranges). For Formula fields, the declared result type is used.
    /// </summary>
    public static FormulaType? FieldType(string typeCode, string? settingsJson) => typeCode switch
    {
        "Text" or "TextMultiLine" or "RichText" or "SingleSelect" or "MultiSelect"
            or "Email" or "Phone" or "Url" or "Time" => FormulaType.Text,
        "Number" or "Currency" or "Percent" or "Rating" => FormulaType.Number,
        "Date" => FormulaType.Date,
        "DateTime" => FormulaType.DateTime,
        "Duration" => FormulaType.Duration,
        "Boolean" => FormulaType.Bool,
        "User" => FormulaType.User,
        "MultiUser" => FormulaType.UserList,
        "Formula" => ResultType(ParseSettings(settingsJson)?.ResultType),
        "Lookup" => LookupResultType(settingsJson),
        "Summary" => SummaryResultType(settingsJson),
        _ => null,
    };

    /// <summary>A Lookup presents the FormulaType of the parent field it pulls down (captured at creation).</summary>
    private static FormulaType? LookupResultType(string? settingsJson)
    {
        var s = ParseLookupSettings(settingsJson);
        return string.IsNullOrWhiteSpace(s?.SourceTypeCode) ? FormulaType.Text : FieldType(s!.SourceTypeCode!, null);
    }

    /// <summary>Count/Sum/Avg present as Number; Min/Max present as the aggregated field's type.</summary>
    private static FormulaType? SummaryResultType(string? settingsJson)
    {
        var s = ParseSummarySettings(settingsJson);
        if (s is null) return FormulaType.Number;
        return s.Function switch
        {
            "Exists" => FormulaType.Bool,
            "Min" or "Max" when !string.IsNullOrWhiteSpace(s.TargetTypeCode) => FieldType(s.TargetTypeCode!, null),
            _ => FormulaType.Number,
        };
    }

    /// <summary>Maps a <see cref="FormulaSettings.ResultType"/> string to a FormulaType (defaults to Text).</summary>
    public static FormulaType ResultType(string? resultType) =>
        Enum.TryParse<FormulaType>(resultType, ignoreCase: true, out var t) ? t : FormulaType.Text;

    /// <summary>Parses an optional expected-result-type string, or null when absent/unrecognised.</summary>
    public static FormulaType? ParseExpected(string? resultType) =>
        string.IsNullOrWhiteSpace(resultType) ? null
        : Enum.TryParse<FormulaType>(resultType, ignoreCase: true, out var t) ? t : null;

    public static FormulaSettings? ParseSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try { return JsonSerializer.Deserialize<FormulaSettings>(settingsJson, JsonOpts); }
        catch (JsonException) { return null; }
    }

    public static LookupSettings? ParseLookupSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try { return JsonSerializer.Deserialize<LookupSettings>(settingsJson, JsonOpts); }
        catch (JsonException) { return null; }
    }

    public static SummarySettings? ParseSummarySettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try { return JsonSerializer.Deserialize<SummarySettings>(settingsJson, JsonOpts); }
        catch (JsonException) { return null; }
    }

    public static ReferenceSettings? ParseReferenceSettings(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try { return JsonSerializer.Deserialize<ReferenceSettings>(settingsJson, JsonOpts); }
        catch (JsonException) { return null; }
    }

    /// <summary>The template of a Url field configured as a formula variant, or null otherwise.</summary>
    public static string? UrlFormulaTemplate(string? settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson)) return null;
        try
        {
            var s = JsonSerializer.Deserialize<UrlSettings>(settingsJson, JsonOpts);
            return s?.Variant == UrlVariants.Formula && !string.IsNullOrWhiteSpace(s.Template) ? s.Template : null;
        }
        catch (JsonException) { return null; }
    }

    /// <summary>
    /// A field whose value is computed at read time and has no physical column: a Formula,
    /// Lookup or Summary field, or a Url field with a formula template. Used for the
    /// in-memory filter/sort split (these fields cannot be filtered/sorted in SQL).
    /// </summary>
    public static bool IsComputedField(string typeCode, string? settingsJson) =>
        PhysicalNaming.IsComputedTypeCode(typeCode) || (typeCode == "Url" && UrlFormulaTemplate(settingsJson) != null);

    /// <summary>
    /// A field computed specifically by the formula engine: a Formula field, or a Url field
    /// with a formula template. Excludes Lookup/Summary (computed by the relationship
    /// projector instead), so the formula projector never tries to compile them.
    /// </summary>
    public static bool IsFormulaComputed(string typeCode, string? settingsJson) =>
        typeCode == "Formula" || (typeCode == "Url" && UrlFormulaTemplate(settingsJson) != null);
}
