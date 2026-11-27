using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Relationships;

/// <summary>
/// Roles > Advanced Permissions: a relationship changes fields (and forms) of its two tables — the reference field and
/// lookups on the child; the Report Link / "Add …" link fields and summaries on the parent; the "{child} details" form
/// section on the parent's forms. So each relationship action needs the Fields / Forms permission on the table it
/// actually changes, not just somewhere in the app (the endpoints' own attribute only checks the app).
/// </summary>
public class RelationshipTableAccess(
    IAppAccessService access, IRelationshipRepository relationships, IAppTableRepository tables, IAppFieldRepository fields)
{
    public enum Side { Parent, Child, Both }

    /// <summary>The permission on the parent and/or child table of the relationship.</summary>
    public async Task RequireAsync(Guid relationshipPublicId, string permissionCode, Side side, CancellationToken ct)
    {
        var rel = await relationships.GetByPublicIdAsync(relationshipPublicId, ct)
            ?? throw new NotFoundException("Relationship", relationshipPublicId);
        if (side is Side.Parent or Side.Both)
            await RequireOnTableIdAsync(rel.ParentTableId, permissionCode, ct);
        if (side is Side.Child or Side.Both)
            await RequireOnTableIdAsync(rel.ChildTableId, permissionCode, ct);
    }

    /// <summary>The permission on the table that holds this field (a lookup / summary / link field of the relationship).</summary>
    public async Task RequireOnFieldTableAsync(Guid fieldPublicId, string permissionCode, CancellationToken ct)
    {
        var field = await fields.GetByPublicIdAsync(fieldPublicId, ct) ?? throw new NotFoundException("Field", fieldPublicId);
        await RequireOnTableIdAsync(field.AppTableId, permissionCode, ct);
    }

    /// <summary>The permission on these two tables (creating a relationship adds fields to both).</summary>
    public async Task RequireOnTablesAsync(Guid parentTablePublicId, Guid childTablePublicId, string permissionCode, CancellationToken ct)
    {
        await access.RequirePermissionByTablePublicIdAsync(parentTablePublicId, permissionCode, ct);
        if (childTablePublicId != parentTablePublicId)
            await access.RequirePermissionByTablePublicIdAsync(childTablePublicId, permissionCode, ct);
    }

    private async Task RequireOnTableIdAsync(long tableId, string permissionCode, CancellationToken ct)
    {
        var table = await tables.GetByIdAsync(tableId, ct);
        await access.RequirePermissionByTablePublicIdAsync(table.PublicId, permissionCode, ct);
    }
}
