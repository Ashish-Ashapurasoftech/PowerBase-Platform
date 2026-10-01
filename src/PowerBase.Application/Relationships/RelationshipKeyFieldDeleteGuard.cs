using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships;

/// <summary>Blocks deleting a field a relationship is built on: a parent table's key field (e.g.
/// "Employee Code"), a relationship's display-key override, or the child's Reference field.
/// Deleting any of these silently breaks the link between parent and child records.</summary>
public static class RelationshipKeyFieldDeleteGuard
{
    public static async Task EnsureNotRelationshipKeyAsync(
        AppTable table,
        IEnumerable<AppField> fieldsToDelete,
        IRelationshipRepository relRepo,
        CancellationToken ct)
    {
        var fields = fieldsToDelete.ToList();
        if (fields.Count == 0) return;

        var relationships = await relRepo.ListByTableAsync(table.Id, ct);
        if (relationships.Count == 0) return;

        foreach (var field in fields)
        {
            var isKey = relationships.Any(r =>
                (r.ParentTableId == table.Id && table.KeyFieldId == field.Id)
                || r.DisplayKeyFieldId == field.Id
                || r.ReferenceFieldId == field.Id);

            if (isKey)
                throw new ConflictException(
                    $"Cannot delete field '{field.Label ?? field.Name}': it is the key of a relationship on {table.Name}. " +
                    "Delete the relationship or change its key first.");
        }
    }
}
