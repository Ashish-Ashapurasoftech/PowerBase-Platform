using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.FieldSettings;
using PowerBase.Application.Formulas;

namespace PowerBase.Application.Relationships;

/// <summary>
/// A Lookup saves the type of value it shows (<see cref="LookupSettings.ResultTypeCode"/>) so clients can type it without reading
/// other tables. When the parent field's type was changed before lookups followed it, that saved type went stale (a Date lookup of a
/// field that is now a Number). This re-reads each lookup's real source and saves the right type — the repair for data written
/// before <see cref="FieldValueTypes.RefreshLookupsOfAsync"/> ran on a type change.
/// </summary>
public class LookupResultTypeRepairService
{
    private readonly IAppFieldRepository _fieldRepo;

    public LookupResultTypeRepairService(IAppFieldRepository fieldRepo) => _fieldRepo = fieldRepo;

    /// <summary>Repairs every Lookup of <paramref name="tableFields"/>'s table. Returns how many were updated.</summary>
    public async Task<int> RepairTableAsync(IReadOnlyList<AppField> tableFields, CancellationToken ct = default)
    {
        var cache = new Dictionary<long, IReadOnlyList<AppField>>();
        var repaired = 0;
        foreach (var lookup in tableFields.Where(f => !f.IsDeleted && SummaryLookupSources.IsLookup(f)))
        {
            var settings = FormulaTypeMap.ParseLookupSettings(lookup.Settings);
            // One part of an address is always text, and a lookup with no source can't be typed: left alone.
            if (settings is null || !string.IsNullOrWhiteSpace(settings.SourceSubField)) continue;

            var actual = await ResolveAsync(lookup, cache, depth: 0, ct);
            if (string.IsNullOrWhiteSpace(actual) || string.Equals(settings.ResultTypeCode, actual, StringComparison.Ordinal)) continue;

            settings.ResultTypeCode = actual;
            await _fieldRepo.UpdateSettingsAsync(lookup.Id, RelationshipFieldFactory.Serialize(settings), ct);
            repaired++;
        }
        return repaired;
    }

    /// <summary>The type of value the lookup's source holds right now; a lookup of a lookup is followed down its chain.</summary>
    private async Task<string?> ResolveAsync(AppField lookup, Dictionary<long, IReadOnlyList<AppField>> cache, int depth, CancellationToken ct)
    {
        var settings = FormulaTypeMap.ParseLookupSettings(lookup.Settings);
        if (settings is not { SourceTableId: long sourceTableId, SourceFid: int sourceFid } || depth >= LookupChain.MaxLength) return null;

        if (!cache.TryGetValue(sourceTableId, out var sourceFields))
            cache[sourceTableId] = sourceFields = await _fieldRepo.ListByTableAsync(sourceTableId, ct);

        var source = sourceFields.FirstOrDefault(f => f.Fid == sourceFid && !f.IsDeleted);
        if (source is null) return null;   // the source field is gone: nothing to follow
        if (SummaryLookupSources.IsLookup(source))
            return !string.IsNullOrWhiteSpace(FormulaTypeMap.ParseLookupSettings(source.Settings)?.SourceSubField)
                ? "Text"
                : await ResolveAsync(source, cache, depth + 1, ct);
        return FieldValueTypes.Of(source);
    }
}
