using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using PowerBase.API.Attributes;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Connections.Common;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.API.Controllers;

/// <summary>Metadata required only by the bulk-upsert steps.</summary>
[ApiController]
[Route("pipelines/bulk-upsert")]
[RequireAuth]
public class PipelineBulkUpsertController : ControllerBase
{
    private readonly ConnectionScopeResolver _connections;
    private readonly IServiceScopeFactory _scopes;

    public PipelineBulkUpsertController(ConnectionScopeResolver connections, IServiceScopeFactory scopes)
    {
        _connections = connections;
        _scopes = scopes;
    }

    [HttpGet("connections/{connectionId:guid}/tables/{tableId:guid}/fields")]
    public async Task<IActionResult> GetFields(Guid connectionId, Guid tableId, CancellationToken ct)
    {
        var connection = await _connections.TryResolveAsync(connectionId, ct)
            ?? throw new NotFoundException("Connection", connectionId);
        await using var scope = await TargetTenantScopeHelper.OpenAsync(_scopes, connection, ct);
        await scope.GetRequiredService<IAppAccessService>()
            .RequirePermissionByTablePublicIdAsync(tableId, PermissionCodes.PowerFlowsRead, ct);
        var table = await scope.GetRequiredService<IAppTableRepository>().GetByPublicIdAsync(tableId, ct);
        var fields = await scope.GetRequiredService<IAppFieldRepository>().ListByTableAsync(table.Id, ct);
        return Ok(new { data = fields.Select(f => new
        {
            f.PublicId, f.Name, f.Label, f.TypeCode, f.Fid,
            f.Settings, f.DefaultValue, f.IsRequired, f.IsUnique, f.IsSystem
        }) });
    }
}
