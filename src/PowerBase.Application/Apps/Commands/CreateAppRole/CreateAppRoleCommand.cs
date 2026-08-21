namespace PowerBase.Application.Apps.Commands.CreateAppRole;

public record CreateAppRoleCommand(
    Guid AppPublicId, 
    string Name, 
    bool IsDefault,
    string? ManageableRolesType = null,
    int? Rank = null,
    IReadOnlyList<Guid>? ManageableRolePublicIds = null);
