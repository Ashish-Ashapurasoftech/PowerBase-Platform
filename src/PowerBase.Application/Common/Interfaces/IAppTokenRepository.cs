using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IAppTokenRepository
{
    Task<AppToken> CreateAsync(AppToken appToken, CancellationToken ct);
    Task<AppToken?> GetByPublicIdAsync(Guid publicId, long tenantId, Guid appPublicId, CancellationToken ct);
    Task<(IEnumerable<AppToken> Items, int TotalCount)> GetPagedAsync(long tenantId, Guid appPublicId, string? search, bool? isActive, int page, int pageSize, string sortBy, bool sortDesc, CancellationToken ct);
    Task<bool> UpdateStatusAsync(Guid publicId, long tenantId, Guid appPublicId, bool isActive, CancellationToken ct);
    Task<bool> DeleteAsync(Guid publicId, long tenantId, Guid appPublicId, CancellationToken ct);
    Task<bool> RotateSecretAsync(long id, string newTokenHash, string newTokenPrefix, CancellationToken ct);
}
