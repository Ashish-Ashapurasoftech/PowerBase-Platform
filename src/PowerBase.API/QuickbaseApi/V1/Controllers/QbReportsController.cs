using Microsoft.AspNetCore.Mvc;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.V1.Models;
using PowerBase.API.QuickbaseApi.V1.Services;

namespace PowerBase.API.QuickbaseApi.V1.Controllers;

/// <summary>Quickbase-compatible "Reports" API, version 1.</summary>
[Route(QbV1.ROUTE_PREFIX + "/reports")]
[QbRestExceptionFilter, QbRequireAuth, QbRealmHostname]
public sealed class QbReportsController : ControllerBase
{
    private readonly QbReportService _reports;
    private readonly QbIdResolver _ids;
    public QbReportsController(QbReportService reports, QbIdResolver ids)
    {
        _reports = reports;
        _ids = ids;
    }

    /// <summary>Get a report (Quickbase getReport): GET /v1/reports/{reportId}?tableId={tableId}.</summary>
    [HttpGet("{reportId}")]
    [ProducesResponseType(typeof(QbReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetReport(string reportId, [FromQuery] string? tableId, CancellationToken ct)
    {
        var table = await _ids.TableAsync(tableId, "tableId", ct);
        var report = await _ids.ReportAsync(reportId, table, "reportId", ct);
        return Ok(await _reports.GetReportAsync(report, table, ct));
    }

    /// <summary>Run a report (Quickbase runReport): POST /v1/reports/{reportId}/run?tableId={tableId}&amp;skip={skip}&amp;top={top}.</summary>
    [HttpPost("{reportId}/run")]
    [ProducesResponseType(typeof(QbRunReportResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(QbErrorBody), StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RunReport(string reportId, [FromQuery] string? tableId, [FromQuery] string? skip,
        [FromQuery] string? top, CancellationToken ct)
    {
        var table = await _ids.TableAsync(tableId, "tableId", ct);
        var report = await _ids.ReportAsync(reportId, table, "reportId", ct);
        var skipValue = QbQueryParams.ParseNonNegativeInt(skip, "skip");
        var topValue = QbQueryParams.ParseNonNegativeInt(top, "top");
        if (topValue is <= 0)
            throw QbApiException.InvalidRequest("Report parameter 'top' must be greater than zero.");
        return Ok(await _reports.RunReportAsync(report, table, skipValue, topValue, ct));
    }
}
