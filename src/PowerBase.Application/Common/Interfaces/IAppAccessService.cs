namespace PowerBase.Application.Common.Interfaces;

public interface IAppAccessService
{
    Task RequirePermissionByAppPublicIdAsync(Guid appPublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByTablePublicIdAsync(Guid tablePublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByReportPublicIdAsync(Guid reportPublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByAppIdAsync(long appId, string permissionCode, CancellationToken ct = default);
    Task RequireAppRoleAsync(Guid appPublicId, string roleName, CancellationToken ct = default);
    Task RequirePermissionByFormPublicIdAsync(Guid formPublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByFormRulePublicIdAsync(Guid rulePublicId, string permissionCode, CancellationToken ct = default);

    /// <summary>Ensures the user is a member of the app that owns the specified table. Does NOT require any specific permission code.</summary>
    Task RequireMembershipByTablePublicIdAsync(Guid tablePublicId, CancellationToken ct = default);
    /// <summary>Ensures the user is a member of the app that owns the specified report. Does NOT require any specific permission code.</summary>
    Task RequireMembershipByReportPublicIdAsync(Guid reportPublicId, CancellationToken ct = default);
}
