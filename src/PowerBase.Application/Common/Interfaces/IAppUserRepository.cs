using System.Data;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public record AppUserDetail(
    Guid PublicId,
    Guid UserPublicId,
    string UserName,
    string UserEmail,
    Guid RolePublicId,
    string RoleName,
    string Status,
    bool ShowInUserPickers,
    DateTime CreatedOn,
    bool IsOwner);

public interface IAppUserRepository
{
    Task<IReadOnlyList<AppUserDetail>> ListByAppIdAsync(long appId, CancellationToken ct = default);
    Task<AppUser?> GetByAppAndUserAsync(long appId, long userId, CancellationToken ct = default);
    Task CreateAsync(AppUser appUser, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task UpdateRoleAsync(long appId, long userId, long newRoleId, CancellationToken ct = default);
    Task UpdateShowInUserPickersAsync(long appId, long userId, bool showInUserPickers, CancellationToken ct = default);
    Task RemoveAsync(long appId, long userId, CancellationToken ct = default);
    Task<string?> GetUserRoleNameAsync(long appId, long userId, CancellationToken ct = default);
    Task<Guid?> GetUserRolePublicIdAsync(long appId, long userId, CancellationToken ct = default);
    Task<IReadOnlySet<string>> GetUserAppPermissionsAsync(long appId, long userId, CancellationToken ct = default);
    Task<IReadOnlyList<long>> GetUserAppRoleIdsAsync(long appId, long userId, CancellationToken ct = default);
    Task<PowerBase.Application.Groups.Queries.GetUserEffectivePermissions.UserEffectivePermissionsDto> GetUserEffectivePermissionsAsync(Guid userPublicId, CancellationToken ct = default);
}
