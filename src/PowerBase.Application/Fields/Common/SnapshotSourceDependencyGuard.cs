using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Fields.Common;

/// <summary>A field that a Snapshot field copies its value from cannot be deleted while that Snapshot is set up —
/// otherwise every new record would capture a blank. Deleting both together is fine.</summary>
public static class SnapshotSourceDependencyGuard
{
    public static void EnsureNotSource(IReadOnlyList<AppField> tableFields, IReadOnlyList<AppField> deleting)
    {
        var deletingIds = deleting.Select(f => f.Id).ToHashSet();
        var errors = new Dictionary<string, string[]>();
        foreach (var field in deleting.Where(f => f.Fid.HasValue))
        {
            var dependents = tableFields
                .Where(t => !t.IsDeleted && t.SnapshotSourceFid == field.Fid && !deletingIds.Contains(t.Id))
                .Select(t => t.Label ?? t.Name)
                .ToList();
            if (dependents.Count == 0) continue;
            errors[$"Field{field.Fid}"] =
            [
                $"Cannot delete '{field.Label ?? field.Name}' because it is the source of the Snapshot field(s): {string.Join(", ", dependents)}. " +
                "Turn Snapshot off on those fields first."
            ];
        }
        if (errors.Count > 0)
            throw new ValidationException(errors.AsReadOnly());
    }
}
