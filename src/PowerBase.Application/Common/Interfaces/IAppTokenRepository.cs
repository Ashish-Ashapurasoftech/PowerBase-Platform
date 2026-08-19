using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

public interface IAppTokenRepository
{
    Task<AppToken> CreateAsync(AppToken appToken, CancellationToken ct);
    Task<AppToken?> GetByPublicIdAsync(Guid publicId, long tenantId, Guid appPublicId, CancellationToken ct);
    Task<(IEnumerable<AppToken> Items, int TotalCount)> GetPagedAsync(long tenantId, Guid appPublicId, string? search, bool? isActive, int page, int pageSize, CancellationToken ct);
    Task<bool> UpdateStatusAsync(Guid publicId, long tenantId, Guid appPublicId, bool isActive, CancellationToken ct);
    Task<bool> DeleteAsync(Guid publicId, long tenantId, Guid appPublicId, CancellationToken ct);
    /// <summary>Soft-deletes every matching token in one statement (PublicId IN @publicIds) rather
    /// than one round-trip per id. Returns how many rows were actually affected — callers should not
    /// assume it equals publicIds.Count (some ids may already be deleted / belong to another tenant-app).</summary>
    Task<int> BulkDeleteAsync(IEnumerable<Guid> publicIds, long tenantId, Guid appPublicId, CancellationToken ct);
    Task<bool> RotateSecretAsync(long id, string newTokenHash, string newTokenPrefix, CancellationToken ct);
}
