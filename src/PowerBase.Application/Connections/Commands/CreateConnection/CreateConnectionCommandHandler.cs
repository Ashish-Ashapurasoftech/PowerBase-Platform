using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Connections.Common;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Connections.Commands.CreateConnection;

/// <summary>
/// Connects an account that PowerFlows steps can then target.
///
/// The raw user token lives only as a local inside <see cref="ConnectWithUserTokenAsync"/>: it is
/// hashed straight away, and only the hash plus a masked prefix are persisted. It never reaches
/// the audit trail, the log, or a response body.
///
/// The whole credential chain is verified before anything is written, so a rejected connect
/// leaves no half-saved account row behind.
/// </summary>
public class CreateConnectionCommandHandler
{
    private const int MaxNameLength = 200;

    private readonly IPipelineAccountRepository _accountRepo;
    private readonly IUserTokenRepository _userTokenRepo;
    private readonly ITenantRepository _tenantRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IQueryContext _queryContext;
    private readonly IServiceScopeFactory _scopeFactory;

    public CreateConnectionCommandHandler(
        IPipelineAccountRepository accountRepo,
        IUserTokenRepository userTokenRepo,
        ITenantRepository tenantRepo,
        IAuditRepository auditRepo,
        IQueryContext queryContext,
        IServiceScopeFactory scopeFactory)
    {
        _accountRepo = accountRepo;
        _userTokenRepo = userTokenRepo;
        _tenantRepo = tenantRepo;
        _auditRepo = auditRepo;
        _queryContext = queryContext;
        _scopeFactory = scopeFactory;
    }

    public async Task<PipelineAccountDto> HandleAsync(CreateConnectionCommand command, CancellationToken ct = default)
    {
        var validator = new CreateConnectionCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        return await ConnectWithUserTokenAsync(command, command.Subdomain.Trim().ToLowerInvariant(), ct);
    }

    private async Task<PipelineAccountDto> ConnectWithUserTokenAsync(
        CreateConnectionCommand command, string subdomain, CancellationToken ct)
    {
        // ── The raw token's entire lifetime ──────────────────────────────────────────
        var rawToken = command.UserToken.Trim();
        var tokenHash = ConnectionScopeResolver.HashToken(rawToken);
        var tokenPrefix = ConnectionScopeResolver.BuildTokenPrefix(rawToken);
        // ── From here on only the hash and the masked prefix are used ────────────────

        var userToken = await _userTokenRepo.GetByHashAsync(tokenHash, ct);
        if (userToken == null || !userToken.IsActive || userToken.IsDeleted)
        {
            throw new UnauthorizedActionException(
                "That User Token is not valid, or it has been revoked. Check the token and try again.");
        }

        var tenant = await _tenantRepo.GetTenantBySlugAsync(subdomain, ct);
        if (tenant == null)
        {
            throw new UnauthorizedActionException(
                $"No active realm was found for the subdomain '{subdomain}'.");
        }

        // A user token is minted against exactly one realm. Honouring it against a different
        // realm would grant access the token never conferred.
        if (userToken.TenantId != tenant.Id)
        {
            throw new UnauthorizedActionException(
                $"That User Token does not belong to the '{subdomain}' realm.");
        }

        IReadOnlySet<long> allowedAppIds = userToken.AccessAllApps
            ? new HashSet<long>()
            : await _userTokenRepo.GetAllowedAppIdsAsync(userToken.Id, ct);

        // Verify the full chain up front through the same scope machinery the metadata queries
        // and the runtime engine use later, so a connect that succeeds here cannot fail with a
        // different verdict on first use. AccountId/ConnectionPublicId are not assigned yet —
        // the helper does not read them.
        var probe = new ConnectionScope
        {
            AccountId = 0,
            ConnectionPublicId = Guid.Empty,
            TargetTenantId = tenant.Id,
            TargetUserId = userToken.UserId,
            IsUserToken = true,
            TokenAccessAllApps = userToken.AccessAllApps,
            AllowedAppIds = allowedAppIds
        };

        string ownerDisplay;
        await using (var targetScope = await TargetTenantScopeHelper.OpenAsync(_scopeFactory, probe, ct))
        {
            var scopedContext = targetScope.GetRequiredService<IQueryContext>();
            ownerDisplay = string.IsNullOrWhiteSpace(scopedContext.UserEmail)
                ? scopedContext.UserName
                : scopedContext.UserEmail;
        }

        var name = BuildName(command.Name, ownerDisplay, subdomain);

        // Re-supplying the same token reconnects the caller's own existing account rather than
        // adding a duplicate entry to the dropdown.
        var existing = await _accountRepo.GetByTokenHashAsync(tokenHash, _queryContext.UserId, ct);
        if (existing != null)
        {
            existing.Name = name;
            existing.Subdomain = subdomain;
            existing.TargetTenantId = tenant.Id;
            existing.TargetUserId = userToken.UserId;
            existing.UserTokenPublicId = userToken.PublicId;
            existing.TokenPrefix = tokenPrefix;
            existing.Status = PipelineAccountStatuses.Active;
            existing.IsActive = true;

            var refreshed = await _accountRepo.RefreshCredentialAsync(existing, ct);
            await LogAsync(AuditActions.Updated, refreshed, ct);
            return ConnectionScopeResolver.ToDto(refreshed);
        }

        var account = new PipelineAccount
        {
            PublicId = Guid.NewGuid(),
            TenantId = _queryContext.TenantId,
            CreatedByUserId = _queryContext.UserId,
            Name = name,
            AuthMode = PipelineAccountAuthModes.UserToken,
            Subdomain = subdomain,
            TargetTenantId = tenant.Id,
            TargetUserId = userToken.UserId,
            UserTokenPublicId = userToken.PublicId,
            TokenHash = tokenHash,
            TokenPrefix = tokenPrefix,
            Status = PipelineAccountStatuses.Active,
            IsActive = true,
            CreatedAt = DateTime.UtcNow
        };

        var created = await _accountRepo.CreateAsync(account, ct);
        await LogAsync(AuditActions.Created, created, ct);
        return ConnectionScopeResolver.ToDto(created);
    }

    private static string BuildName(string? requested, string ownerDisplay, string subdomain)
    {
        var name = string.IsNullOrWhiteSpace(requested)
            ? $"{ownerDisplay} ({subdomain})"
            : requested.Trim();

        return name.Length > MaxNameLength ? name[..MaxNameLength] : name;
    }

    /// <summary>Audit payload carries display-safe values only — never the hash or the raw token.</summary>
    private Task LogAsync(string action, PipelineAccount account, CancellationToken ct) =>
        _auditRepo.LogActivityAsync(
            action,
            AuditEntityTypes.PipelineConnection,
            account.PublicId.ToString(),
            $"PowerFlows account '{account.Name}' connected to realm '{account.Subdomain}'",
            ct: ct);
}
