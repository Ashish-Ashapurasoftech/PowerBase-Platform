using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.ConnectedTables.Access;

/// <summary>Who reads the source: a tenant, a user in it, and (for a user token) the token's app restrictions.</summary>
public sealed record ConnectedSourceIdentity(long TenantId, long UserId, bool IsUserToken, bool TokenAccessAllApps, IReadOnlySet<long> AllowedAppIds);

/// <summary>A DI scope pinned to the source tenant and the reading user. It exposes reads only: nothing here can change the
/// source app's data or structure. (The one write is the provider-side activity entry recording that data was read.)
/// A fresh scope is required because the tenant connection string is frozen per scope once resolved; the caller's own
/// scope and identity are never touched.</summary>
public sealed class ConnectedSourceScope : IAsyncDisposable
{
    private readonly IServiceScope _scope;

    internal ConnectedSourceScope(IServiceScope scope, ConnectedSourceIdentity identity)
    {
        _scope = scope;
        Identity = identity;
    }

    public ConnectedSourceIdentity Identity { get; }

    private T Get<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public async Task<App?> FindAppAsync(Guid appPublicId, CancellationToken ct)
    {
        try
        {
            var app = await Get<IAppRepository>().GetByPublicIdAsync(appPublicId, ct);
            return app.IsDeleted ? null : app;
        }
        catch (NotFoundException) { return null; }
    }

    public async Task<IReadOnlyList<AppTable>> ListTablesAsync(long appId, CancellationToken ct) =>
        (await Get<IAppTableRepository>().ListByAppAsync(appId, ct)).ToList();

    public async Task<AppTable?> FindTableAsync(Guid tablePublicId, CancellationToken ct)
    {
        try { return await Get<IAppTableRepository>().GetByPublicIdAsync(tablePublicId, ct); }
        catch (NotFoundException) { return null; }
    }

    public async Task<IReadOnlyList<AppField>> ListFieldsAsync(long tableId, CancellationToken ct) =>
        (await Get<IAppFieldRepository>().ListByTableAsync(tableId, ct) ?? Array.Empty<AppField>()).Where(f => !f.IsDeleted).ToList();

    public Task<TableAccessContext> GetTableAccessAsync(AppTable table, IReadOnlyList<AppField> fields, CancellationToken ct) =>
        Get<IRolePermissionEnforcer>().GetTableAccessAsync(table, fields, ct);

    public Task<IReadOnlySet<string>> GetAppPermissionsAsync(long appId, CancellationToken ct) =>
        Get<IAppUserRepository>().GetUserAppPermissionsAsync(appId, Identity.UserId, ct);

    public Task<IReadOnlyList<string>> GetAppRoleNamesAsync(long appId, CancellationToken ct) =>
        Get<IAppUserRepository>().GetUserAppRoleNamesAsync(appId, Identity.UserId, ct);

    public Task<bool> IsValidAppTokenAsync(long appId, string tokenHash, CancellationToken ct) =>
        Get<IConnectedAppTokenReader>().IsValidAsync(appId, tokenHash, ct);

    /// <summary>The next <paramref name="take"/> live records after <paramref name="afterId"/> up to <paramref name="maxId"/> that
    /// match <paramref name="filter"/> (keyset paging on Record ID#, decrypted).</summary>
    public Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadChunkAsync(
        AppTable table, IReadOnlyList<AppField> readFields, FilterGroup? filter, long afterId, long maxId, int take, long? restrictToCreatedBy, CancellationToken ct)
    {
        var bounded = new FilterGroup { Logic = "and" };
        if (filter is not null && filter.Nodes.Count > 0) bounded.Nodes.Add(new FilterNode { Group = filter });
        bounded.Nodes.Add(new FilterNode { Condition = new FilterCondition { FieldId = ConnectedSystemFids.RecordId, Operator = "lte", Value = maxId.ToString(CultureInfo.InvariantCulture) } });
        return Get<IRecordRepository>().ListBatchAfterIdAsync(table, readFields, afterId, take, bounded, restrictToCreatedBy, ct);
    }

    public Task<int> CountAsync(AppTable table, IReadOnlyList<AppField> fields, FilterGroup? filter, long? restrictToCreatedBy, CancellationToken ct) =>
        Get<IRecordRepository>().CountAsync(table, fields, filter, restrictToCreatedBy, ct);

    /// <summary>The highest live Record ID# (0 when the table is empty).</summary>
    public async Task<long> GetMaxRecordIdAsync(AppTable table, AppField recordIdField, CancellationToken ct)
    {
        var rows = await Get<IRecordRepository>().ListAsync(table, [recordIdField], 1, 1, null,
            [new SortSpec { FieldId = ConnectedSystemFids.RecordId, Desc = true }], null, ct);
        return rows.Count == 0 ? 0 : Convert.ToInt64(rows[0]["Id"], CultureInfo.InvariantCulture);
    }

    /// <summary>Computes lookup / summary / formula values for rows read from the source.</summary>
    public async Task<IReadOnlyList<IReadOnlyDictionary<long, object?>>> ProjectAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyList<IReadOnlyDictionary<string, object?>> rows, CancellationToken ct)
    {
        var related = await Get<IRelationalProjector>().ProjectAsync(table, fields, rows, ct);
        return Get<IFormulaProjector>().Project(fields, rows, related, table);
    }

    public IReadOnlySet<long>? GetFormulaDependencies(IReadOnlyList<AppField> fields, IEnumerable<long> formulaFids) =>
        Get<IFormulaProjector>().GetFormulaDependencies(fields, formulaFids);

    /// <summary>Records on the provider side that a consumer read this table (the provider's own activity log).</summary>
    public Task LogProviderReadAsync(AppTable table, string consumerDescription, long rows, CancellationToken ct) =>
        Get<IAuditRepository>().LogActivityAsync("ConnectedSourceRead", "AppTable", table.PublicId.ToString(),
            $"Connected table read {rows:N0} records of {table.Name} for {consumerDescription}", table.AppId, ct: ct);

    public async ValueTask DisposeAsync()
    {
        if (_scope is IAsyncDisposable asyncDisposable) await asyncDisposable.DisposeAsync();
        else _scope.Dispose();
    }
}

/// <summary>Opens <see cref="ConnectedSourceScope"/>s. Mirrors how PowerFlows pins a target-tenant scope, but is the module's
/// own copy so the two features stay independent.</summary>
public sealed class ConnectedSourceScopeFactory(IServiceScopeFactory scopeFactory)
{
    public async Task<ConnectedSourceScope> OpenAsync(ConnectedSourceIdentity identity, CancellationToken ct)
    {
        var scope = scopeFactory.CreateScope();
        try
        {
            var context = scope.ServiceProvider.GetRequiredService<IQueryContext>();
            // The tenant first: the lookups below are tenant-scoped.
            context.SetTenantId(identity.TenantId);

            User user;
            try { user = await scope.ServiceProvider.GetRequiredService<IUserRepository>().GetByIdAsync(identity.UserId, ct); }
            catch (NotFoundException) { throw ConnectedTableException.Forbidden(ConnectedErrorCodes.ConnectionUnusable, "The user behind this connection no longer exists. Reconnect it."); }
            if (!user.IsActive || user.IsDeleted)
                throw ConnectedTableException.Forbidden(ConnectedErrorCodes.ConnectionUnusable, "The user behind this connection is no longer active. Reconnect it.");

            var tenants = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
            var tenantRole = await tenants.GetUserRoleNameInTenantAsync(identity.UserId, identity.TenantId, ct) ?? string.Empty;
            var permissions = await scope.ServiceProvider.GetRequiredService<IUserPermissionRepository>().GetPermissionsAsync(identity.UserId, identity.TenantId, ct);

            // Never as a super admin: the read must be bounded by the reading user's own app permissions.
            context.SetUserIdentity(identity.UserId, isSuperAdmin: false, user.Name, user.Email, permissions, tenantRole);
            context.SetTokenScope(identity.IsUserToken, identity.TokenAccessAllApps, identity.AllowedAppIds);

            if (!await tenants.IsActiveMemberAsync(identity.UserId, ct))
                throw ConnectedTableException.Forbidden(ConnectedErrorCodes.ConnectionUnusable, "The user behind this connection is not an active member of the source realm. Reconnect it.");

            return new ConnectedSourceScope(scope, identity);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }
}
