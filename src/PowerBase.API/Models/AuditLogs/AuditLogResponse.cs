namespace PowerBase.API.Models.AuditLogs;

public class AuditLogResponse
{
    public long Id { get; set; }
    public string? Email { get; set; }
    public string? FirstName { get; set; }
    public string? LastName { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? Value { get; set; }
    public string? IpAddress { get; set; }
    public DateTime OccurredOn { get; set; }
}
