using Microsoft.AspNetCore.Mvc;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Models;
using PowerBase.API.QuickbaseApi.V1.Services;

namespace PowerBase.API.QuickbaseApi.V1.Controllers;

/// <summary>Quickbase-compatible "Fields" API, version 1.</summary>
[Route(QbV1.ROUTE_PREFIX + "/fields")]
[QbRestExceptionFilter, QbRequireAuth, QbRealmHostname]
public sealed class QbFieldsController : ControllerBase
{
    private readonly QbSchemaService _schema;
    private readonly QbIdResolver _ids;
    public QbFieldsController(QbSchemaService schema, QbIdResolver ids)
    {
        _schema = schema;
        _ids = ids;
    }

    /// <summary>Get fields for a table (Quickbase getFields): GET /v1/fields?tableId={tableId}&amp;includeFieldPerms={bool}.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(IReadOnlyList<QbFieldResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetFields([FromQuery] string? tableId, [FromQuery] string? includeFieldPerms, CancellationToken ct)
    {
        var id = await _ids.TableAsync(tableId, "tableId", ct);
        var withPerms = QbQueryParams.ParseBool(includeFieldPerms, "includeFieldPerms") ?? false;
        return Ok(await _schema.GetFieldsAsync(id, withPerms, ct));
    }
}
