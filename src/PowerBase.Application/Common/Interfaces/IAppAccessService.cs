namespace PowerBase.Application.Common.Interfaces;

public interface IAppAccessService
{
    Task RequirePermissionByAppPublicIdAsync(Guid appPublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByTablePublicIdAsync(Guid tablePublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByReportPublicIdAsync(Guid reportPublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByAppIdAsync(long appId, string permissionCode, CancellationToken ct = default);
    Task RequireAppRoleAsync(Guid appPublicId, string roleName, CancellationToken ct = default);
    Task RequireByFormPublicIdAsync(Guid formPublicId, AppAccess required, CancellationToken ct = default);
    Task RequireByFormRulePublicIdAsync(Guid rulePublicId, AppAccess required, CancellationToken ct = default);
    Task<AppPermissionFlags> GetPermissionFlagsByTablePublicIdAsync(Guid tablePublicId, CancellationToken ct = default);
}
