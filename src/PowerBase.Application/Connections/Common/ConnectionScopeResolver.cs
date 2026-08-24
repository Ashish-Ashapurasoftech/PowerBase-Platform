using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Connections.Common;

/// <summary>
/// Turns a step's <c>connectionPublicId</c> into a verified <see cref="ConnectionScope"/>.
///
/// Used by the connection metadata queries, the step validator, the editor query and the
/// runtime engine so all four agree on one authorization rule. Returns null when the id is
/// not a saved account (so callers keep their existing tenant / system-connection paths);
/// throws when it IS a saved account that cannot be honoured.
/// </summary>
public class ConnectionScopeResolver
{
    private readonly IPipelineAccountRepository _accountRepo;
    private readonly IUserTokenRepository _userTokenRepo;
    private readonly IQueryContext _queryContext;

    public ConnectionScopeResolver(
        IPipelineAccountRepository accountRepo,
        IUserTokenRepository userTokenRepo,
        IQueryContext queryContext)
    {
        _accountRepo = accountRepo;
        _userTokenRepo = userTokenRepo;
        _queryContext = queryContext;
    }

    /// <summary>SHA-256 hex (lowercase) — identical to the hashing JwtMiddleware applies to pb_ut_* tokens.</summary>
    public static string HashToken(string rawToken)
    {
        using var sha256 = SHA256.Create();
        return Convert.ToHexString(sha256.ComputeHash(Encoding.UTF8.GetBytes(rawToken))).ToLowerInvariant();
    }

    /// <summary>Masked prefix kept for display only. Never enough to reconstruct the token.</summary>
    public static string BuildTokenPrefix(string rawToken)
    {
        const int visible = 10;
        if (string.IsNullOrEmpty(rawToken)) return string.Empty;
        return rawToken.Length <= visible
            ? new string('•', rawToken.Length)
            : rawToken[..visible] + "…";
    }

    /// <summary>
    /// Loads a saved account owned by the acting user. Returns null when
    /// <paramref name="connectionPublicId"/> is not a saved account at all.
    /// </summary>
    public Task<PipelineAccount?> TryGetAccountAsync(Guid connectionPublicId, long actingUserId, CancellationToken ct = default)
        => _accountRepo.GetByPublicIdForUserAsync(connectionPublicId, actingUserId, ct);

    /// <summary>
    /// Resolves the scope for the current request's user. Returns null when the id is not a
    /// saved account. Throws <see cref="UnauthorizedActionException"/> when the account exists
    /// but its credential is no longer usable.
    /// </summary>
    public Task<ConnectionScope?> TryResolveAsync(Guid connectionPublicId, CancellationToken ct = default)
        => TryResolveForUserAsync(connectionPublicId, _queryContext.UserId, ct);

    /// <summary>
    /// Same as <see cref="TryResolveAsync"/> but for an explicit acting user — used by the
    /// runtime engine, where execution authority is the pipeline's creator rather than a
    /// live HTTP caller.
    /// </summary>
    public async Task<ConnectionScope?> TryResolveForUserAsync(Guid connectionPublicId, long actingUserId, CancellationToken ct = default)
    {
        // ── Gate 1: the acting user must own the account row ─────────────────────────
        var account = await _accountRepo.GetByPublicIdForUserAsync(connectionPublicId, actingUserId, ct);
        if (account == null) return null;

        return await ResolveAsync(account, ct);
    }

    /// <summary>
    /// Verifies an already-loaded account and produces its scope.
    /// </summary>
    public async Task<ConnectionScope> ResolveAsync(PipelineAccount account, CancellationToken ct = default)
    {
        if (!account.IsActive || account.Status != PipelineAccountStatuses.Active)
        {
            throw new UnauthorizedActionException(
                $"The connected account '{account.Name}' is {account.Status}. Reconnect the account before using it.");
        }

        if (account.AuthMode != PipelineAccountAuthModes.UserToken)
        {
            // 'current_user' accounts are never persisted — the UI resolves those to an
            // existing permitted tenant instead. A stored row in that mode is a data defect.
            throw new UnauthorizedActionException(
                $"The connected account '{account.Name}' has an unsupported authentication mode.");
        }

        if (string.IsNullOrEmpty(account.TokenHash))
        {
            throw new UnauthorizedActionException(
                $"The connected account '{account.Name}' has no stored credential. Reconnect the account.");
        }

        // ── Gate 2a: the token must still exist and be live ──────────────────────────
        var userToken = await _userTokenRepo.GetByHashAsync(account.TokenHash, ct);
        if (userToken == null || !userToken.IsActive || userToken.IsDeleted)
        {
            await _accountRepo.UpdateStatusAsync(account.Id, PipelineAccountStatuses.Revoked, false, ct);
            throw new UnauthorizedActionException(
                $"The user token behind connected account '{account.Name}' has been revoked. Reconnect the account.");
        }

        // ── Gate 2b: the token must still point at the same identity and realm ───────
        if (userToken.UserId != account.TargetUserId || userToken.TenantId != account.TargetTenantId)
        {
            await _accountRepo.UpdateStatusAsync(account.Id, PipelineAccountStatuses.Unavailable, false, ct);
            throw new UnauthorizedActionException(
                $"The user token behind connected account '{account.Name}' no longer matches the realm it was connected to. Reconnect the account.");
        }

        // ── Token app restrictions travel with the scope ─────────────────────────────
        IReadOnlySet<long> allowedAppIds = userToken.AccessAllApps
            ? new HashSet<long>()
            : await _userTokenRepo.GetAllowedAppIdsAsync(userToken.Id, ct);

        await _accountRepo.UpdateLastUsedAtAsync(account.Id, ct);
        await _userTokenRepo.UpdateLastUsedAtAsync(userToken.Id, ct);

        return new ConnectionScope
        {
            AccountId = account.Id,
            ConnectionPublicId = account.PublicId,
            TargetTenantId = account.TargetTenantId,
            TargetUserId = account.TargetUserId,
            IsUserToken = true,
            TokenAccessAllApps = userToken.AccessAllApps,
            AllowedAppIds = allowedAppIds
        };
    }

    /// <summary>
    /// Maps to the display-safe DTO. Never copies TokenHash or the internal Id.
    /// </summary>
    public static PipelineAccountDto ToDto(PipelineAccount account) => new()
    {
        PublicId = account.PublicId,
        Name = account.Name,
        AuthMode = account.AuthMode,
        Subdomain = account.Subdomain,
        TokenPrefix = account.TokenPrefix,
        Status = account.IsActive ? account.Status : PipelineAccountStatuses.Unavailable,
        CreatedAt = account.CreatedAt,
        LastUsedAt = account.LastUsedAt
    };
}
