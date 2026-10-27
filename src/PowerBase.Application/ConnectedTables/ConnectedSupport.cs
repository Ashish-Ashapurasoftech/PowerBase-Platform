using System.Security.Cryptography;
using System.Text;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.ConnectedTables;

/// <summary>Destination-side permission rules of the module (plan §5.4), evaluated against the existing app roles.</summary>
public sealed class ConnectedPermissions(
    IQueryContext context,
    IAppAccessService appAccess,
    IAppUserRepository appUsers,
    IAppRepository apps,
    IConnectedTableRepository connectedTables)
{
    private const string AdminRoleName = "Administrator";

    public long UserId => context.UserId;

    public async Task<bool> IsAppAdminAsync(long appId, CancellationToken ct)
    {
        if (context.IsSuperAdmin) return true;
        var roles = await appUsers.GetUserAppRoleNamesAsync(appId, context.UserId, ct);
        if (roles.Any(r => string.Equals(r, AdminRoleName, StringComparison.OrdinalIgnoreCase))) return true;
        var permissions = await appUsers.GetUserAppPermissionsAsync(appId, context.UserId, ct);
        return permissions.Contains(PermissionCodes.AppsUpdate);
    }

    /// <summary>The caller can see the app (and therefore its connected tables' settings and history).</summary>
    public async Task RequireMemberAsync(long appId, CancellationToken ct)
    {
        var appPublicId = await apps.GetPublicIdByIdAsync(appId, ct);
        await appAccess.RequireMembershipByAppPublicIdAsync(appPublicId, ct);
    }

    /// <summary>G1: the caller may create tables in the destination app.</summary>
    public async Task RequireCanCreateTablesAsync(long appId, CancellationToken ct)
    {
        if (context.IsSuperAdmin) return;
        await appAccess.RequirePermissionByAppIdAsync(appId, PermissionCodes.TablesCreate, ct);
    }

    public bool IsOwner(ConnectedTable table) => table.OwnerUserId == context.UserId;

    /// <summary>Filter, connected fields and refresh key: the connection owner only (their credential defines what is reachable).</summary>
    public void RequireOwner(ConnectedTable table)
    {
        if (!IsOwner(table) && !context.IsSuperAdmin)
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.Forbidden, "Only the owner of this table's connection can change its filter, connected fields or refresh key.");
    }

    public async Task RequireOwnerOrAdminAsync(ConnectedTable table, CancellationToken ct)
    {
        if (IsOwner(table) || await IsAppAdminAsync(table.AppId, ct)) return;
        throw ConnectedTableException.Forbidden(ConnectedErrorCodes.Forbidden, "Only the connection owner or an app administrator can change this.");
    }

    public async Task RequireAdminAsync(long appId, CancellationToken ct)
    {
        if (!await IsAppAdminAsync(appId, ct))
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.Forbidden, "Only an app administrator can do this.");
    }

    public async Task<bool> CanRefreshAsync(ConnectedTable table, CancellationToken ct)
    {
        if (IsOwner(table) || await IsAppAdminAsync(table.AppId, ct)) return true;
        var allowed = await connectedTables.ListRefreshRoleIdsAsync(table.Id, ct);
        if (allowed.Count == 0) return false;
        var mine = await appUsers.GetUserAppRoleIdsAsync(table.AppId, context.UserId, ct);
        return mine.Any(allowed.Contains);
    }

    public async Task RequireCanRefreshAsync(ConnectedTable table, CancellationToken ct)
    {
        await RequireMemberAsync(table.AppId, ct);
        if (!await CanRefreshAsync(table, ct))
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.Forbidden, "Your role can't refresh this connected table.");
    }

    public void RequireTenantAdmin()
    {
        if (!context.IsTenantAdmin && !context.IsSuperAdmin)
            throw ConnectedTableException.Forbidden(ConnectedErrorCodes.Forbidden, "Only a realm administrator can manage data sharing.");
    }
}

public static class ConnectedTokens
{
    /// <summary>SHA-256 hex (lowercase), the same hashing PowerBase applies to user tokens and app tokens.</summary>
    public static string Hash(string raw) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw.Trim()))).ToLowerInvariant();

    /// <summary>Masked prefix kept for display only.</summary>
    public static string Prefix(string raw)
    {
        raw = raw.Trim();
        return raw.Length <= 10 ? new string('•', raw.Length) : raw[..10] + "…";
    }

    /// <summary>"https://acme.powerbase.app/app/…", "acme.powerbase.app" or "acme" → "acme".</summary>
    public static string RealmSlug(string input)
    {
        var text = (input ?? string.Empty).Trim();
        if (text.Contains("://", StringComparison.Ordinal) && Uri.TryCreate(text, UriKind.Absolute, out var uri)) text = uri.Host;
        else if (text.Contains('/')) text = text[..text.IndexOf('/')];
        var dot = text.IndexOf('.');
        if (dot > 0) text = text[..dot];
        return text.Trim().ToLowerInvariant();
    }

    public static string RowVersion(byte[] value) => Convert.ToBase64String(value ?? []);

    public static byte[] RowVersion(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new ValidationException(new Dictionary<string, string[]> { ["RowVersion"] = ["The row version is required."] });
        try { return Convert.FromBase64String(value); }
        catch (FormatException) { throw new ValidationException(new Dictionary<string, string[]> { ["RowVersion"] = ["The row version is not valid."] }); }
    }
}
