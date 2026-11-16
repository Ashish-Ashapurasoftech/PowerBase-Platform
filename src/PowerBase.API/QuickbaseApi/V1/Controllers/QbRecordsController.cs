using Microsoft.AspNetCore.Mvc;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Models;
using PowerBase.API.QuickbaseApi.V1.Services;

namespace PowerBase.API.QuickbaseApi.V1.Controllers;

/// <summary>Quickbase-compatible "Records" API, version 1.</summary>
[Route(QbV1.ROUTE_PREFIX + "/records")]
[QbRestExceptionFilter, QbRequireAuth, QbRealmHostname]
public sealed class QbRecordsController : ControllerBase
{
    private readonly QbRecordService _records;
    public QbRecordsController(QbRecordService records) => _records = records;

    /// <summary>Insert/Update record(s) (Quickbase upsert): POST /v1/records. 207 when some rows failed (see metadata.lineErrors).</summary>
    [HttpPost]
    [ProducesResponseType(typeof(QbUpsertResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(QbUpsertResponse), StatusCodes.Status207MultiStatus)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Upsert([FromBody] QbUpsertRequest? request, CancellationToken ct)
    {
        if (request is null) throw QbApiException.BadRequest("Request body is required");
        var outcome = await _records.UpsertAsync(request, ct);
        return StatusCode(outcome.HasLineErrors ? StatusCodes.Status207MultiStatus : StatusCodes.Status200OK, outcome.Response);
    }

    /// <summary>Delete record(s) (Quickbase deleteRecords): DELETE /v1/records with body { from, where }.</summary>
    [HttpDelete]
    [ProducesResponseType(typeof(QbDeleteRecordsResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete([FromBody] QbDeleteRecordsRequest? request, CancellationToken ct)
    {
        if (request is null) throw QbApiException.BadRequest("Request body is required");
        return Ok(await _records.DeleteAsync(request, ct));
    }
}
