using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Connections.Common;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Connections.Queries.GetConnectionTables;

/// <summary>
/// Lists tables through a saved account.
///
/// Access is checked inside the target realm by the scoped <see cref="IAppAccessService"/>, which
/// also enforces the token's app restrictions — so a restricted token cannot read tables of an app
/// it was not granted.
/// </summary>
public class GetConnectionTablesQueryHandler
{
    private readonly ConnectionScopeResolver _scopeResolver;
    private readonly IServiceScopeFactory _scopeFactory;

    public GetConnectionTablesQueryHandler(
        ConnectionScopeResolver scopeResolver,
        IServiceScopeFactory scopeFactory)
    {
        _scopeResolver = scopeResolver;
        _scopeFactory = scopeFactory;
    }

    public async Task<IReadOnlyList<ConnectionTableDto>> HandleAsync(GetConnectionTablesQuery query, CancellationToken ct = default)
    {
        var connection = await _scopeResolver.TryResolveAsync(query.ConnectionPublicId, ct)
            ?? throw new NotFoundException("Connection", query.ConnectionPublicId);

        await using var targetScope = await TargetTenantScopeHelper.OpenAsync(_scopeFactory, connection, ct);

        var appAccess = targetScope.GetRequiredService<IAppAccessService>();
        await appAccess.RequirePermissionByAppPublicIdAsync(query.AppPublicId, PermissionCodes.PowerFlowsRead, ct);

        var appRepo = targetScope.GetRequiredService<IAppRepository>();
        var tableRepo = targetScope.GetRequiredService<IAppTableRepository>();

        var appId = await appRepo.GetIdByPublicIdAsync(query.AppPublicId, ct);
        var tables = await tableRepo.ListByAppAsync(appId, ct);

        return tables
            .Select(t => new ConnectionTableDto { PublicId = t.PublicId, Name = t.Name })
            .OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}
