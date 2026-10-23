using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.ValueObjects;

namespace PowerBase.Application.ConnectedTables.Access;

/// <summary>A verified, open way into the source app: every gate passed and a read-only scope is pinned to the source.</summary>
public sealed class ConnectedSourceAccess : IAsyncDisposable
{
    internal ConnectedSourceAccess(ConnectedSourceScope scope, App app, bool isCrossTenant, ConnectedDataShare? share, bool isAppAdmin)
    {
        Scope = scope;
        App = app;
        IsCrossTenant = isCrossTenant;
        Share = share;
        IsSourceAppAdmin = isAppAdmin;
    }

    public ConnectedSourceScope Scope { get; }
    public App App { get; }
    public bool IsCrossTenant { get; }
    /// <summary>The approved share the read relies on (cross-tenant only).</summary>
    public ConnectedDataShare? Share { get; }
    public bool IsSourceAppAdmin { get; }
    /// <summary>Encrypted source fields may be copied: always within a realm, across realms only when the share allows it.</summary>
    public bool AllowsEncryptedFields => !IsCrossTenant || Share?.AllowEncryptedFields == true;

    public ValueTask DisposeAsync() => Scope.DisposeAsync();
}

/// <summary>The source table as the reading user may see it.</summary>
public sealed record ConnectedSourceTable(AppTable Table, IReadOnlyList<AppField> AllFields, IReadOnlyList<AppField> VisibleFields, TableAccessContext Access)
{
    public AppField? FindVisible(int fid) => VisibleFields.FirstOrDefault(f => f.Fid == fid);
}

/// <summary>The credential a new or existing connection resolves to.</summary>
public sealed record ConnectedCredential(long SourceTenantId, long RunAsUserId, bool IsUserToken, bool TokenAccessAllApps, IReadOnlySet<long> AllowedAppIds);

/// <summary>Every access gate of the module (see docs/CONNECTED_TABLES_IMPLEMENTATION_PLAN.md §5), checked when a connection
/// is created, whenever source data is listed, and at the start of every refresh. A failing gate stops the work before any
/// source row is read. Errors raised before consent and app access are confirmed never name the source app or table.</summary>
public sealed class ConnectedAccessVerifier(
    IConnectedControlRepository control,
    IConnectedConnectionRepository connections,
    IUserTokenRepository userTokens,
    ConnectedSourceScopeFactory scopes)
{
    private const string AdminRoleName = "Administrator";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    /// <summary>G3 for a raw user token supplied while connecting: returns the realm and user it belongs to.</summary>
    public async Task<ConnectedCredential> ResolveUserTokenAsync(string tokenHash, long realmTenantId, CancellationToken ct)
    {
        var token = await userTokens.GetByHashAsync(tokenHash, ct);
        if (token is null || !token.IsActive || token.IsDeleted)
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.ConnectionUnusable, "That user token is not valid, or it has been revoked.");
        if (token.TenantId != realmTenantId)
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.ConnectionUnusable, "That user token does not belong to the realm you entered.");
        var allowed = token.AccessAllApps ? (IReadOnlySet<long>)new HashSet<long>() : await userTokens.GetAllowedAppIdsAsync(token.Id, ct);
        return new ConnectedCredential(realmTenantId, token.UserId, true, token.AccessAllApps, allowed);
    }

    /// <summary>G3 for a saved connection: re-checks the stored credential and marks the connection unusable when it fails.</summary>
    public async Task<ConnectedCredential> ResolveSavedAsync(ConnectedConnection connection, long consumerTenantId, CancellationToken ct)
    {
        if (connection.Status != ConnectedConnectionStatuses.Active)
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.ConnectionUnusable,
                $"The connection '{connection.Name}' is {connection.Status}. Reconnect it before using it.");

        if (connection.AuthMode == ConnectedAuthModes.SameRealmUser)
        {
            if (connection.SourceTenantId != consumerTenantId || connection.RunAsUserId != connection.OwnerUserId)
                throw await UnusableAsync(connection, ConnectedConnectionStatuses.Unavailable, "This connection can only read apps in this realm. Reconnect it with a user token.", ct);
            return new ConnectedCredential(consumerTenantId, connection.OwnerUserId, false, true, new HashSet<long>());
        }

        if (string.IsNullOrEmpty(connection.UserTokenHash))
            throw await UnusableAsync(connection, ConnectedConnectionStatuses.Unavailable, "This connection has no stored credential. Reconnect it.", ct);

        var token = await userTokens.GetByHashAsync(connection.UserTokenHash, ct);
        if (token is null || !token.IsActive || token.IsDeleted)
            throw await UnusableAsync(connection, ConnectedConnectionStatuses.Revoked, "The user token behind this connection has been revoked. Reconnect it.", ct);
        if (token.UserId != connection.RunAsUserId || token.TenantId != connection.SourceTenantId)
            throw await UnusableAsync(connection, ConnectedConnectionStatuses.Unavailable, "The user token behind this connection no longer matches the realm it was connected to. Reconnect it.", ct);

        var allowed = token.AccessAllApps ? (IReadOnlySet<long>)new HashSet<long>() : await userTokens.GetAllowedAppIdsAsync(token.Id, ct);
        await userTokens.UpdateLastUsedAtAsync(token.Id, ct);
        return new ConnectedCredential(connection.SourceTenantId, token.UserId, true, token.AccessAllApps, allowed);
    }

    /// <summary>Opens a saved connection: G2–G8 and G12. The caller then checks the table (G9–G11) with <see cref="GetSourceTableAsync"/>.</summary>
    public async Task<ConnectedSourceAccess> OpenAsync(ConnectedConnection connection, long consumerTenantId, CancellationToken ct)
    {
        var credential = await ResolveSavedAsync(connection, consumerTenantId, ct);
        var access = await OpenAsync(credential, connection.SourceAppPublicId, connection.AppTokenHash, consumerTenantId, ct);
        await connections.TouchAsync(connection.Id, verified: true, ct);
        return access;
    }

    /// <summary>G2, G4–G8 and G12 for a credential and a source app.</summary>
    public async Task<ConnectedSourceAccess> OpenAsync(ConnectedCredential credential, Guid sourceAppPublicId, string? appTokenHash, long consumerTenantId, CancellationToken ct)
    {
        var crossTenant = credential.SourceTenantId != consumerTenantId;

        // G2: this realm may pull from other realms.
        if (crossTenant && !(await control.GetPolicyAsync(consumerTenantId, ct)).InboundEnabled)
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.InboundDisabled, "Your realm does not allow tables connected to other realms.");

        // G4: the source realm is up.
        var provider = await control.GetTenantAsync(credential.SourceTenantId, ct);
        if (provider is null || !provider.IsUsable)
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.SourceUnavailable, "The source realm is not available right now.");

        ConnectedDataShare? share = null;
        if (crossTenant)
        {
            // G5: the source realm agreed to share with this realm.
            share = await RequireShareAsync(credential.SourceTenantId, consumerTenantId, sourceAppPublicId, ct);

            // G12: copying between servers needs the share to allow it.
            var consumer = await control.GetTenantAsync(consumerTenantId, ct);
            if (!share.AllowCrossRegion && !SameServer(provider.ServerRef, consumer?.ServerRef))
                throw ConnectedTableException.Forbidden(ConnectedErrorCodes.CrossRegion, "The source realm is hosted in a different region, and its sharing approval does not allow copying data across regions.");
        }

        // G6: the reading user is active and still a member of the source realm (checked while the scope opens).
        var scope = await scopes.OpenAsync(new ConnectedSourceIdentity(credential.SourceTenantId, credential.RunAsUserId,
            credential.IsUserToken, credential.TokenAccessAllApps, credential.AllowedAppIds), ct);
        try
        {
            var app = await scope.FindAppAsync(sourceAppPublicId, ct)
                ?? throw ConnectedTableException.Forbidden(ConnectedErrorCodes.SourceUnavailable, "The source app is not available.");

            // G7: the token may be limited to some apps.
            if (credential.IsUserToken && !credential.TokenAccessAllApps && !credential.AllowedAppIds.Contains(app.Id))
                throw ConnectedTableException.Forbidden(ConnectedErrorCodes.SourceUnavailable, "The user token does not give access to the source app.");

            // Membership of the source app (a non-member would otherwise read as "unrestricted").
            var permissions = await scope.GetAppPermissionsAsync(app.Id, ct);
            if (permissions.Count == 0)
                throw ConnectedTableException.Forbidden(ConnectedErrorCodes.SourceUnavailable, "The connected user is not a member of the source app.");
            var roles = await scope.GetAppRoleNamesAsync(app.Id, ct);
            var isAdmin = roles.Any(r => string.Equals(r, AdminRoleName, StringComparison.OrdinalIgnoreCase));

            // G8: the source app's own connection settings.
            var security = ParseSecurity(app.SecurityOptions);
            if (!security.AllowNonAdminsToConnect && !isAdmin)
                throw ConnectedTableException.Forbidden(ConnectedErrorCodes.SourceAppDisallows, "The source app only allows its administrators to connect to it.");
            if (!string.IsNullOrEmpty(appTokenHash))
            {
                if (!await scope.IsValidAppTokenAsync(app.Id, appTokenHash, ct))
                    throw ConnectedTableException.Forbidden(ConnectedErrorCodes.SourceAppDisallows, "The app token is not valid for the source app.");
            }
            else if (crossTenant && security.RequireAppTokens)
            {
                throw ConnectedTableException.Forbidden(ConnectedErrorCodes.SourceAppDisallows, "The source app requires an app token for connections from other realms. Add one to the connection.");
            }

            return new ConnectedSourceAccess(scope, app, crossTenant, share, isAdmin);
        }
        catch
        {
            await scope.DisposeAsync();
            throw;
        }
    }

    /// <summary>G9–G11: the reading user can see the table; which fields and records they can see.</summary>
    public async Task<ConnectedSourceTable> GetSourceTableAsync(ConnectedSourceAccess access, Guid tablePublicId, CancellationToken ct)
    {
        var table = await access.Scope.FindTableAsync(tablePublicId, ct);
        if (table is null || table.AppId != access.App.Id)
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.SourceUnavailable, "The source table is not available.");
        return await DescribeAsync(access, table, ct);
    }

    public async Task<ConnectedSourceTable> DescribeAsync(ConnectedSourceAccess access, AppTable table, CancellationToken ct)
    {
        var fields = await access.Scope.ListFieldsAsync(table.Id, ct);
        var tableAccess = await access.Scope.GetTableAccessAsync(table, fields, ct);
        if (!tableAccess.CanView)
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.SourceUnavailable, "The connected user cannot view the source table.");
        var visible = (tableAccess.Unrestricted ? fields : tableAccess.VisibleFields)
            .Where(f => f.Fid.HasValue && !f.IsDeleted && !f.IsHidden)
            .ToList();
        return new ConnectedSourceTable(table, fields, visible, tableAccess);
    }

    /// <summary>G5 on its own: the approved, unexpired share that covers the app (or an explanation of what is missing).</summary>
    public async Task<ConnectedDataShare> RequireShareAsync(long providerTenantId, long consumerTenantId, Guid appPublicId, CancellationToken ct)
    {
        var policy = await control.GetPolicyAsync(providerTenantId, ct);
        if (policy.OutboundPolicy == ConnectedOutboundPolicies.Disabled)
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.DataShareNotApproved, "The source realm does not share data with other realms.");
        var share = await control.GetOpenShareAsync(providerTenantId, consumerTenantId, ct);
        if (share is null || share.Status != ConnectedDataShareStatuses.Approved)
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.DataShareNotApproved, share is null
                ? "The source realm hasn't approved sharing data with your realm. Ask its administrators for access."
                : "Your realm's request to use data from the source realm is still waiting for approval.");
        if (share.ExpiresOn is { } expires && expires <= DateTime.UtcNow)
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.DataShareNotApproved, "The source realm's approval to share data with your realm has expired.");
        if (!ShareCoversApp(share, appPublicId))
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.DataShareNotApproved, "The source realm's approval does not include this app.");
        return share;
    }

    public static bool ShareCoversApp(ConnectedDataShare share, Guid appPublicId)
    {
        if (share.Scope == ConnectedDataShareScopes.AllApps) return true;
        if (string.IsNullOrWhiteSpace(share.AppPublicIdsJson)) return false;
        try { return (JsonSerializer.Deserialize<List<Guid>>(share.AppPublicIdsJson) ?? []).Contains(appPublicId); }
        catch (JsonException) { return false; }
    }

    /// <summary>Tenants on the same database server (or with no server recorded, i.e. the default one) count as the same region.</summary>
    public static bool SameServer(string? a, string? b) =>
        string.Equals(string.IsNullOrWhiteSpace(a) ? "" : a.Trim(), string.IsNullOrWhiteSpace(b) ? "" : b.Trim(), StringComparison.OrdinalIgnoreCase);

    public static AppSecurityOptionsSettings ParseSecurity(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new AppSecurityOptionsSettings();
        try { return JsonSerializer.Deserialize<AppSecurityOptionsSettings>(json, JsonOptions) ?? new AppSecurityOptionsSettings(); }
        catch (JsonException) { return new AppSecurityOptionsSettings(); }
    }

    /// <summary>Whether a field type has a stored value that can be copied (used to grey out fields in the wizard).</summary>
    public static bool IsSystemField(AppField field) => field.IsSystem && field.Fid is >= 1 and <= 5;

    private async Task<ConnectedTableException> UnusableAsync(ConnectedConnection connection, string status, string message, CancellationToken ct)
    {
        await connections.SetStatusAsync(connection.Id, status, message, ct);
        return ConnectedTableException.Forbidden(ConnectedErrorCodes.ConnectionUnusable, message);
    }
}
