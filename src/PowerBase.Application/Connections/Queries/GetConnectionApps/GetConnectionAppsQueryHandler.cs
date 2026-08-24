using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Connections.Common;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Connections.Queries.GetConnectionApps;

/// <summary>
/// Lists apps through a saved account.
///
/// The apps come from the account's own realm and are those visible to the token owner —
/// not to the logged-in user, and not from the logged-in user's realm. When the token is
/// app-restricted, only its permitted apps are returned.
/// </summary>
public class GetConnectionAppsQueryHandler
{
    private readonly ConnectionScopeResolver _scopeResolver;
    private readonly IServiceScopeFactory _scopeFactory;

    public GetConnectionAppsQueryHandler(
        ConnectionScopeResolver scopeResolver,
        IServiceScopeFactory scopeFactory)
    {
        _scopeResolver = scopeResolver;
        _scopeFactory = scopeFactory;
    }

    public async Task<IReadOnlyList<ConnectionAppDto>> HandleAsync(GetConnectionAppsQuery query, CancellationToken ct = default)
    {
        var connection = await _scopeResolver.TryResolveAsync(query.ConnectionPublicId, ct)
            ?? throw new NotFoundException("Connection", query.ConnectionPublicId);

        await using var targetScope = await TargetTenantScopeHelper.OpenAsync(_scopeFactory, connection, ct);

        var appRepo = targetScope.GetRequiredService<IAppRepository>();
        var apps = await appRepo.ListAllByUserAsync(connection.TargetUserId, ct);

        // Token app restrictions: the account can never reach further than its token does.
        var visible = connection.TokenAccessAllApps
            ? apps
            : apps.Where(a => connection.AllowedAppIds.Contains(a.Id)).ToList();

        return visible
            .Select(a => new ConnectionAppDto { PublicId = a.PublicId, Name = a.Name })
            .OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
