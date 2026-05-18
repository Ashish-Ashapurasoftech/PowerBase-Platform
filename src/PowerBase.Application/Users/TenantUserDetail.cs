namespace PowerBase.Application.Users;

public record TenantUserDetail(
    Guid UserPublicId,
    string Email,
    string Name,
    Guid? RolePublicId,
    string? RoleName,
    bool IsOwner,
    bool IsActive,
    DateTime JoinedOn);
