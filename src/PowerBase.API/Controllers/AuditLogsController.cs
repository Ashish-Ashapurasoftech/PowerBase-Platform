using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.AuditLogs;
using PowerBase.Application.AuditLogs.Queries.ExportAuditLogsCsv;
using PowerBase.Application.AuditLogs.Queries.ListAuditLogs;
using PowerBase.Domain.Constants;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("audit-logs")]
public class AuditLogsController : ControllerBase
{
    private readonly ListAuditLogsQueryHandler _listHandler;
    private readonly ExportAuditLogsCsvQueryHandler _exportHandler;

    public AuditLogsController(
        ListAuditLogsQueryHandler listHandler,
        ExportAuditLogsCsvQueryHandler exportHandler)
    {
        _listHandler = listHandler;
        _exportHandler = exportHandler;
    }

    /// <summary>
    /// Query audit activity logs for the current tenant.
    /// Optionally filter by app, date range, user email, entity type, or action.
    /// </summary>
    [HttpGet]
    [RequirePermission(PermissionCodes.AuditLogsRead)]
    [ProducesResponseType(typeof(ApiListResponse<AuditLogResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        [FromQuery] Guid? appId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? email,
        [FromQuery] string? entityType,
        [FromQuery] string? action,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var query = new ListAuditLogsQuery(
            AppPublicId: appId,
            From: from,
            To: to,
            Email: email,
            EntityType: entityType,
            Action: action,
            Page: page,
            PageSize: pageSize);

        var result = await _listHandler.HandleAsync(query, ct);
        var items = result.Items.Select(Map).ToList();
        return Ok(new ApiListResponse<AuditLogResponse>(items, result.Total, result.Page, result.PageSize));
    }

    /// <summary>
    /// Export audit activity logs as CSV for the current tenant.
    /// Supports the same filters as the list endpoint. Max 90 days of data per export.
    /// </summary>
    [HttpGet("export")]
    [RequirePermission(PermissionCodes.AuditLogsRead)]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Export(
        [FromQuery] Guid? appId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? email,
        [FromQuery] string? entityType,
        [FromQuery] string? action,
        CancellationToken ct = default)
    {
        var query = new ExportAuditLogsCsvQuery(
            AppPublicId: appId,
            From: from,
            To: to,
            Email: email,
            EntityType: entityType,
            Action: action);

        var bytes = await _exportHandler.HandleAsync(query, ct);
        var filename = $"audit-logs-{DateTime.UtcNow:yyyyMMdd-HHmm}.csv";
        return File(bytes, "text/csv", filename);
    }

    private static AuditLogResponse Map(Domain.Entities.ActivityLog log) => new()
    {
        Id = log.Id,
        UserEmail = log.UserEmail,
        UserName = log.UserName,
        Action = log.Action,
        EntityType = log.EntityType,
        EntityId = log.EntityId,
        IpAddress = log.IpAddress,
        OccurredOn = log.OccurredOn,
    };
}
