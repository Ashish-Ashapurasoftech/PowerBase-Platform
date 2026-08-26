using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Connections;
using PowerBase.Application.Connections.Commands.CreateConnection;
using PowerBase.Application.Connections.Common;
using PowerBase.Application.Connections.Queries.GetConnectionApps;
using PowerBase.Application.Connections.Queries.GetConnectionFields;
using PowerBase.Application.Connections.Queries.GetConnections;
using PowerBase.Application.Connections.Queries.GetConnectionTables;
using System;

namespace PowerBase.API.Controllers;

/// <summary>
/// Saved PowerFlows accounts ("Connect new account") and the metadata reachable through them.
///
/// Every metadata endpoint resolves the account's own credentials server-side and reads the
/// account's realm in an isolated scope. The caller's own tenant/session is never switched, and
/// no credential is ever handed to the browser.
/// </summary>
[ApiController]
[Route("")]
[RequireAuth]
public class PipelineConnectionsController : ControllerBase
{
    private readonly GetConnectionsQueryHandler _listHandler;
    private readonly CreateConnectionCommandHandler _createHandler;
    private readonly GetConnectionAppsQueryHandler _appsHandler;
    private readonly GetConnectionTablesQueryHandler _tablesHandler;
    private readonly GetConnectionFieldsQueryHandler _fieldsHandler;

    public PipelineConnectionsController(
        GetConnectionsQueryHandler listHandler,
        CreateConnectionCommandHandler createHandler,
        GetConnectionAppsQueryHandler appsHandler,
        GetConnectionTablesQueryHandler tablesHandler,
        GetConnectionFieldsQueryHandler fieldsHandler)
    {
        _listHandler = listHandler;
        _createHandler = createHandler;
        _appsHandler = appsHandler;
        _tablesHandler = tablesHandler;
        _fieldsHandler = fieldsHandler;
    }

    /// <summary>List the saved accounts the logged-in user can pick in a PowerFlows step.</summary>
    /// <remarks>Display-safe only: the masked token prefix is returned, never the token itself.</remarks>
    [HttpGet("pipelines/connections")]
    [ProducesResponseType(typeof(ApiListResponse<PipelineAccountDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var result = await _listHandler.HandleAsync(new GetConnectionsQuery(), ct);
        return Ok(new ApiListResponse<PipelineAccountDto>(result.Items, result.Items.Count, 1, result.Items.Count));
    }

    /// <summary>Connect a new account using a user token.</summary>
    [HttpPost("pipelines/connections")]
    [ProducesResponseType(typeof(ApiResponse<PipelineAccountDto>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create([FromBody] CreatePipelineConnectionRequest request, CancellationToken ct)
    {
        var command = new CreateConnectionCommand(
            request.AuthMode,
            request.Subdomain,
            request.UserToken,
            request.Name);

        var account = await _createHandler.HandleAsync(command, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<PipelineAccountDto>(account));
    }

    /// <summary>List apps visible through a saved account.</summary>
    [HttpGet("pipelines/connections/{connectionId:guid}/apps")]
    [ProducesResponseType(typeof(ApiListResponse<ConnectionAppDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetApps(Guid connectionId, CancellationToken ct)
    {
        var apps = await _appsHandler.HandleAsync(new GetConnectionAppsQuery(connectionId), ct);
        return Ok(new ApiListResponse<ConnectionAppDto>(apps, apps.Count, 1, apps.Count));
    }

    /// <summary>List tables of an app visible through a saved account.</summary>
    [HttpGet("pipelines/connections/{connectionId:guid}/apps/{appId:guid}/tables")]
    [ProducesResponseType(typeof(ApiListResponse<ConnectionTableDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTables(Guid connectionId, Guid appId, CancellationToken ct)
    {
        var tables = await _tablesHandler.HandleAsync(new GetConnectionTablesQuery(connectionId, appId), ct);
        return Ok(new ApiListResponse<ConnectionTableDto>(tables, tables.Count, 1, tables.Count));
    }

    /// <summary>List fields of a table visible through a saved account.</summary>
    [HttpGet("pipelines/connections/{connectionId:guid}/tables/{tableId:guid}/fields")]
    [ProducesResponseType(typeof(ApiListResponse<ConnectionFieldDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFields(Guid connectionId, Guid tableId, CancellationToken ct)
    {
        var fields = await _fieldsHandler.HandleAsync(new GetConnectionFieldsQuery(connectionId, tableId), ct);
        return Ok(new ApiListResponse<ConnectionFieldDto>(fields, fields.Count, 1, fields.Count));
    }
}
