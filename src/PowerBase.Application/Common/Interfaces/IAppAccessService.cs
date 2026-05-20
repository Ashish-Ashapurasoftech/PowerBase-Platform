namespace PowerBase.Application.Common.Interfaces;

public enum AppAccess { Read, Admin }

public record AppPermissionFlags(bool CanView, bool CanAdd, bool CanEdit, bool CanDelete);

public interface IAppAccessService
{
    Task RequireByAppPublicIdAsync(Guid appPublicId, AppAccess required, CancellationToken ct = default);
    Task RequireByTablePublicIdAsync(Guid tablePublicId, AppAccess required, CancellationToken ct = default);
    Task RequireByReportPublicIdAsync(Guid reportPublicId, AppAccess required, CancellationToken ct = default);
    Task RequireByAppIdAsync(long appId, AppAccess required, CancellationToken ct = default);
    Task<AppPermissionFlags> GetPermissionFlagsByTablePublicIdAsync(Guid tablePublicId, CancellationToken ct = default);
}
