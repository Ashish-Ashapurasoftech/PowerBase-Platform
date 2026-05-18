using System.Data;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IAppRoleRepository
{
    Task<IReadOnlyList<AppRole>> ListByAppIdAsync(long appId, CancellationToken ct = default);
    Task<AppRole?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> CreateAsync(AppRole role, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task<bool> NameExistsInAppAsync(long appId, string name, CancellationToken ct = default);
    Task DeleteAsync(Guid publicId, CancellationToken ct = default);
}
