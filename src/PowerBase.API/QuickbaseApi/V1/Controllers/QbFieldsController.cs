using Microsoft.AspNetCore.Mvc;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Models;
using PowerBase.API.QuickbaseApi.V1.Services;

namespace PowerBase.API.QuickbaseApi.V1.Controllers;

/// <summary>Quickbase-compatible "Fields" API, version 1.</summary>
[Route(QbV1.ROUTE_PREFIX + "/fields")]
[QbRestExceptionFilter, QbRequireAuth, QbRealmHostname, QbSortedJson]
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
        Guid id;
        try { id = await _ids.TableAsync(tableId, "tableId", ct); }
        catch (QbApiException) when (QbIdResolver.Classify(tableId, out _, out _) == QbIdResolver.IdKind.QuickbaseId)
        {
            // Live realm: getFields for a dbid the caller has no table for answers 401 "User token is invalid".
            throw new QbApiException(401, "Access denied", "User token is invalid");
        }
        var withPerms = QbQueryParams.ParseBool(includeFieldPerms, "includeFieldPerms") ?? false;
        return Ok(await _schema.GetFieldsAsync(id, withPerms, ct));
    }

    /// <summary>Get a field (Quickbase getField): GET /v1/fields/{fieldId}?tableId={tableId}&amp;includeFieldPerms={bool}.</summary>
    [HttpGet("{fieldId}")]
    [ProducesResponseType(typeof(QbFieldResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetField(string fieldId, [FromQuery] string? tableId, [FromQuery] string? includeFieldPerms, CancellationToken ct)
    {
        var id = await _ids.TableAsync(tableId, "tableId", ct);
        if (!long.TryParse(fieldId, out var fid)) throw QbApiException.BadRequest("'fieldId' must be integer");
        var withPerms = QbQueryParams.ParseBool(includeFieldPerms, "includeFieldPerms") ?? false;
        var field = (await _schema.GetFieldsAsync(id, withPerms, ct)).FirstOrDefault(f => f.Id == fid)
            ?? throw new QbApiException(404, "No such field", $"Field with fid {fid} not found");
        return Ok(field);
    }
}
