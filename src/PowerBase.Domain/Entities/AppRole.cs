namespace PowerBase.Domain.Entities;

public class AppRole
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long AppId { get; set; }
    public long TenantId { get; set; }
    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsSystem { get; set; }
    public bool CanViewRecords { get; set; } = true;
    public bool CanAddRecords { get; set; } = true;
    public bool CanEditRecords { get; set; } = true;
    public bool CanDeleteRecords { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public bool IsDeleted { get; set; }
}
