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

    private readonly Dictionary<long, (AppTable Parent, AppField? Key, IReadOnlyList<AppField> ReadFields)?> _referenceTargets = [];

    /// <summary>The text each parent record shows in <paramref name="reference"/> (the relationship's display key, else the parent
    /// table's key, else its Record ID#), by parent Record ID#. Parents that no longer exist are left out.</summary>
    public async Task<IReadOnlyDictionary<long, string?>> ReadReferenceKeysAsync(AppField reference, IReadOnlyCollection<long> parentIds, CancellationToken ct)
    {
        var result = new Dictionary<long, string?>();
        if (parentIds.Count == 0) return result;

        if (!_referenceTargets.TryGetValue(reference.Id, out var target))
        {
            target = null;
            var settings = ReadReferenceSettings(reference.Settings);
            if (settings?.ParentTableId is long parentTableId)
            {
                AppTable? parent = null;
                try { parent = await Get<IAppTableRepository>().GetByIdAsync(parentTableId, ct); }
                catch (NotFoundException) { }
                if (parent is not null && !parent.IsDeleted)
                {
                    var relationship = settings.RelationshipId is long relId ? await Get<IRelationshipRepository>().GetByIdAsync(relId, ct) : null;
                    var parentFields = await ListFieldsAsync(parent.Id, ct);
                    var key = KeyFieldResolver.ResolveDisplayKey(relationship, parent, parentFields);
                    var recordId = parentFields.FirstOrDefault(f => f.IsSystem && f.Fid == ConnectedSystemFids.RecordId);
                    var readFields = new[] { recordId, key }.Where(f => f is not null).Select(f => f!).ToList();
                    if (readFields.Count > 0) target = (parent, key, readFields);
                }
            }
            _referenceTargets[reference.Id] = target;
        }
        if (target is not { } t) return result;

        var records = Get<IRecordRepository>();
        foreach (var batch in parentIds.Distinct().Chunk(1000))
        {
            var filter = new FilterGroup
            {
                Logic = "and",
                Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = ConnectedSystemFids.RecordId, Operator = "in", Value = System.Text.Json.JsonSerializer.Serialize(batch) } }],
            };
            var rows = await records.ListAllMatchingAsync(t.Parent, t.ReadFields, filter, ct: ct);
            foreach (var row in rows)
            {
                var id = Convert.ToInt64(row["Id"], CultureInfo.InvariantCulture);
                if (t.Key is null) { result[id] = id.ToString(CultureInfo.InvariantCulture); continue; }
                var column = KeyFieldResolver.ColumnName(t.Key);
                var raw = row.TryGetValue(column, out var v) ? v : null;
                var text = raw is null or DBNull ? null : KeyFieldResolver.FormatForSubmit(raw);
                result[id] = string.IsNullOrEmpty(text) ? null : text;
            }
        }
        return result;
    }

    private static PowerBase.Domain.FieldSettings.ReferenceSettings? ReadReferenceSettings(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try { return System.Text.Json.JsonSerializer.Deserialize<PowerBase.Domain.FieldSettings.ReferenceSettings>(json, new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true }); }
        catch (System.Text.Json.JsonException) { return null; }
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
