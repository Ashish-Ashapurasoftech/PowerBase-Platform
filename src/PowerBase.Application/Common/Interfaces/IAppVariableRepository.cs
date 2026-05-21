using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IAppVariableRepository
{
    Task<IReadOnlyList<AppVariable>> ListAsync(long appId, CancellationToken ct = default);
    Task<AppVariable?> GetByPublicIdAsync(long appId, Guid publicId, CancellationToken ct = default);
    Task<int> CountAsync(long appId, CancellationToken ct = default);
    Task<bool> NameExistsAsync(long appId, string name, CancellationToken ct = default);
    Task<Guid> CreateAsync(AppVariable variable, CancellationToken ct = default);
    Task UpdateAsync(long appId, Guid publicId, string name, string value, string? description, CancellationToken ct = default);
    Task DeleteAsync(long appId, Guid publicId, CancellationToken ct = default);
}
