using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Connections.Common;

/// <summary>
/// A DI scope pinned to a saved account's target tenant, carrying the token owner's
/// identity and the token's app restrictions. Every tenant-scoped repository resolved
/// from <see cref="Services"/> reads and writes the target tenant's database.
/// </summary>
public sealed class TargetTenantScope : IAsyncDisposable, IDisposable
{
    private readonly IServiceScope _scope;

    internal TargetTenantScope(IServiceScope scope, ConnectionScope connection)
    {
        _scope = scope;
        Connection = connection;
    }

    public ConnectionScope Connection { get; }

    public IServiceProvider Services => _scope.ServiceProvider;

    public T GetRequiredService<T>() where T : notnull => _scope.ServiceProvider.GetRequiredService<T>();

    public void Dispose() => _scope.Dispose();

    public async ValueTask DisposeAsync()
    {
        if (_scope is IAsyncDisposable asyncDisposable)
            await asyncDisposable.DisposeAsync();
        else
            _scope.Dispose();
    }
}

/// <summary>
/// Opens a <see cref="TargetTenantScope"/> for a verified <see cref="ConnectionScope"/>.
///
/// A fresh IServiceScope is mandatory: TenantConnectionFactory caches its connection string
/// per scope, so the tenant is frozen once resolved. This never mutates the caller's context,
/// so the user's own tenant/session is left untouched.
/// </summary>
public static class TargetTenantScopeHelper
{
    public static async Task<TargetTenantScope> OpenAsync(
        IServiceScopeFactory scopeFactory,
        ConnectionScope connection,
        CancellationToken ct = default)
    {
        var scope = scopeFactory.CreateScope();
        try
        {
            var queryContext = scope.ServiceProvider.GetRequiredService<IQueryContext>();

            // Pin the tenant first — the control-plane lookups below are tenant-sensitive.
            queryContext.SetTenantId(connection.TargetTenantId);

            var userRepo = scope.ServiceProvider.GetRequiredService<IUserRepository>();
            var tenantRepo = scope.ServiceProvider.GetRequiredService<ITenantRepository>();
            var permissionRepo = scope.ServiceProvider.GetRequiredService<IUserPermissionRepository>();

            var user = await userRepo.GetByIdAsync(connection.TargetUserId, ct);
            if (user == null || !user.IsActive || user.IsDeleted)
            {
                throw new UnauthorizedActionException(
                    "The user behind this connected account is no longer active.");
            }

            var tenantRole = await tenantRepo.GetUserRoleNameInTenantAsync(
                connection.TargetUserId, connection.TargetTenantId, ct) ?? string.Empty;

            var permissions = await permissionRepo.GetPermissionsAsync(
                connection.TargetUserId, connection.TargetTenantId, ct);

            // Work runs as the token owner in the target tenant — never as a super admin,
            // and never with the logged-in user's own permissions.
            queryContext.SetUserIdentity(
                connection.TargetUserId,
                isSuperAdmin: false,
                user.Name,
                user.Email,
                permissions,
                tenantRole);

            // Carry the token's app restrictions so AppAccessService enforces them here too.
            queryContext.SetTokenScope(
                connection.IsUserToken,
                connection.TokenAccessAllApps,
                connection.AllowedAppIds);

            // ── Gate 2c: membership of the target tenant, re-checked at use time ──────
            var isMember = await tenantRepo.IsActiveMemberAsync(connection.TargetUserId, ct);
            if (!isMember)
            {
                throw new UnauthorizedActionException(
                    "The user behind this connected account is not an active member of the connected realm.");
            }

            return new TargetTenantScope(scope, connection);
        }
        catch
        {
            scope.Dispose();
            throw;
        }
    }
}
