namespace PowerBase.Domain.Entities;

public class Page
{
    public long Id { get; set; }
    public Guid PublicId { get; set; }
    public long AppId { get; set; }
    public int PageNumber { get; set; }
    public string PageType { get; set; } = "Dashboard";
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public long OwnerId { get; set; }
    public string Visibility { get; set; } = "Personal";
    public string Definition { get; set; } = "{}";
    public string? ContentType { get; set; }
    public string? CodeHtml { get; set; }
    public string? CodeCss { get; set; }
    public string? CodeJs { get; set; }
    public bool IsPublished { get; set; }
    public int CurrentVersionNo { get; set; } = 1;
    public int? PublishedVersionNo { get; set; }
    public bool ShowInNav { get; set; }
    public int NavOrder { get; set; }
    public string? NavIcon { get; set; }
    public bool IsDefaultHome { get; set; }
    public bool IsDeleted { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
    public DateTime? ModifiedOn { get; set; }
    public long? ModifiedBy { get; set; }
    public DateTime? DeletedOn { get; set; }
    public long? DeletedBy { get; set; }
    public byte[] RowVersion { get; set; } = Array.Empty<byte>();
}

public static class PageTypes
{
    public const string Dashboard = "Dashboard";
    public const string Code = "Code";
    public static readonly string[] All = [Dashboard, Code];
}
