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
        IRecordRepository recordRepo,
        CancellationToken ct)
    {
        if (parentRelationships.Count == 0 || parentIds.Count == 0) return;

        // A child's reference column ALWAYS stores the parent's row Id, even for a Set-Key table (the
        // key field only changes what the picker and grid show), so match on the row Ids themselves.
        IReadOnlyCollection<object> parentKeyValues = parentIds.Select(id => (object)id).ToList();

        foreach (var rel in parentRelationships)
        {
            var childTable = await tableRepo.GetByIdAsync(rel.ChildTableId, ct);
            var counts = await recordRepo.AggregateByReferenceAsync(
                childTable, rel.ReferenceFid, SummaryFunctions.Count, targetFid: null, parentKeyValues, filterTree: null, ct: ct);
            long total = counts.Values.Sum(v => v is null ? 0L : Convert.ToInt64(v));
            if (total > 0)
                throw new ConflictException(
                    $"Cannot delete: {total} {childTable.Name} record(s) still reference this {parentTable.Name}. " +
                    "Delete or reassign those records first.");
        }
    }
}
