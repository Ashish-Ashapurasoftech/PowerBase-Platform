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
        _ => null,
    };

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

    /// <summary>A field whose value is computed at read time: a Formula field, or a Url field with a formula template.</summary>
    public static bool IsComputedField(string typeCode, string? settingsJson) =>
        PhysicalNaming.IsComputedTypeCode(typeCode) || (typeCode == "Url" && UrlFormulaTemplate(settingsJson) != null);
}
