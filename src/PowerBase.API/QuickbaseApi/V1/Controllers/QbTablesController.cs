using Microsoft.AspNetCore.Mvc;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Models;
using PowerBase.API.QuickbaseApi.V1.Services;

namespace PowerBase.API.QuickbaseApi.V1.Controllers;

/// <summary>Quickbase-compatible "Tables" API, version 1.</summary>
[Route(QbV1.ROUTE_PREFIX + "/tables")]
[QbRestExceptionFilter, QbRequireAuth, QbRealmHostname]
public sealed class QbTablesController : ControllerBase
{
    private readonly QbSchemaService _schema;
    private readonly QbIdResolver _ids;
    public QbTablesController(QbSchemaService schema, QbIdResolver ids)
    {
        _schema = schema;
        _ids = ids;
    }

    /// <summary>Get tables for an app (Quickbase getAppTables): GET /v1/tables?appId={appId}. appId: app Guid or numeric id.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<QbTableResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAppTables([FromQuery] string? appId, CancellationToken ct)
    {
        var id = await _ids.AppAsync(appId, "appId", ct);
        return Ok(await _schema.GetAppTablesAsync(id, ct));
    }
}
