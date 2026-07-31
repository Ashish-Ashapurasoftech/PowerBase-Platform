using PowerBase.Domain.Entities;
using PowerBase.Application.UserTokens.Common;

namespace PowerBase.Application.Common.Interfaces;

public interface IUserTokenRepository
{
    Task<UserToken> CreateAsync(UserToken userToken, IEnumerable<Guid>? allowedAppPublicIds, CancellationToken ct);
    Task<UserToken?> GetByPublicIdAsync(Guid publicId, long tenantId, CancellationToken ct);
    Task<IEnumerable<Guid>> GetAllowedAppPublicIdsAsync(long userTokenId, long? targetTenantId = null, CancellationToken ct = default);
    Task<(IEnumerable<Guid> PublicIds, IEnumerable<string> Names)> GetAllowedAppDetailsAsync(long userTokenId, long? targetTenantId = null, CancellationToken ct = default);
    Task<IEnumerable<UserToken>> GetMyTokensAsync(long userId, long tenantId, CancellationToken ct);
    Task<(IEnumerable<AdminUserTokenDto> Items, int TotalCount)> GetAdminTokensPagedAsync(long tenantId, string? search, bool? isActive, int page, int pageSize, CancellationToken ct);
    Task<IEnumerable<Guid>> GetExistingPublicIdsAsync(IEnumerable<Guid> publicIds, long tenantId, CancellationToken ct);
    Task<bool> UpdateStatusAsync(IEnumerable<Guid> publicIds, long tenantId, bool isActive, CancellationToken ct);
    Task<bool> RevokeAsync(Guid publicId, long tenantId, CancellationToken ct);
    Task<bool> RotateSecretAsync(long id, string newTokenHash, string newTokenPrefix, CancellationToken ct);
    Task<UserToken?> GetByHashAsync(string hash, CancellationToken ct);
    Task UpdateLastUsedAtAsync(long id, CancellationToken ct);
}
