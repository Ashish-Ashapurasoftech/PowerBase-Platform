using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.Application.AppLogs;

namespace PowerBase.API.Controllers;

/// <summary>
/// The application log of an app (App Settings > Application Audit), read from the logs database (applog.AppLog).
/// It holds every change made in the app — written by AppLogMiddleware — and is separate from /audit-logs, which reads
/// the tenant's audit.ActivityLog for the record page Stream.
/// </summary>
[ApiController]
[Route("app-logs")]
[RequireAuth]
public class AppLogsController : ControllerBase
{
    private readonly AppLogQueryHandler _handler;

    public AppLogsController(AppLogQueryHandler handler) => _handler = handler;

    [HttpGet]
    [ProducesResponseType(typeof(ApiListResponse<AppLogResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(
        [FromQuery] Guid appId,
        [FromQuery] DateTime? from,
        [FromQuery] DateTime? to,
        [FromQuery] string? email,
        [FromQuery] string? entityType,
        [FromQuery] string? action,
        [FromQuery] Guid? tableId,
        [FromQuery] long? recordNumber,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        if (from.HasValue && to.HasValue && (to.Value.Date - from.Value.Date).TotalDays > 15)
            return BadRequest("Date range cannot exceed 15 days.");

        var (items, total, currentPage, size) = await _handler.ListAsync(new ListAppLogsQuery(
            appId, from, to?.Date.AddDays(1).AddTicks(-1), email, entityType, action, tableId, recordNumber, page, pageSize), ct);
        return Ok(new ApiListResponse<AppLogResponse>(items.Select(e => AppLogResponse.From(e, withDetails: false)).ToList(), total, currentPage, size));
    }

    /// <summary>One row with its details: the old / new field values ("View details"). The request body stays in the log, not shown.</summary>
    [HttpGet("{id:long}")]
    [ProducesResponseType(typeof(ApiResponse<AppLogResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(long id, CancellationToken ct = default) =>
        Ok(new ApiResponse<AppLogResponse>(AppLogResponse.From(await _handler.GetAsync(id, ct), withDetails: true)));
}

public sealed class AppLogResponse
{
    public long Id { get; init; }
    public string? Name { get; init; }
    public string? Email { get; init; }
    public string Action { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public string? EntityId { get; init; }
    public string? Description { get; init; }
    public long? RecordNumber { get; init; }
    public string? HttpMethod { get; init; }
    public string? Path { get; init; }
    public int? StatusCode { get; init; }
    public DateTime OccurredOn { get; init; }
    public bool HasDetails { get; init; }
    public string? OldValues { get; init; }
    public string? NewValues { get; init; }

    public static AppLogResponse From(AppLogEntry e, bool withDetails) => new()
    {
        Id = e.Id,
        Name = e.UserName,
        Email = e.UserEmail,
        Action = e.Action,
        EntityType = e.EntityType,
        EntityId = e.EntityId,
        Description = e.Description,
        RecordNumber = e.RecordNumber,
        HttpMethod = e.HttpMethod,
        Path = e.Path,
        StatusCode = e.StatusCode,
        OccurredOn = DateTime.SpecifyKind(e.OccurredOn, DateTimeKind.Utc),
        HasDetails = e.HasDetails,
        OldValues = withDetails ? e.OldValues : null,
        NewValues = withDetails ? e.NewValues : null,
    };
}
