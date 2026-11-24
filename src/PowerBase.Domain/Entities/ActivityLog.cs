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
    /// <summary>The app table this row belongs to (records, fields, tables, forms, form rules, reports); NULL for app-level rows and rows written before migration 100.</summary>
    public long? TableId { get; set; }
    /// <summary>Record ID# of the record a "Record" row is about; NULL for other rows, bulk summaries and rows written before migration 100.</summary>
    public long? RecordNumber { get; set; }
    public string? OldValues { get; set; }
    public string? NewValues { get; set; }
    public string? IpAddress { get; set; }
    public string? UserAgent { get; set; }
    public DateTime OccurredOn { get; set; }
}
