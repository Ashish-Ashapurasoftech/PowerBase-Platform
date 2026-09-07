namespace PowerBase.Application.Apps.Commands.UpdateAppRole;

public record UpdateAppRoleCommand(
    Guid AppPublicId,
    Guid RolePublicId,
    IReadOnlyList<string>? Permissions = null,
    string? ManageableRolesType = null,
    int? Rank = null,
    IReadOnlyList<Guid>? ManageableRolePublicIds = null,
    string? Name = null);
