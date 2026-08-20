namespace PowerBase.API.Models.Apps;

public record UpdateAppRoleRequest(
    IReadOnlyList<string>? Permissions = null,
    string? ManageableRolesType = null,
    int? Rank = null,
    IReadOnlyList<Guid>? ManageableRolePublicIds = null);
