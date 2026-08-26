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

namespace PowerBase.Application.Connections.Queries.GetConnectionFields;

/// <summary>
/// Lists fields through a saved account, so a step's field mapping is built from the schema of
/// the realm the step will actually write to.
/// </summary>
public class GetConnectionFieldsQueryHandler
{
    private readonly ConnectionScopeResolver _scopeResolver;
    private readonly IServiceScopeFactory _scopeFactory;

    public GetConnectionFieldsQueryHandler(
        ConnectionScopeResolver scopeResolver,
        IServiceScopeFactory scopeFactory)
    {
        _scopeResolver = scopeResolver;
        _scopeFactory = scopeFactory;
    }

    public async Task<IReadOnlyList<ConnectionFieldDto>> HandleAsync(GetConnectionFieldsQuery query, CancellationToken ct = default)
    {
        var connection = await _scopeResolver.TryResolveAsync(query.ConnectionPublicId, ct)
            ?? throw new NotFoundException("Connection", query.ConnectionPublicId);

        await using var targetScope = await TargetTenantScopeHelper.OpenAsync(_scopeFactory, connection, ct);

        var appAccess = targetScope.GetRequiredService<IAppAccessService>();
        await appAccess.RequirePermissionByTablePublicIdAsync(query.TablePublicId, PermissionCodes.PowerFlowsRead, ct);

        var tableRepo = targetScope.GetRequiredService<IAppTableRepository>();
        var fieldRepo = targetScope.GetRequiredService<IAppFieldRepository>();

        var table = await tableRepo.GetByPublicIdAsync(query.TablePublicId, ct);
        var fields = await fieldRepo.ListByTableAsync(table.Id, ct);

        return fields.Select(f => new ConnectionFieldDto
        {
            PublicId = f.PublicId,
            Name = f.Name,
            Label = f.Label,
            TypeCode = f.TypeCode,
            Fid = f.Fid,
            Settings = f.Settings,
            DefaultValue = f.DefaultValue,
            IsRequired = f.IsRequired,
            IsSystem = f.IsSystem
        }).ToList();
    }
}
