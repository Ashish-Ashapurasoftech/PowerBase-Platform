using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IAppTableRepository
{
    Task<AppTable> GetByIdAsync(long id, CancellationToken ct = default);
    Task<AppTable> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<IReadOnlyList<AppTable>> ListByAppAsync(long appId, CancellationToken ct = default);
    Task<bool> NameExistsInAppAsync(long appId, string name, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> CreateAsync(AppTable table, CancellationToken ct = default);
    Task UpdatePhysicalNameAsync(long id, string physicalTableName, CancellationToken ct = default);
    Task DeleteAsync(Guid publicId, CancellationToken ct = default);
}
