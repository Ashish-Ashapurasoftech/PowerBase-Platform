using System.Data;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IAppRepository
{
    Task<App> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<long> GetIdByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyList<App>> ListAsync(int page, int pageSize, CancellationToken ct = default);
    Task<int> CountAsync(CancellationToken ct = default);
    Task<IReadOnlyList<App>> ListByUserAsync(long userId, int page, int pageSize, CancellationToken ct = default);
    Task<int> CountByUserAsync(long userId, CancellationToken ct = default);
    Task<bool> NameExistsAsync(string name, CancellationToken ct = default);
    Task<(Guid PublicId, long Id)> CreateAsync(App app, IDbTransaction? transaction = null, CancellationToken ct = default);
    Task UpdateAsync(App app, CancellationToken ct = default);
    Task DeleteAsync(Guid publicId, CancellationToken ct = default);
}
