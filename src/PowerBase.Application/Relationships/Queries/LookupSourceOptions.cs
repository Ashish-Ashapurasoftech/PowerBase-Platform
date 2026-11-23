using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships.Queries;

/// <summary>One parent field a lookup may pull (Lookup field options → "Value field").</summary>
public record LookupValueFieldOption(int Fid, string Label, string TypeCode, string? ResultTypeCode);

/// <summary>One Reference field on the lookup's table (Lookup field options → "Reference field"): the relationship it
/// stands for, its parent ("foreign") table, and the parent fields compatible with the lookup's current type.</summary>
public record LookupReferenceOption(
    Guid RelationshipPublicId,
    int ReferenceFid,
    string ReferenceFieldLabel,
    Guid ParentTablePublicId,
    string ParentTableName,
    IReadOnlyList<LookupValueFieldOption> ValueFields);

/// <summary>What the "Lookup field options" section of a Lookup field shows: the current choice (with whether it still
/// exists — a deleted reference / relationship / value field leaves the lookup pointing at nothing) and the options.</summary>
public record LookupSourceOptionsDto(
    Guid? CurrentRelationshipPublicId,
    int? CurrentReferenceFid,
    int? CurrentSourceFid,
    string? CurrentSourceLabel,
    string? ResultTypeCode,
    bool DisplayAsLinkToParent,
    bool RelationshipMissing,
    bool SourceFieldMissing,
    IReadOnlyList<LookupReferenceOption> References);

public class GetLookupSourceOptionsQueryHandler
{
    private readonly IRelationshipRepository _relRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;

    public GetLookupSourceOptionsQueryHandler(IRelationshipRepository relRepo, IAppTableRepository tableRepo, IAppFieldRepository fieldRepo)
    {
        _relRepo = relRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
    }

    public async Task<LookupSourceOptionsDto> HandleAsync(Guid tablePublicId, Guid fieldPublicId, CancellationToken ct = default)
    {
        var table = await _tableRepo.GetByPublicIdAsync(tablePublicId, ct);
        var lookup = await _fieldRepo.GetByPublicIdAsync(fieldPublicId, ct);
        if (lookup is null || lookup.IsDeleted || lookup.AppTableId != table.Id || !SummaryLookupSources.IsLookup(lookup))
            throw new NotFoundException("Lookup field", fieldPublicId);

        var settings = FormulaTypeMap.ParseLookupSettings(lookup.Settings);
        var currentType = FieldValueTypes.Of(lookup);
        var childFields = await _fieldRepo.ListByTableAsync(table.Id, ct);

        var references = new List<LookupReferenceOption>();
        Relationship? currentRel = null;
        AppField? currentSource = null;
        foreach (var rel in await _relRepo.ListByChildTableAsync(table.Id, ct))
        {
            var reference = childFields.FirstOrDefault(f => f.Id == rel.ReferenceFieldId && !f.IsDeleted);
            if (reference is null) continue;   // a relationship whose reference field is gone can't be followed
            var parent = await _tableRepo.GetByIdAsync(rel.ParentTableId, ct);
            var parentFields = await _fieldRepo.ListByTableAsync(parent.Id, ct);
            if (rel.Id == settings?.RelationshipId)
            {
                currentRel = rel;
                currentSource = parentFields.FirstOrDefault(f => f.Fid == settings.SourceFid && !f.IsDeleted);
            }

            var values = new List<LookupValueFieldOption>();
            foreach (var f in parentFields)
            {
                if (!LookupSourceCompatibility.CanBeSource(f) || f.Id == lookup.Id) continue;
                var resultType = await TryResolveResultTypeAsync(f, lookup, ct);
                if (resultType is null) continue;   // chain too deep, or it loops back to this lookup
                // The field the lookup pulls now is always offered, so the current choice shows even when the parent
                // field's type was changed underneath it.
                var isCurrent = rel.Id == settings?.RelationshipId && f.Fid == settings.SourceFid;
                if (!isCurrent && !LookupSourceCompatibility.IsCompatible(currentType, resultType.Value.ResultType)) continue;
                values.Add(new LookupValueFieldOption(f.Fid!.Value, Display(f), f.TypeCode, resultType.Value.ResultType));
            }

            references.Add(new LookupReferenceOption(
                rel.PublicId, rel.ReferenceFid, Display(reference), parent.PublicId, parent.Name,
                values.OrderBy(v => v.Label, StringComparer.OrdinalIgnoreCase).ToList()));
        }

        return new LookupSourceOptionsDto(
            currentRel?.PublicId,
            settings?.ReferenceFid,
            settings?.SourceFid,
            currentSource is null ? null : Display(currentSource),
            currentType,
            settings?.DisplayAsLinkToParent ?? false,
            RelationshipMissing: currentRel is null,
            SourceFieldMissing: currentSource is null,
            references.OrderBy(r => r.ReferenceFieldLabel, StringComparer.OrdinalIgnoreCase).ToList());
    }

    /// <summary>The type of value <paramref name="source"/> would give the lookup, or null when it can't be its source
    /// (lookup chain too long, or the chain comes back to <paramref name="lookup"/> itself).</summary>
    private async Task<(string SourceType, string? ResultType)?> TryResolveResultTypeAsync(AppField source, AppField lookup, CancellationToken ct)
    {
        if (await LookupSourceRules.ChainReachesAsync(source, lookup.Id, _fieldRepo, ct)) return null;
        try
        {
            return await LookupChain.ResolveForNewLookupAsync(source, null, _fieldRepo, ct);
        }
        catch (ValidationException)
        {
            return null;
        }
    }

    private static string Display(AppField f) => string.IsNullOrWhiteSpace(f.Label) ? f.Name : f.Label!;
}
