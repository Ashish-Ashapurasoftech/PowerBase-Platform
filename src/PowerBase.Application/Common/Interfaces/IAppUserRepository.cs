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
    DateTime CreatedOn);

public interface IAppUserRepository
{
    Task<IReadOnlyList<AppUserDetail>> ListByAppIdAsync(long appId, CancellationToken ct = default);
    Task<AppUser?> GetByAppAndUserAsync(long appId, long userId, CancellationToken ct = default);
    Task CreateAsync(AppUser appUser, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task UpdateRoleAsync(long appId, long userId, long newRoleId, CancellationToken ct = default);
    Task RemoveAsync(long appId, long userId, CancellationToken ct = default);
    Task<string?> GetUserRoleNameAsync(long appId, long userId, CancellationToken ct = default);
}
