namespace PowerBase.Domain.Entities;

public class ActivityLog
{
    public long Id { get; set; }
    public long TenantId { get; set; }
    public long? UserId { get; set; }
    public string? UserEmail { get; set; }
    public string? UserName { get; set; }
    public string Action { get; set; } = string.Empty;
    public string EntityType { get; set; } = string.Empty;
    public string? EntityId { get; set; }
    public string? EntityTitle { get; set; }
    public long? AppId { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime OccurredOn { get; set; }
}
