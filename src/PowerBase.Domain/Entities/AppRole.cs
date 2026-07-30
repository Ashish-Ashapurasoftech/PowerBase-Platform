namespace PowerBase.Domain.Entities;

public class AppRole
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long AppId { get; set; }

    public string Name { get; set; } = string.Empty;
    public bool IsDefault { get; set; }
    public bool IsSystem { get; set; }
    /// <summary>The Page (if any) this role lands on instead of the app's built-in dashboard.</summary>
    public long? HomePageId { get; set; }

    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public bool IsDeleted { get; set; }
}
