using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Keeps the type a Reference is shown as (<see cref="Domain.FieldSettings.ReferenceSettings.ResultTypeCode"/>) in step
/// with the parent key it shows. A Reference made as a new field takes the type of the parent's key
/// (<see cref="FieldValueTypes.ForNewReference"/>); when that key changes — the parent's Set Key, the relationship's
/// Display Key, or the key field's own type (Number → Currency) — its saved type would go stale ("Number (Reference)"
/// while it now shows Department Name), so it is saved again here. A Reference converted from an existing child field
/// keeps that field's own type (<see cref="FieldValueTypes.ForConvertedReference"/>) and is left alone.
/// </summary>
public static class ReferenceResultTypeRefresher
{
    /// <summary>Saves the current parent-key type on every new-field Reference of a relationship whose parent is
    /// <paramref name="parentTableId"/>, where it differs from what is saved. Returns how many were updated.</summary>
    public static async Task<int> RefreshForParentAsync(
        long parentTableId, IRelationshipRepository relRepo, IAppTableRepository tableRepo, IAppFieldRepository fieldRepo,
        CancellationToken ct = default)
    {
        // Read the table fresh: the caller has just changed its key, and the one it holds may be from before that.
        if ((await relRepo.ListByParentTableAsync(parentTableId, ct)).All(r => r.ReferenceFieldIsExisting)) return 0;
        var parent = await tableRepo.GetByIdAsync(parentTableId, ct);
        return parent is null ? 0 : await RefreshForParentAsync(parent, relRepo, fieldRepo, ct);
    }

    /// <summary>As above, for a caller that holds the parent table with its current key (a change of a field's type
    /// leaves the table's key where it was).</summary>
    public static async Task<int> RefreshForParentAsync(
        AppTable parent, IRelationshipRepository relRepo, IAppFieldRepository fieldRepo, CancellationToken ct = default)
    {
        var relationships = (await relRepo.ListByParentTableAsync(parent.Id, ct))
            .Where(r => !r.ReferenceFieldIsExisting)
            .ToList();
        if (relationships.Count == 0) return 0;

        var parentFields = await fieldRepo.ListByTableAsync(parent.Id, ct);
        var childFieldsByTable = new Dictionary<long, IReadOnlyList<AppField>>();

        var updated = 0;
        foreach (var rel in relationships)
        {
            var wanted = FieldValueTypes.ForNewReference(KeyFieldResolver.ResolveDisplayKey(rel, parent, parentFields));

            if (!childFieldsByTable.TryGetValue(rel.ChildTableId, out var childFields))
                childFieldsByTable[rel.ChildTableId] = childFields = await fieldRepo.ListByTableAsync(rel.ChildTableId, ct);
            var reference = childFields.FirstOrDefault(f => f.Id == rel.ReferenceFieldId && !f.IsDeleted);
            if (reference is null || reference.TypeCode != nameof(Domain.Enums.FieldTypeCode.Reference)) continue;

            var settings = FormulaTypeMap.ParseReferenceSettings(reference.Settings);
            if (settings is null || string.Equals(settings.ResultTypeCode, wanted, StringComparison.Ordinal)) continue;

            settings.ResultTypeCode = wanted;
            await fieldRepo.UpdateSettingsAsync(reference.Id, RelationshipFieldFactory.Serialize(settings), ct);
            updated++;
        }
        return updated;
    }
}
