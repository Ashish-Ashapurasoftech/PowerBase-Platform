using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Fields.TypeConversion;

/// <summary>
/// The Settings JSON and Default value a field gets when it changes type (docs/field-type-conversion.md).
/// Settings start from the new type's defaults, except where something must carry over:
/// a formula's expression (Formula → Formula), the numeric family's display options, and SingleSelect choices.
/// Pure — unit-tested without a database.
/// </summary>
public static class FieldTypeChangeSettings
{
    public static string? Build(AppField field, string targetTypeCode, ConversionKind kind, IReadOnlyList<string>? selectChoices = null)
    {
        switch (kind)
        {
            case ConversionKind.FormulaToFormula:
                // The expression is kept and recompiled as the new result type; display options reset.
                var expression = FormulaTypeMap.ExpressionAndType(field.TypeCode, field.Settings)?.Expression;
                if (string.IsNullOrWhiteSpace(expression)) return null;
                return new JsonObject { [targetTypeCode == "Formula_Url" ? "template" : "expression"] = expression }.ToJsonString();

            case ConversionKind.NumericFamily:
                // Same settings shape for the whole family. DisplayAs must name the new type, or the next ordinary
                // Save on the field page would switch the type straight back (UpdateFieldCommandHandler).
                return SetProperty(field.Settings, "displayAs", targetTypeCode);

            case ConversionKind.ScalarToFormula:
                // Never restore an old expression: the field starts as an empty formula.
                return null;

            default:
                if (targetTypeCode == "SingleSelect")
                    return new JsonObject { ["options"] = new JsonArray((selectChoices ?? []).Select(c => (JsonNode)c!).ToArray()) }.ToJsonString();
                return null;
        }
    }

    /// <summary>The field's Default value converted to the new type, or null when it can't be carried over.
    /// Only text-like and numeric defaults carry over; every other type starts without a default.</summary>
    public static string? BuildDefaultValue(AppField field, string targetTypeCode, FieldValueConversionContext context)
    {
        if (string.IsNullOrWhiteSpace(field.DefaultValue) || FieldTypeConversionMatrix.IsFormula(targetTypeCode)) return null;
        if (targetTypeCode is not ("Text" or "TextMultiLine" or "RichText" or "SingleSelect" or "Email" or "Url"
            or "Number" or "Currency" or "Percent" or "Rating")) return null;

        var converted = FieldValueConverter.Convert(field.DefaultValue, field.TypeCode, targetTypeCode, context).Value;
        return converted switch
        {
            null => null,
            decimal d => d.ToString("0.##########", CultureInfo.InvariantCulture),
            _ => Convert.ToString(converted, CultureInfo.InvariantCulture),
        };
    }

    /// <summary>Sets one property on a settings object, matching an existing key case-insensitively and keeping
    /// every other property as stored.</summary>
    internal static string SetProperty(string? settingsJson, string name, string value)
    {
        JsonObject obj;
        try
        {
            obj = string.IsNullOrWhiteSpace(settingsJson) ? new JsonObject() : JsonNode.Parse(settingsJson) as JsonObject ?? new JsonObject();
        }
        catch (JsonException)
        {
            obj = new JsonObject();
        }

        var existing = obj.Select(p => p.Key).FirstOrDefault(k => string.Equals(k, name, StringComparison.OrdinalIgnoreCase));
        obj[existing ?? name] = value;
        return obj.ToJsonString();
    }
}
