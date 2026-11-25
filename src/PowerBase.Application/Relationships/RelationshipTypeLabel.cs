using System.Text.Json;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Relationships;

/// <summary>
/// How a Reference, Lookup or Summary field's type is named to people, the Quickbase way: the type of value it holds,
/// with where the value comes from as a tag — "Currency (Lookup)", "Number (Reference)", "Number (Summary)". The type is
/// the field's saved <c>resultTypeCode</c> (ReferenceSettings / LookupSettings / SummarySettings). A lookup or summary of
/// a Reference saves "Reference" but shows the parent's Record ID#, so it is named Number. A field that saved no type
/// (one made before it was kept) is named by its tag alone. Every other field is named by its own type code.
/// The frontend twin is <c>fieldTypeDisplay</c> in field-type-helpers.ts.
/// </summary>
public static class RelationshipTypeLabel
{
    /// <summary>True for the field types named with a tag: Reference, Lookup and Summary.</summary>
    public static bool IsTagged(string? typeCode) =>
        typeCode is nameof(Domain.Enums.FieldTypeCode.Reference)
            or nameof(Domain.Enums.FieldTypeCode.Lookup)
            or nameof(Domain.Enums.FieldTypeCode.Summary);

    /// <summary>The type a Reference / Lookup / Summary is shown as — its saved resultTypeCode, "Reference" read as
    /// Number — or null for any other field and for one that saved no type.</summary>
    public static string? ValueType(string? typeCode, string? settingsJson)
    {
        if (!IsTagged(typeCode) || string.IsNullOrWhiteSpace(settingsJson)) return null;
        string? saved = null;
        try
        {
            using var doc = JsonDocument.Parse(settingsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            foreach (var p in doc.RootElement.EnumerateObject())
                if (p.Name.Equals("resultTypeCode", StringComparison.OrdinalIgnoreCase) && p.Value.ValueKind == JsonValueKind.String)
                    saved = p.Value.GetString();
        }
        catch (JsonException) { return null; }
        if (string.IsNullOrWhiteSpace(saved)) return null;
        return saved == nameof(Domain.Enums.FieldTypeCode.Reference) ? nameof(Domain.Enums.FieldTypeCode.Number) : saved;
    }

    /// <summary>The field's type as people read it: "Currency (Lookup)" for a tagged field with a saved type, the bare tag
    /// ("Lookup") without one, the type code itself for any other field.</summary>
    public static string Of(string typeCode, string? settingsJson) =>
        !IsTagged(typeCode) ? typeCode
        : ValueType(typeCode, settingsJson) is { } valueType ? $"{valueType} ({typeCode})"
        : typeCode;

    /// <inheritdoc cref="Of(string, string?)"/>
    public static string Of(AppField field) => Of(field.TypeCode, field.Settings);
}
