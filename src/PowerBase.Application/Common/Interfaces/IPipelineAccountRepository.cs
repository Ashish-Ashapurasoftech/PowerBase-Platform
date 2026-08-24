using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

/// <summary>
/// Saved PowerFlows connection accounts (meta.PipelineAccount, tenant DB).
/// All reads are scoped to the current tenant and to the owning user — a saved account
/// is only ever visible to, and usable by, the user who created it.
/// </summary>
public interface IPipelineAccountRepository
{
    Task<IReadOnlyList<PipelineAccount>> ListForUserAsync(long userId, CancellationToken ct = default);

    /// <summary>Loads an account owned by <paramref name="userId"/>. Returns null when it does not exist.</summary>
    Task<PipelineAccount?> GetByPublicIdForUserAsync(Guid publicId, long userId, CancellationToken ct = default);

    /// <summary>Finds this user's existing account for the same token, so re-adding it reuses the row.</summary>
    Task<PipelineAccount?> GetByTokenHashAsync(string tokenHash, long userId, CancellationToken ct = default);

    Task<PipelineAccount> CreateAsync(PipelineAccount account, CancellationToken ct = default);

    /// <summary>Re-points an existing row at a freshly supplied token and reactivates it.</summary>
    Task<PipelineAccount> RefreshCredentialAsync(PipelineAccount account, CancellationToken ct = default);

    Task<int> UpdateStatusAsync(long id, string status, bool isActive, CancellationToken ct = default);

    Task UpdateLastUsedAtAsync(long id, CancellationToken ct = default);
}
