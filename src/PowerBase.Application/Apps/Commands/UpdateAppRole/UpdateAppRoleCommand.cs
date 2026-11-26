namespace PowerBase.Application.Apps.Commands.UpdateAppRole;

public record UpdateAppRoleCommand(
    Guid AppPublicId,
    Guid RolePublicId,
    IReadOnlyList<string>? Permissions = null,
    string? ManageableRolesType = null,
    int? Rank = null,
    IReadOnlyList<Guid>? ManageableRolePublicIds = null,
    string? Name = null,
    // Roles > Advanced Permissions > Table selection: the tables each held builder permission does NOT apply to.
    // Null = leave the role's table selections as they are.
    IReadOnlyList<TableScopeExclusion>? TableExclusions = null);

/// <summary>Builder permission <see cref="Code"/> does not apply to this table for the role.</summary>
public record TableScopeExclusion(string Code, Guid TablePublicId);
