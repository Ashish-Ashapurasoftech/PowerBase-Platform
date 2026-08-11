namespace PowerBase.Domain.Entities;

public class AppToken
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long TenantId { get; set; }
    public long AppId { get; set; }
    public long CreatedByUserId { get; set; }
    public string TokenName { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public string TokenPrefix { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? LastUsedAt { get; set; }
    public bool IsDeleted { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}
