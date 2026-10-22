using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships;

/// <summary>
/// The type of value a field holds, as a field type code (Text, Number, Currency, Date, Boolean, …) — what a Summary
/// or Lookup saves as its ResultTypeCode (<see cref="SummarySettings.ResultTypeCode"/>,
/// <see cref="LookupSettings.ResultTypeCode"/>), so a client can type it without reading other tables: a lookup of
/// "Min of Salary" holds a number. A stored field holds its own type; a Formula what it returns; a Summary what its
/// function produces; a Lookup what its source holds. Null when a field holds no value (Report Link, action buttons)
/// or its type can't be told from what is at hand.
/// </summary>
public static class FieldValueTypes
{
    /// <summary>The type of value <paramref name="field"/> holds — a Summary's / Lookup's saved ResultTypeCode when
    /// it has one, otherwise worked out from its settings.</summary>
    public static string? Of(AppField field)
    {
        if (SummaryLookupSources.IsLookup(field))
        {
            var s = FormulaTypeMap.ParseLookupSettings(field.Settings);
            if (!string.IsNullOrWhiteSpace(s?.ResultTypeCode)) return s.ResultTypeCode;
            if (!string.IsNullOrWhiteSpace(s?.SourceSubField)) return "Text";   // one part of an address
            // A stored or Formula_{X} source says it all; a Summary / generic Formula source needs that field.
            return s?.SourceTypeCode is { } source && source is not ("Formula" or "Summary" or "Lookup")
                ? OfTypeCode(source, null)
                : null;
        }
        if (field.TypeCode == nameof(Domain.Enums.FieldTypeCode.Summary))
        {
            var s = FormulaTypeMap.ParseSummarySettings(field.Settings);
            if (!string.IsNullOrWhiteSpace(s?.ResultTypeCode)) return s.ResultTypeCode;
            var target = s?.TargetTypeCode is { } t && t is not ("Formula" or "Summary" or "Lookup") ? OfTypeCode(t, null) : null;
            return ForSummary(s?.Function, target, s?.TargetSubField);
        }
        return OfTypeCode(field.TypeCode, field.Settings);
    }

    /// <summary>The type of value a summary target holds: a lookup through its source (<paramref name="lookupSources"/>,
    /// see <see cref="SummaryLookupSources.LoadAsync"/>) when known, anything else as <see cref="Of"/>.</summary>
    public static string? OfTarget(AppField target, IReadOnlyDictionary<long, AppField>? lookupSources)
    {
        if (SummaryLookupSources.IsLookup(target) && target.Fid is int fid && lookupSources?.GetValueOrDefault(fid) is { } source)
            return !string.IsNullOrWhiteSpace(SummaryLookupSources.SourceSubField(target)) ? "Text" : Of(source) ?? Of(target);
        return Of(target);
    }

    /// <summary>The type of value a summary produces: Number for Count / Distinct Count, Boolean for True / False, Text
    /// for Combined Text, and the target's value type (<paramref name="targetValueType"/>) for Sum / Avg / Min / Max —
    /// text for one part of an address.</summary>
    public static string? ForSummary(string? function, string? targetValueType, string? targetSubField) =>
        SummaryFunctions.Normalize(function) switch
        {
            SummaryFunctions.Count or SummaryFunctions.DistinctCount => "Number",
            SummaryFunctions.Exists => "Boolean",
            SummaryFunctions.CombinedText => "Text",
            SummaryFunctions.Sum or SummaryFunctions.Avg or SummaryFunctions.Min or SummaryFunctions.Max =>
                !string.IsNullOrWhiteSpace(targetSubField) ? "Text" : targetValueType,
            _ => null,
        };

    /// <summary>Saves <paramref name="resultTypeCode"/> on every lookup that pulls <paramref name="source"/> down to a
    /// child table — and on the lookups of those, down a chain — when it differs from what they have: a summary edited
    /// from "Min of Salary" (Currency) to "Min of Experience" (Number). Returns how many lookups were updated.</summary>
    public static async Task<int> RefreshLookupsOfAsync(
        AppField source, string? resultTypeCode, IRelationshipRepository relRepo, IAppFieldRepository fieldRepo,
        CancellationToken ct, int depth = 0)
    {
        if (source.Fid is not int sourceFid || depth >= LookupChain.MaxLength) return 0;
        var updated = 0;
        foreach (var rel in await relRepo.ListByParentTableAsync(source.AppTableId, ct))
        {
            foreach (var lookup in await fieldRepo.ListByTableAsync(rel.ChildTableId, ct))
            {
                if (!SummaryLookupSources.IsLookup(lookup)) continue;
                var s = FormulaTypeMap.ParseLookupSettings(lookup.Settings);
                if (s is null || s.SourceTableId != source.AppTableId || s.SourceFid != sourceFid
                    || !string.IsNullOrWhiteSpace(s.SourceSubField) || s.ResultTypeCode == resultTypeCode)
                    continue;
                s.ResultTypeCode = resultTypeCode;
                await fieldRepo.UpdateSettingsAsync(lookup.Id, RelationshipFieldFactory.Serialize(s), ct);
                updated += 1 + await RefreshLookupsOfAsync(lookup, resultTypeCode, relRepo, fieldRepo, ct, depth + 1);
            }
        }
        return updated;
    }

    /// <summary>A field type code (with a generic Formula's settings) as the type of value it holds: a stored type as
    /// itself, a Formula as what it returns.</summary>
    private static string? OfTypeCode(string typeCode, string? settings)
    {
        if (typeCode == "Formula") return OfFormulaResult(FormulaTypeMap.ParseSettings(settings)?.ResultType ?? "Text");
        if (PhysicalNaming.IsFormulaVariantTypeCode(typeCode)) return OfFormulaResult(typeCode["Formula_".Length..]);
        if (typeCode is "Lookup" or "Summary" or "ReportLink" || PhysicalNaming.IsActionButtonTypeCode(typeCode)) return null;
        return typeCode;
    }

    /// <summary>A formula result ("Number", "Bool", "ListUser" …) as the field type that holds such a value.</summary>
    private static string? OfFormulaResult(string result) => result.ToLowerInvariant() switch
    {
        "text" => "Text",
        "number" => "Number",
        "date" => "Date",
        "datetime" => "DateTime",
        "duration" => "Duration",
        "time" => "Time",
        "bool" or "boolean" => "Boolean",
        "user" => "User",
        "listuser" => "MultiUser",
        "email" => "Email",
        "phone" => "Phone",
        "url" => "Url",
        "richtext" => "RichText",
        "multiselect" => "MultiSelect",
        _ => null,
    };
}
