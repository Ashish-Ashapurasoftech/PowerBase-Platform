using PowerBase.Domain.Enums;

namespace PowerBase.Domain.Entities;

public class Report
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long TenantId { get; set; }
    public long AppTableId { get; set; }
    public long OwnerId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public ReportType ReportType { get; set; }
    public Visibility Visibility { get; set; }
    public string DefinitionJson { get; set; } = "{}";
    public bool IsDeleted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
