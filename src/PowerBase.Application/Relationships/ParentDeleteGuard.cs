using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;

namespace PowerBase.Application.Relationships;

/// <summary>Enforces the one-to-many "restrict" delete rule: a parent record cannot be deleted
/// while child records still reference it.</summary>
public static class ParentDeleteGuard
{
    public static async Task EnsureNotReferencedAsync(
        AppTable parentTable,
        IReadOnlyList<Relationship> parentRelationships,
        IReadOnlyCollection<long> parentIds,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IRecordRepository recordRepo,
        CancellationToken ct)
    {
        if (parentRelationships.Count == 0 || parentIds.Count == 0) return;

        // The value a child's reference column actually stores: the row Id for the default key, or
        // this table's key-field value for a Set-Key table.
        var keyField = await KeyFieldResolver.ResolveAsync(parentTable, fieldRepo, ct);
        IReadOnlyCollection<object> parentKeyValues;
        if (keyField is null)
        {
            parentKeyValues = parentIds.Select(id => (object)id).ToList();
        }
        else
        {
            var col = KeyFieldResolver.ColumnName(keyField);
            var keyValues = await recordRepo.GetColumnValuesByIdsAsync(parentTable, col, parentIds, ct);
            parentKeyValues = keyValues.Values.Where(v => v is not null).Select(v => v!).ToList();
        }
        if (parentKeyValues.Count == 0) return;

        foreach (var rel in parentRelationships)
        {
            var childTable = await tableRepo.GetByIdAsync(rel.ChildTableId, ct);
            var counts = await recordRepo.AggregateByReferenceAsync(
                childTable, rel.ReferenceFid, SummaryFunctions.Count, targetFid: null, parentKeyValues, filterTree: null, ct);
            long total = counts.Values.Sum(v => v is null ? 0L : Convert.ToInt64(v));
            if (total > 0)
                throw new ConflictException(
                    $"Cannot delete: {total} {childTable.Name} record(s) still reference this {parentTable.Name}. " +
                    "Delete or reassign those records first.");
        }
    }
}
