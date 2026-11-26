using PowerBase.Domain.Entities;

namespace PowerBase.Application.Common.Interfaces;

/// <summary>Microsoft (Outlook) accounts connected by a user (meta.MicrosoftConnection, tenant DB).</summary>
public interface IMicrosoftConnectionRepository
{
    Task<IReadOnlyList<MicrosoftConnection>> ListForUserAsync(long userId, string provider, CancellationToken ct = default);
    Task<MicrosoftConnection?> GetByPublicIdForUserAsync(Guid publicId, long userId, CancellationToken ct = default);
    Task<MicrosoftConnection?> GetByMicrosoftUserAsync(long userId, string provider, string microsoftUserId, CancellationToken ct = default);
    Task<MicrosoftConnection> CreateAsync(MicrosoftConnection connection, CancellationToken ct = default);
    /// <summary>Updates identity, scopes, tokens and status. Throws ConcurrencyException on RowVersion mismatch.</summary>
    Task<MicrosoftConnection> UpdateAsync(MicrosoftConnection connection, CancellationToken ct = default);
    Task<bool> SoftDeleteAsync(MicrosoftConnection connection, CancellationToken ct = default);
}
