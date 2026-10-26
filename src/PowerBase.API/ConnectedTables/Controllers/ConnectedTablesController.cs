using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.ConnectedTables.Models;
using PowerBase.Application.ConnectedTables.Handlers;
using PowerBase.Domain.ConnectedTables;

namespace PowerBase.API.ConnectedTables.Controllers;

/// <summary>Connected tables: create, read, change, refresh, history.</summary>
[ApiController]
[Route("connected-tables")]
[RequireAuth]
[ConnectedTablesExceptionFilter]
public sealed class ConnectedTablesController(ConnectedTableHandlers tables, ConnectedRunHandlers runs) : ControllerBase
{
    /// <summary>Create a connected table: the destination table and its fields, then the first refresh is queued.</summary>
    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateConnectedTableRequest r, CancellationToken ct)
    {
        var result = await tables.CreateAsync(new CreateConnectedTableCommand(r.AppId, r.Name, r.SingularLabel, r.PluralLabel, r.Description, r.Icon,
            r.ConnectionId, r.SourceTableId, r.SourceFids, r.RefreshKeyFid, r.UpdateMode, r.FilterEnabled, r.Filter, r.Schedule, r.TimeZone, r.NotifyOnFailure), ct);
        return StatusCode(StatusCodes.Status201Created, new { data = result });
    }

    /// <summary>The connected tables of an app.</summary>
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid appId, CancellationToken ct) => Ok(new { data = await tables.ListByAppAsync(appId, ct) });

    /// <summary>The connected tables and connected fields of an app (for the plug icons).</summary>
    [HttpGet("markers")]
    public async Task<IActionResult> Markers([FromQuery] Guid appId, CancellationToken ct) => Ok(new { data = await tables.MarkersAsync(appId, ct) });

    /// <summary>Whether a table name is already used in the app.</summary>
    [HttpGet("name-check")]
    public async Task<IActionResult> NameCheck([FromQuery] Guid appId, [FromQuery] string name, CancellationToken ct) =>
        Ok(new { data = new { exists = await tables.NameExistsAsync(appId, name, ct) } });

    /// <summary>The connected-table summary of an ordinary table (data is null when the table isn't connected).</summary>
    [HttpGet("by-table/{tableId:guid}")]
    public async Task<IActionResult> ByTable(Guid tableId, CancellationToken ct) => Ok(new { data = await tables.GetByTableAsync(tableId, ct) });

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id, CancellationToken ct) => Ok(new { data = await tables.GetAsync(id, ct) });

    /// <summary>Change the filter (connection owner).</summary>
    [HttpPatch("{id:guid}/filter")]
    public async Task<IActionResult> Filter(Guid id, [FromBody] UpdateFilterRequest r, CancellationToken ct) =>
        Ok(new { data = await tables.UpdateFilterAsync(new UpdateConnectedFilterCommand(id, r.FilterEnabled, r.Filter, r.RowVersion), ct) });

    /// <summary>Connect more source fields (connection owner).</summary>
    [HttpPost("{id:guid}/fields")]
    public async Task<IActionResult> ConnectFields(Guid id, [FromBody] ConnectFieldsRequest r, CancellationToken ct) =>
        Ok(new { data = await tables.ConnectFieldsAsync(new ConnectFieldsCommand(id, r.SourceFids, r.RowVersion), ct) });

    /// <summary>Disconnect a field (connection owner); it is kept as an ordinary field unless deleteField is set.</summary>
    [HttpPost("{id:guid}/fields/disconnect")]
    public async Task<IActionResult> DisconnectField(Guid id, [FromBody] DisconnectFieldRequest r, CancellationToken ct) =>
        Ok(new { data = await tables.DisconnectFieldAsync(new DisconnectFieldCommand(id, r.FieldId, r.DeleteField, r.RowVersion), ct) });

    /// <summary>Update mode, schedule, time zone, failure email, mass-delete guard (owner or app admin).</summary>
    [HttpPatch("{id:guid}/refresh-options")]
    public async Task<IActionResult> RefreshOptions(Guid id, [FromBody] UpdateRefreshOptionsRequest r, CancellationToken ct) =>
        Ok(new { data = await tables.UpdateRefreshOptionsAsync(new UpdateRefreshOptionsCommand(id, r.UpdateMode, r.Schedule, r.TimeZone,
            r.NotifyOnFailure, r.MassDeleteGuardPct, r.ConfirmModeChange, r.RowVersion), ct) });

    /// <summary>Change the refresh key (connection owner).</summary>
    [HttpPatch("{id:guid}/refresh-key")]
    public async Task<IActionResult> RefreshKey(Guid id, [FromBody] ChangeRefreshKeyRequest r, CancellationToken ct) =>
        Ok(new { data = await tables.ChangeRefreshKeyAsync(new ChangeRefreshKeyCommand(id, r.SourceFid, r.Confirm, r.RowVersion), ct) });

    /// <summary>Move the table to one of your connections and become its owner (owner or app admin).</summary>
    [HttpPatch("{id:guid}/connection")]
    public async Task<IActionResult> SwitchConnection(Guid id, [FromBody] SwitchConnectionRequest r, CancellationToken ct) =>
        Ok(new { data = await tables.SwitchConnectionAsync(new SwitchConnectionCommand(id, r.ConnectionId, r.RowVersion), ct) });

    /// <summary>The roles allowed to refresh the table (app admin).</summary>
    [HttpPut("{id:guid}/refresh-permission")]
    public async Task<IActionResult> RefreshPermission(Guid id, [FromBody] RefreshPermissionRequest r, CancellationToken ct) =>
        Ok(new { data = await tables.SetRefreshPermissionAsync(id, r.RoleIds, ct) });

    /// <summary>Turn the table into an ordinary, editable table (app admin). Fields and records stay.</summary>
    [HttpPost("{id:guid}/disconnect")]
    public async Task<IActionResult> Disconnect(Guid id, CancellationToken ct)
    {
        await tables.DisconnectAsync(id, ct);
        return NoContent();
    }

    /// <summary>Queue a refresh now.</summary>
    [HttpPost("{id:guid}/refresh")]
    public async Task<IActionResult> Refresh(Guid id, CancellationToken ct) =>
        Accepted(new { data = await runs.RefreshAsync(id, ConnectedRunTriggers.Manual, ct) });

    /// <summary>Allow a refresh held by the mass-delete guard to remove the records.</summary>
    [HttpPost("{id:guid}/confirm-deletion")]
    public async Task<IActionResult> ConfirmDeletion(Guid id, CancellationToken ct) => Accepted(new { data = await runs.ConfirmDeletionAsync(id, ct) });

    /// <summary>Stop the refresh in progress (records already refreshed stay; nothing is removed).</summary>
    [HttpPost("{id:guid}/cancel")]
    public async Task<IActionResult> Cancel(Guid id, CancellationToken ct)
    {
        await runs.CancelAsync(id, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/runs")]
    public async Task<IActionResult> Runs(Guid id, [FromQuery] int take = 50, CancellationToken ct = default) => Ok(new { data = await runs.ListAsync(id, take, ct) });

    [HttpGet("runs/{runId:guid}")]
    public async Task<IActionResult> Run(Guid runId, CancellationToken ct) => Ok(new { data = await runs.GetAsync(runId, ct) });

    [HttpGet("runs/{runId:guid}/issues")]
    public async Task<IActionResult> Issues(Guid runId, CancellationToken ct) => Ok(new { data = await runs.ListIssuesAsync(runId, ct) });
}
