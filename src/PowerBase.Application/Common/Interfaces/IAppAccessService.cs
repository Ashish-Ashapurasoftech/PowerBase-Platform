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
    Task RequirePermissionByPagePublicIdAsync(Guid pagePublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByPipelinePublicIdAsync(Guid pipelinePublicId, string permissionCode, CancellationToken ct = default);

    /// <summary>Ensures the user is a member of the app that owns the specified table. Does NOT require any specific permission code.</summary>
    Task RequireMembershipByTablePublicIdAsync(Guid tablePublicId, CancellationToken ct = default);
    /// <summary>Ensures the user is a member of the app that owns the specified report. Does NOT require any specific permission code.</summary>
    Task RequireMembershipByReportPublicIdAsync(Guid reportPublicId, CancellationToken ct = default);
    /// <summary>Ensures the user is a member of the app that owns the specified page. Does NOT require any specific permission code.
    /// Used for the render endpoint, which is data-access governed by the page's own role visibility, not a flat permission code.</summary>
    Task RequireMembershipByPagePublicIdAsync(Guid pagePublicId, CancellationToken ct = default);
}
