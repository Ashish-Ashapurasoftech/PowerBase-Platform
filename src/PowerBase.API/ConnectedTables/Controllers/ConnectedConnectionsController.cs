using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.ConnectedTables.Models;
using PowerBase.Application.ConnectedTables.Handlers;

namespace PowerBase.API.ConnectedTables.Controllers;

/// <summary>My connections for Connected Tables: credentials that read one source app, in this realm or another.</summary>
[ApiController]
[Route("connected/connections")]
[RequireAuth]
[ConnectedTablesExceptionFilter]
public sealed class ConnectedConnectionsController(ConnectionHandlers handlers) : ControllerBase
{
    /// <summary>List the connections I own.</summary>
    [HttpGet]
    public async Task<IActionResult> List(CancellationToken ct) => Ok(new { data = await handlers.ListMineAsync(ct) });

    /// <summary>Create a connection. Every access check runs before it is saved; the user token is stored only as a hash.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateConnectionRequest request, CancellationToken ct) =>
        Ok(new { data = await handlers.CreateAsync(new CreateConnectionCommand(request.Name, request.AuthMode, request.Realm, request.AppId, request.UserToken, request.AppToken), ct) });

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(new { data = await handlers.GetAsync(id, ct) });

    /// <summary>Rename, or replace the user token / app token (re-verified before saving).</summary>
    [HttpPatch("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateConnectionRequest request, CancellationToken ct) =>
        Ok(new { data = await handlers.UpdateAsync(new UpdateConnectionCommand(id, request.Name, request.UserToken, request.AppToken, request.ClearAppToken, request.RowVersion), ct) });

    /// <summary>Run every access check again.</summary>
    [HttpPost("{id:guid}/test")]
    public async Task<IActionResult> Test(Guid id, CancellationToken ct) => Ok(new { data = await handlers.TestAsync(id, ct) });

    /// <summary>Delete a connection no connected table uses.</summary>
    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id, [FromQuery] string rowVersion, CancellationToken ct)
    {
        await handlers.DeleteAsync(id, rowVersion, ct);
        return NoContent();
    }

    /// <summary>The source tables the connected user can view.</summary>
    [HttpGet("{id:guid}/tables")]
    public async Task<IActionResult> Tables(Guid id, CancellationToken ct) => Ok(new { data = await handlers.ListSourceTablesAsync(id, ct) });

    /// <summary>The fields of a source table, each with the destination type it would become (or why it can't be connected).</summary>
    [HttpGet("{id:guid}/tables/{tableId:guid}/fields")]
    public async Task<IActionResult> Fields(Guid id, Guid tableId, CancellationToken ct) =>
        Ok(new { data = await handlers.ListSourceFieldsAsync(id, tableId, ct) });

    /// <summary>How many source records a draft filter would connect.</summary>
    [HttpPost("{id:guid}/tables/{tableId:guid}/preview-count")]
    public async Task<IActionResult> PreviewCount(Guid id, Guid tableId, [FromBody] PreviewCountRequest request, CancellationToken ct) =>
        Ok(new { data = new { count = await handlers.PreviewCountAsync(new PreviewCountCommand(id, tableId, request.Filter), ct) } });
}
