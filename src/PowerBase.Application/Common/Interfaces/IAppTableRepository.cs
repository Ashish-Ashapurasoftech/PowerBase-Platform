using PowerBase.Application.Common.Models;
using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IAppTableRepository
{
    Task<AppTable> GetByIdAsync(long id, CancellationToken ct = default);
    Task<AppTable> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default);
    Task<long> GetAppIdByPublicIdAsync(Guid tablePublicId, CancellationToken ct = default);
    Task<IReadOnlyList<AppTableListItemDto>> ListByAppAsync(long appId, CancellationToken ct = default);
    Task<bool> NameExistsInAppAsync(long appId, string name, CancellationToken ct = default);
    Task<(long Id, Guid PublicId)> CreateAsync(AppTable table, CancellationToken ct = default);
    Task UpdatePhysicalNameAsync(long id, string physicalTableName, CancellationToken ct = default);
    Task<int> UpdateAsync(Guid publicId, string name, string? singularLabel, string? pluralLabel, string? description, string? icon, CancellationToken ct = default);
    Task DeleteAsync(Guid publicId, CancellationToken ct = default);
    Task IncrementRecordCountAsync(long id, CancellationToken ct = default);
    Task DecrementRecordCountAsync(long id, CancellationToken ct = default);
}
