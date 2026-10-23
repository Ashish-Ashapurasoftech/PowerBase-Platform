using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.ConnectedTables.Handlers;

public sealed record SetSharingPolicyCommand(string OutboundPolicy, bool InboundEnabled);

public sealed record RequestDataShareCommand(string Realm, string? Note);

public sealed record ApproveDataShareCommand(
    Guid PublicId, string Scope, IReadOnlyList<Guid>? AppPublicIds, bool AllowEncryptedFields, bool AllowCrossRegion, DateTime? ExpiresOn, string RowVersion);

/// <summary>Consent between realms (plan §5.2): a consumer realm asks, the provider realm's administrators approve (all apps or
/// chosen apps), reject, or later revoke; and they can see — and stop — every connected table that reads their data.
/// Revoking stops future refreshes; data already copied stays with the consumer.</summary>
public sealed class DataSharingHandlers(
    IConnectedControlRepository control,
    ConnectedPermissions permissions,
    IQueryContext context,
    IUserRepository users,
    IEmailService email,
    IServiceScopeFactory scopeFactory,
    IAuditRepository audit,
    ILogger<DataSharingHandlers> logger)
{
    public async Task<SharingPolicyResult> GetPolicyAsync(CancellationToken ct)
    {
        permissions.RequireTenantAdmin();
        var policy = await control.GetPolicyAsync(context.TenantId, ct);
        return new SharingPolicyResult(policy.OutboundPolicy, policy.InboundEnabled);
    }

    public async Task<SharingPolicyResult> SetPolicyAsync(SetSharingPolicyCommand command, CancellationToken ct)
    {
        permissions.RequireTenantAdmin();
        if (command.OutboundPolicy is not (ConnectedOutboundPolicies.Disabled or ConnectedOutboundPolicies.ApprovedOnly))
            throw new ValidationException(new Dictionary<string, string[]> { ["OutboundPolicy"] = ["Choose whether other realms may connect to your data."] });
        await control.SavePolicyAsync(new ConnectedSharingPolicy
        {
            TenantId = context.TenantId, OutboundPolicy = command.OutboundPolicy, InboundEnabled = command.InboundEnabled, ModifiedBy = context.UserId
        }, ct);
        await audit.LogActivityAsync("DataSharingPolicyChanged", "Tenant", context.TenantId.ToString(),
            $"Outbound: {command.OutboundPolicy}; inbound: {(command.InboundEnabled ? "on" : "off")}", ct: ct);
        return await GetPolicyAsync(ct);
    }

    /// <summary>A consumer asks a provider realm for access. Any member who can build tables may ask; the provider decides.</summary>
    public async Task<DataShareResult> RequestAsync(RequestDataShareCommand command, CancellationToken ct)
    {
        var provider = await control.GetTenantBySlugAsync(ConnectedTokens.RealmSlug(command.Realm), ct);
        if (provider is null || !provider.IsUsable)
            throw new BadRequestException(ConnectedErrorCodes.SourceUnavailable, "No active PowerBase realm was found at that URL.");
        if (provider.Id == context.TenantId)
            throw new BadRequestException(ConnectedErrorCodes.Forbidden, "Tables in your own realm don't need a sharing approval.");

        if (await control.GetOpenShareAsync(provider.Id, context.TenantId, ct) is { } open)
            return await ToResultAsync(open, ct);

        var share = new ConnectedDataShare
        {
            ProviderTenantId = provider.Id,
            ConsumerTenantId = context.TenantId,
            Scope = ConnectedDataShareScopes.AllApps,
            Status = ConnectedDataShareStatuses.Requested,
            RequestedByUserId = context.UserId,
            RequestNote = string.IsNullOrWhiteSpace(command.Note) ? null : command.Note.Trim()[..Math.Min(500, command.Note.Trim().Length)],
        };
        (share.Id, share.PublicId) = await control.CreateShareAsync(share, ct);
        await audit.LogActivityAsync("DataShareRequested", "DataShare", share.PublicId.ToString(), $"Access requested to realm {provider.Name}", ct: ct);
        await NotifyProviderAdminsAsync(provider.Id, share, ct);
        return await ToResultAsync((await control.GetShareAsync(share.PublicId, ct))!, ct);
    }

    public async Task<IReadOnlyList<DataShareResult>> ListIncomingAsync(CancellationToken ct)
    {
        permissions.RequireTenantAdmin();
        var shares = await control.ListSharesForProviderAsync(context.TenantId, ct);
        return await ToResultsAsync(shares, ct);
    }

    public async Task<IReadOnlyList<DataShareResult>> ListOutgoingAsync(CancellationToken ct)
    {
        permissions.RequireTenantAdmin();
        var shares = await control.ListSharesForConsumerAsync(context.TenantId, ct);
        return await ToResultsAsync(shares, ct);
    }

    public async Task<DataShareResult> ApproveAsync(ApproveDataShareCommand command, CancellationToken ct)
    {
        permissions.RequireTenantAdmin();
        var share = await GetIncomingAsync(command.PublicId, ct);
        if (share.Status is not (ConnectedDataShareStatuses.Requested or ConnectedDataShareStatuses.Approved))
            throw ConnectedTableException.Conflict(ConnectedErrorCodes.Forbidden, "Only a pending or approved request can be approved.");
        if (command.Scope is not (ConnectedDataShareScopes.AllApps or ConnectedDataShareScopes.Apps))
            throw new ValidationException(new Dictionary<string, string[]> { ["Scope"] = ["Choose all apps or specific apps."] });
        if (command.Scope == ConnectedDataShareScopes.Apps && (command.AppPublicIds is null || command.AppPublicIds.Count == 0))
            throw new ValidationException(new Dictionary<string, string[]> { ["AppPublicIds"] = ["Choose at least one app."] });
        if (command.ExpiresOn is { } expires && expires <= DateTime.UtcNow)
            throw new ValidationException(new Dictionary<string, string[]> { ["ExpiresOn"] = ["The expiry date must be in the future."] });

        share.RowVersion = ConnectedTokens.RowVersion(command.RowVersion);
        share.Scope = command.Scope;
        share.AppPublicIdsJson = command.Scope == ConnectedDataShareScopes.Apps ? JsonSerializer.Serialize(command.AppPublicIds!.Distinct()) : null;
        share.AllowEncryptedFields = command.AllowEncryptedFields;
        share.AllowCrossRegion = command.AllowCrossRegion;
        share.ExpiresOn = command.ExpiresOn;
        share.Status = ConnectedDataShareStatuses.Approved;
        share.DecidedByUserId = context.UserId;
        share.DecidedOn = DateTime.UtcNow;
        await control.UpdateShareAsync(share, ct);
        // Approving again re-enables the consumer's tables that a revoke had stopped.
        await control.SetLinkStatusForPairAsync(share.ProviderTenantId, share.ConsumerTenantId, ConnectedLinkStatuses.RevokedByProvider, ConnectedLinkStatuses.Active, ct);
        await audit.LogActivityAsync("DataShareApproved", "DataShare", share.PublicId.ToString(), await ConsumerNameAsync(share, ct), ct: ct);
        return await ToResultAsync((await control.GetShareAsync(share.PublicId, ct))!, ct);
    }

    public async Task<DataShareResult> RejectAsync(Guid publicId, string rowVersion, CancellationToken ct)
    {
        permissions.RequireTenantAdmin();
        var share = await GetIncomingAsync(publicId, ct);
        if (share.Status != ConnectedDataShareStatuses.Requested)
            throw ConnectedTableException.Conflict(ConnectedErrorCodes.Forbidden, "Only a pending request can be rejected.");
        share.RowVersion = ConnectedTokens.RowVersion(rowVersion);
        share.Status = ConnectedDataShareStatuses.Rejected;
        share.DecidedByUserId = context.UserId;
        share.DecidedOn = DateTime.UtcNow;
        await control.UpdateShareAsync(share, ct);
        await audit.LogActivityAsync("DataShareRejected", "DataShare", share.PublicId.ToString(), await ConsumerNameAsync(share, ct), ct: ct);
        return await ToResultAsync((await control.GetShareAsync(share.PublicId, ct))!, ct);
    }

    /// <summary>Stops every connected table of the consumer realm that reads this realm. Data already copied stays with them.</summary>
    public async Task<DataShareResult> RevokeAsync(Guid publicId, string rowVersion, CancellationToken ct)
    {
        permissions.RequireTenantAdmin();
        var share = await GetIncomingAsync(publicId, ct);
        if (share.Status != ConnectedDataShareStatuses.Approved)
            throw ConnectedTableException.Conflict(ConnectedErrorCodes.Forbidden, "Only an approved share can be revoked.");
        share.RowVersion = ConnectedTokens.RowVersion(rowVersion);
        share.Status = ConnectedDataShareStatuses.Revoked;
        share.DecidedByUserId = context.UserId;
        share.DecidedOn = DateTime.UtcNow;
        await control.UpdateShareAsync(share, ct);
        await control.SetLinkStatusForPairAsync(share.ProviderTenantId, share.ConsumerTenantId, ConnectedLinkStatuses.Active, ConnectedLinkStatuses.RevokedByProvider, ct);
        await audit.LogActivityAsync("DataShareRevoked", "DataShare", share.PublicId.ToString(), await ConsumerNameAsync(share, ct), ct: ct);
        return await ToResultAsync((await control.GetShareAsync(share.PublicId, ct))!, ct);
    }

    /// <summary>Every connected table (in any realm, this one included) that reads this realm's data.</summary>
    public async Task<IReadOnlyList<ConnectedLinkResult>> ListLinksAsync(CancellationToken ct)
    {
        permissions.RequireTenantAdmin();
        var links = await control.ListLinksForProviderAsync(context.TenantId, ct);
        var realms = await control.GetTenantNamesAsync(links.Select(l => l.ConsumerTenantId).ToList(), ct);
        var userNames = await users.GetNamesByIdsAsync(links.Select(l => l.RunAsUserId).Distinct(), ct);
        var appNames = await AppNamesAsync(links.Select(l => l.ProviderAppPublicId).Distinct().ToList(), ct);
        return links.Select(l => new ConnectedLinkResult(l.PublicId, realms.GetValueOrDefault(l.ConsumerTenantId) ?? "Unknown realm",
            l.ConsumerTableName, appNames.GetValueOrDefault(l.ProviderAppPublicId) ?? "Unknown app", l.ProviderTableName,
            userNames.GetValueOrDefault(l.RunAsUserId), l.Status, ConnectedTime.Utc(l.LastRunOn), l.LastRowCount, ConnectedTime.Utc(l.CreatedOn))).ToList();
    }

    public async Task RevokeLinkAsync(Guid linkPublicId, CancellationToken ct)
    {
        permissions.RequireTenantAdmin();
        var link = await control.GetLinkByPublicIdAsync(linkPublicId, ct);
        if (link is null || link.ProviderTenantId != context.TenantId) throw new NotFoundException("ConnectedLink", linkPublicId);
        if (link.Status != ConnectedLinkStatuses.Active) return;
        await control.SetLinkStatusAsync(link.Id, ConnectedLinkStatuses.RevokedByProvider, ct);
        await audit.LogActivityAsync("ConnectedLinkRevoked", "ConnectedLink", link.PublicId.ToString(), $"{link.ProviderTableName} → {link.ConsumerTableName}", ct: ct);
    }

    // ── helpers ──────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<ConnectedDataShare> GetIncomingAsync(Guid publicId, CancellationToken ct)
    {
        var share = await control.GetShareAsync(publicId, ct);
        if (share is null || share.ProviderTenantId != context.TenantId) throw new NotFoundException("DataShare", publicId);
        return share;
    }

    private async Task<string> ConsumerNameAsync(ConnectedDataShare share, CancellationToken ct) =>
        (await control.GetTenantNamesAsync([share.ConsumerTenantId], ct)).GetValueOrDefault(share.ConsumerTenantId) ?? "another realm";

    private async Task<IReadOnlyList<DataShareResult>> ToResultsAsync(IReadOnlyList<ConnectedDataShare> shares, CancellationToken ct)
    {
        var names = await control.GetTenantNamesAsync(shares.SelectMany(s => new[] { s.ProviderTenantId, s.ConsumerTenantId }).Distinct().ToList(), ct);
        return shares.Select(s => ToResult(s, names)).ToList();
    }

    private async Task<DataShareResult> ToResultAsync(ConnectedDataShare share, CancellationToken ct) =>
        ToResult(share, await control.GetTenantNamesAsync([share.ProviderTenantId, share.ConsumerTenantId], ct));

    private static DataShareResult ToResult(ConnectedDataShare s, IReadOnlyDictionary<long, string> names)
    {
        IReadOnlyList<Guid> apps = [];
        if (!string.IsNullOrWhiteSpace(s.AppPublicIdsJson))
            try { apps = JsonSerializer.Deserialize<List<Guid>>(s.AppPublicIdsJson) ?? []; } catch (JsonException) { }
        return new DataShareResult(s.PublicId, names.GetValueOrDefault(s.ProviderTenantId) ?? "Unknown realm",
            names.GetValueOrDefault(s.ConsumerTenantId) ?? "Unknown realm", s.Scope, apps, s.AllowEncryptedFields, s.AllowCrossRegion,
            s.Status, ConnectedTime.Utc(s.RequestedOn), s.RequestNote, ConnectedTime.Utc(s.DecidedOn), ConnectedTime.Utc(s.ExpiresOn), ConnectedTokens.RowVersion(s.RowVersion));
    }

    /// <summary>Names of this realm's apps (the provider side lists links to its own apps).</summary>
    private async Task<IReadOnlyDictionary<Guid, string>> AppNamesAsync(IReadOnlyList<Guid> appPublicIds, CancellationToken ct)
    {
        var apps = new Dictionary<Guid, string>();
        if (appPublicIds.Count == 0) return apps;
        var repo = scopeFactory.CreateScope();
        try
        {
            repo.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(context.TenantId);
            var appRepo = repo.ServiceProvider.GetRequiredService<IAppRepository>();
            foreach (var id in appPublicIds)
            {
                try { apps[id] = (await appRepo.GetByPublicIdAsync(id, ct)).Name; }
                catch (NotFoundException) { }
            }
        }
        finally { repo.Dispose(); }
        return apps;
    }

    /// <summary>Emails the provider realm's administrators about a new request. A failed email never fails the request.</summary>
    private async Task NotifyProviderAdminsAsync(long providerTenantId, ConnectedDataShare share, CancellationToken ct)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            scope.ServiceProvider.GetRequiredService<IQueryContext>().SetTenantId(providerTenantId);
            var admins = await scope.ServiceProvider.GetRequiredService<ITenantRepository>()
                .ListUsersAsync(roleName: DefaultTenantRoles.Administrator, status: null, ct: ct);
            var consumer = (await control.GetTenantNamesAsync([share.ConsumerTenantId], ct)).GetValueOrDefault(share.ConsumerTenantId) ?? "another realm";
            var requester = (await users.GetIdentitiesByIdsAsync([share.RequestedByUserId], ct)).GetValueOrDefault(share.RequestedByUserId);
            var body = $"""
                {requester?.Name ?? "Someone"} ({requester?.Email ?? "unknown"}) in the realm "{consumer}" asks to connect tables to data in your realm.
                {(share.RequestNote is null ? "" : $"\nTheir note: {share.RequestNote}\n")}
                Nothing is shared until a realm administrator approves the request under Data sharing. If you approve, users in "{consumer}" will see the
                connected data according to their own permissions; you can stop future refreshes at any time, but data already copied stays with them.
                """;
            foreach (var admin in admins.Where(a => a.IsActive && !string.IsNullOrWhiteSpace(a.Email)))
                await email.SendEmailAsync(admin.Email, $"\"{consumer}\" asks to connect to your PowerBase data", body, isHtml: false, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "Could not email the administrators of realm {TenantId} about data share {ShareId}.", providerTenantId, share.PublicId);
        }
    }
}
