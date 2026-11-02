using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Relationships;

/// <summary>Which parent fields are keys and so must stay unique. A relationship's key is its own display-key
/// override, else the parent table's key field (same precedence as <see cref="KeyFieldResolver.ResolveDisplayKey"/>).
/// The table's key field is locked even while no relationship uses it: Set Key promises a unique key, and a
/// relationship created later takes it as it is (CreateRelationshipCommandHandler checks only an override).</summary>
public static class RelationshipKeyRules
{
    /// <summary>The relationships whose parent key is <paramref name="field"/>. <paramref name="relationships"/> is
    /// every relationship of <paramref name="table"/> (IRelationshipRepository.ListByTableAsync).</summary>
    public static IReadOnlyList<Relationship> KeyUsages(AppTable table, AppField field, IEnumerable<Relationship> relationships) =>
        relationships
            .Where(r => r.DisplayKeyFieldId == field.Id
                || (r.DisplayKeyFieldId is null && r.ParentTableId == table.Id && table.KeyFieldId == field.Id))
            .ToList();

    /// <summary>True when Unique can't be turned off on <paramref name="field"/>: it is the table's key field, or
    /// some relationship's display-key override.</summary>
    public static bool IsUniqueLocked(AppTable table, AppField field, IEnumerable<Relationship> relationships) =>
        table.KeyFieldId == field.Id || relationships.Any(r => r.DisplayKeyFieldId == field.Id);

    /// <summary>"Employees → Department" for each relationship, in the order given.</summary>
    public static async Task<IReadOnlyList<string>> DescribeAsync(
        IEnumerable<Relationship> relationships, AppTable parent, IAppTableRepository tableRepo, CancellationToken ct)
    {
        var names = new List<string>();
        foreach (var r in relationships)
        {
            var child = r.ChildTableId == parent.Id ? parent : await tableRepo.GetByIdAsync(r.ChildTableId, ct);
            names.Add($"{child.Name} → {parent.Name}");
        }
        return names;
    }
}
