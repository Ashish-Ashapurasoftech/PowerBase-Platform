namespace PowerBase.Application.AppLogs;

/// <summary>One row of the application log (logs database, applog.AppLog): a change made in an app.</summary>
public sealed class AppLogEntry
{
    public long Id { get; set; }
    public long? TenantId { get; set; }
    public long? AppId { get; set; }
    public long? TableId { get; set; }
    public long? RecordNumber { get; set; }
    public long? UserId { get; set; }
    public string? UserName { get; set; }
    public string? UserEmail { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? Description { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? HttpMethod { get; set; }
    public string? Path { get; set; }
    public int? StatusCode { get; set; }
    public int? DurationMs { get; set; }
    public string? RequestBody { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime OccurredOn { get; set; }
    /// <summary>Not a column: the list query says whether old / new values or a request body exist ("View details").</summary>
    public bool HasDetails { get; set; }
}

/// <summary>A change a handler reported during the current request (or background job), through the audit calls it already makes.</summary>
public sealed record AppLogEvent(
    string Action,
    string EntityType,
    string? EntityId,
    string? Description,
    long? AppId,
    long? TableId,
    long? RecordNumber,
    string? OldValues,
    string? NewValues);

public sealed record AppLogFilter(
    long TenantId,
    long? AppId,
    DateTime? From,
    DateTime? To,
    string? Email,
    string? EntityType,
    string? Action,
    long? TableId,
    long? RecordNumber,
    int Page = 1,
    int PageSize = 50);
