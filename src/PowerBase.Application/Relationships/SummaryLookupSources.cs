using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Enums;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Lookup fields in a Summary field's target, sort or matching criteria (Quickbase offers a child
/// table's lookups there too — e.g. summarizing Employees' "Department Name"). A lookup has no
/// column of its own: the summary SQL reads the parent column it pulls down, through the child's
/// reference (RecordRepository). So a lookup is judged by that parent field — it must still exist,
/// have a column (not be calculated itself) and not be encrypted.
/// </summary>
public static class SummaryLookupSources
{
    public static bool IsLookup(AppField field) => field.TypeCode == nameof(FieldTypeCode.Lookup);

    /// <summary>The parent field each of the child's lookups pulls down, keyed by the lookup's Fid.
    /// A lookup whose source is gone (or whose settings are incomplete) is absent. One field list
    /// per parent table, and none at all when the child has no lookups.</summary>
    public static async Task<IReadOnlyDictionary<long, AppField>> LoadAsync(
        IReadOnlyList<AppField> childFields, IAppFieldRepository fieldRepo, CancellationToken ct)
    {
        var result = new Dictionary<long, AppField>();
        var lookups = childFields
            .Where(f => f.Fid.HasValue && IsLookup(f))
            .Select(f => (Field: f, Settings: FormulaTypeMap.ParseLookupSettings(f.Settings)))
            .Where(x => x.Settings is { SourceTableId: not null, ReferenceFid: not null, SourceFid: not null })
            .ToList();
        foreach (var byTable in lookups.GroupBy(x => x.Settings!.SourceTableId!.Value))
        {
            var sourceFields = await fieldRepo.ListByTableAsync(byTable.Key, ct);
            foreach (var (field, settings) in byTable)
                if (sourceFields.FirstOrDefault(f => f.Fid == settings!.SourceFid) is { } source)
                    result[field.Fid!.Value] = source;
        }
        return result;
    }

    /// <summary>The field a summary reads for <paramref name="field"/>: the parent field of a lookup
    /// (null when it's gone or calculated — nothing to read), the field itself otherwise.</summary>
    public static AppField? ReadableSource(AppField field, IReadOnlyDictionary<long, AppField>? lookupSources)
    {
        if (!IsLookup(field)) return field;
        return field.Fid is int fid && lookupSources is not null && lookupSources.TryGetValue(fid, out var source)
               && !PhysicalNaming.IsComputedTypeCode(source.TypeCode)
            ? source
            : null;
    }

    /// <summary>True when a summary can read <paramref name="field"/> in SQL: a stored or system
    /// field, or a lookup of one.</summary>
    public static bool IsReadable(AppField field, IReadOnlyDictionary<long, AppField>? lookupSources) =>
        ReadableSource(field, lookupSources) is { } source && !PhysicalNaming.IsComputedTypeCode(source.TypeCode);

    /// <summary>The lookup's JSON sub-key (just the city of an Address), or null.</summary>
    public static string? SourceSubField(AppField field) => Settings(field)?.SourceSubField;

    /// <summary>A lookup's settings (which parent, through which reference, which field), or null
    /// for any other field — for the summary SQL, which reads the parent column.</summary>
    public static LookupSettings? Settings(AppField field) =>
        IsLookup(field) ? FormulaTypeMap.ParseLookupSettings(field.Settings) : null;

    /// <summary>The child fields as the value resolution of matching criteria should see them: a
    /// lookup takes its source's type, so a condition on a looked-up User field gets its picked
    /// user resolved like one on a User field of the child.</summary>
    public static IReadOnlyDictionary<long, AppField> WithSourceTypes(
        IReadOnlyDictionary<long, AppField> childFieldsByFid, IReadOnlyDictionary<long, AppField> lookupSources)
    {
        if (lookupSources.Count == 0) return childFieldsByFid;
        var result = new Dictionary<long, AppField>(childFieldsByFid);
        foreach (var (fid, source) in lookupSources)
            if (result.TryGetValue(fid, out var lookup))
                result[fid] = new AppField { Id = lookup.Id, Fid = lookup.Fid, Name = lookup.Name, Label = lookup.Label, TypeCode = source.TypeCode };
        return result;
    }
}
