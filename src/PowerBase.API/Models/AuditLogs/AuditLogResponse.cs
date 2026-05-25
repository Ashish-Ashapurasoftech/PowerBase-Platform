namespace PowerBase.API.Models.AuditLogs;

public class AuditLogResponse
{
    public long Id { get; init; }
    public string? UserEmail { get; init; }
    public string? UserName { get; init; }
    public string Action { get; init; } = string.Empty;
    public string EntityType { get; init; } = string.Empty;
    public string? EntityId { get; init; }
    public string? IpAddress { get; init; }
    public DateTime OccurredOn { get; init; }
}
