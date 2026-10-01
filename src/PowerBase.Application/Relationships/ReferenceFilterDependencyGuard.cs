using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships;

/// <summary>Blocks deleting a field that controls a Reference field's dependent dropdown
/// (<see cref="Domain.FieldSettings.ReferenceSettings.FilterConditions"/>): with the controlling field gone
/// the condition could never be evaluated. The admin removes the filter first.</summary>
public static class ReferenceFilterDependencyGuard
{
    public static void EnsureNotControlling(
        AppTable table,
        IReadOnlyList<AppField> tableFields,
        IEnumerable<AppField> fieldsToDelete)
    {
        var deleting = fieldsToDelete.Where(f => f.Fid.HasValue).ToList();
        if (deleting.Count == 0) return;

        var deletingIds = deleting.Select(f => f.Id).ToHashSet();
        foreach (var reference in tableFields.Where(f => f.TypeCode == "Reference" && !deletingIds.Contains(f.Id)))
        {
            var conditions = FormulaTypeMap.ParseReferenceSettings(reference.Settings)?.FilterConditions;
            if (conditions is not { Count: > 0 }) continue;

            var blocker = deleting.FirstOrDefault(d => conditions.Any(c => c.FormFid == d.Fid));
            if (blocker is not null)
                throw new ConflictException(
                    $"Cannot delete field '{blocker.Label ?? blocker.Name}': it controls the dropdown values of " +
                    $"'{reference.Label ?? reference.Name}' on {table.Name}. Remove that dropdown filter first.");
        }
    }
}
