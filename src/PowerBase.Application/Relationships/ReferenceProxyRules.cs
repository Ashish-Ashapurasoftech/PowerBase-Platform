using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Rules for a relationship's reference proxy (<see cref="Relationship.ProxyFieldId"/>): the child field that stands in
/// for the Reference on forms and reports, as Quickbase's "Proxy field" does. A proxy is a Lookup of this relationship
/// or a Formula field on the child table. The first lookup becomes the proxy by default; when the proxy field goes away
/// (deleted, or converted to a stored type) the role moves to the relationship's next lookup, or is cleared.
/// </summary>
public static class ReferenceProxyRules
{
    /// <summary>True when <paramref name="field"/> may be <paramref name="rel"/>'s proxy. A lookup of another
    /// relationship is refused: it would label the reference with a different parent's data.</summary>
    public static bool IsEligible(AppField field, Relationship rel) =>
        field.AppTableId == rel.ChildTableId && field.Fid.HasValue
        && (IsLookupOf(field, rel) || FormulaTypeMap.IsFormulaComputed(field.TypeCode, field.Settings));

    /// <summary>The default proxy: the relationship's first lookup, skipping <paramref name="excludeIds"/>
    /// (fields that are being removed). Null when the relationship has no other lookup.</summary>
    public static AppField? PickDefault(
        IEnumerable<AppField> childFields, Relationship rel, IReadOnlyCollection<long>? excludeIds = null) =>
        childFields.FirstOrDefault(f => IsLookupOf(f, rel) && (excludeIds is null || !excludeIds.Contains(f.Id)));

    /// <summary>For every relationship on <paramref name="childTableId"/> whose proxy is one of
    /// <paramref name="removedFieldIds"/>, hands the role to the next lookup, or clears it — so a proxy never points at
    /// a deleted field or one that can no longer be a proxy.</summary>
    public static async Task ReassignIfRemovedAsync(
        long childTableId,
        IReadOnlyCollection<long> removedFieldIds,
        IRelationshipRepository relRepo,
        IAppFieldRepository fieldRepo,
        CancellationToken ct)
    {
        if (removedFieldIds.Count == 0) return;

        var affected = (await relRepo.ListByChildTableAsync(childTableId, ct))
            .Where(r => r.ProxyFieldId is long proxyId && removedFieldIds.Contains(proxyId))
            .ToList();
        if (affected.Count == 0) return;

        var childFields = await fieldRepo.ListByTableAsync(childTableId, ct);
        foreach (var rel in affected)
            await relRepo.UpdateProxyFieldAsync(rel.Id, PickDefault(childFields, rel, removedFieldIds)?.Id, ct);
    }

    private static bool IsLookupOf(AppField field, Relationship rel) =>
        field.TypeCode == "Lookup" && FormulaTypeMap.ParseLookupSettings(field.Settings)?.RelationshipId == rel.Id;
}
