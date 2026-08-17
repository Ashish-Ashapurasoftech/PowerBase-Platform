using System.Data;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public record AppRoleDetail(
    long Id, Guid PublicId, long AppId, string Name, bool IsDefault, bool IsSystem,
    IReadOnlyList<string> Permissions, string ManageableRolesType, int? Rank,
    IReadOnlyList<Guid> ManageableRolePublicIds);

public interface IAppRoleRepository
{
    Task<IReadOnlyList<AppRoleDetail>> ListDetailsByAppIdAsync(long appId, CancellationToken ct = default);
    Task<AppRole?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> CreateAsync(AppRole role, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<bool> NameExistsInAppAsync(long appId, string name, CancellationToken ct = default);
    Task SetPermissionsAsync(long appRoleId, IReadOnlyList<string> permissionCodes, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task DeleteAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyList<Guid>> GetManageableRolePublicIdsAsync(long roleId, CancellationToken ct = default);
    Task UpdateRoleHierarchyAsync(Guid publicId, string manageableRolesType, int? rank, IReadOnlyList<Guid> manageableRolePublicIds, CancellationToken ct = default);
}
