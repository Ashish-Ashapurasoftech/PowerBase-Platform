namespace PowerBase.Application.Common.Interfaces;

public interface IAppAccessService
{
    Task RequirePermissionByAppPublicIdAsync(Guid appPublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByTablePublicIdAsync(Guid tablePublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByReportPublicIdAsync(Guid reportPublicId, string permissionCode, CancellationToken ct = default);
    Task RequirePermissionByAppIdAsync(long appId, string permissionCode, CancellationToken ct = default);
    Task RequireAppRoleAsync(Guid appPublicId, string roleName, CancellationToken ct = default);
}
