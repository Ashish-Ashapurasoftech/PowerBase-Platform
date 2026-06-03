namespace PowerBase.Application.Admin;

public record AdminTenantDto(
    long Id,
    Guid PublicId,
    string Name,
    string Slug,
    string Status,
    string ProvisioningState,
    string? DatabaseName,
    int SchemaVersion,
    int MemberCount,
    DateTime CreatedOn);

public record AdminUserDto(
    long Id,
    Guid PublicId,
    string Email,
    string Name,
    bool IsActive,
    bool IsEmailVerified,
    string? SystemRoleCode,
    int TenantCount,
    DateTime CreatedOn,
    DateTime? LastLoginOn);

public record AdminTenantMemberDto(
    Guid UserPublicId,
    string Email,
    string Name,
    bool IsActive,
    Guid? RolePublicId,
    string RoleName,
    bool IsOwner,
    DateTime? JoinedOn);

public record AdminTenantRoleDto(
    Guid PublicId,
    string Name,
    bool IsDefault,
    bool IsSystem);

public record AdminRolePermissionDto(string Code, string DisplayName, string? Description);

public record AdminPermissionDto(string Code, string DisplayName, string? Description);

public record AdminLoginAttemptDto(
    long Id,
    string EmailAttempted,
    string IpAddress,
    bool WasSuccessful,
    string? FailureReason,
    DateTime AttemptedOn);
