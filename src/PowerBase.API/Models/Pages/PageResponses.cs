namespace PowerBase.API.Models.Pages;

public class PageListItemResponse
{
    public Guid Id { get; set; }
    public int PageNumber { get; set; }
    public string PageType { get; set; } = "Dashboard";
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Visibility { get; set; } = "Personal";
    public bool IsPublished { get; set; }
    public bool ShowInNav { get; set; }
    public int NavOrder { get; set; }
    public bool IsDefaultHome { get; set; }
    public IReadOnlyList<string> HomePageForRoles { get; set; } = [];
    public DateTime CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
}

public class PageDetailResponse
{
    public Guid Id { get; set; }
    public int PageNumber { get; set; }
    public string PageType { get; set; } = "Dashboard";
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Visibility { get; set; } = "Personal";
    public IReadOnlyList<Guid> VisibleToRoleIds { get; set; } = [];
    public string Definition { get; set; } = "{}";
    public string? ContentType { get; set; }
    public string? CodeHtml { get; set; }
    public string? CodeCss { get; set; }
    public string? CodeJs { get; set; }
    public bool IsPublished { get; set; }
    public int CurrentVersionNo { get; set; }
    public int? PublishedVersionNo { get; set; }
    public bool ShowInNav { get; set; }
    public int NavOrder { get; set; }
    public string? NavIcon { get; set; }
    public bool IsDefaultHome { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
}

public class PageVersionResponse
{
    public Guid Id { get; set; }
    public int VersionNo { get; set; }
    public string PageType { get; set; } = "Dashboard";
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Definition { get; set; } = "{}";
    public string? CodeHtml { get; set; }
    public string? CodeCss { get; set; }
    public string? CodeJs { get; set; }
    public string ChangeNotes { get; set; } = string.Empty;
    public bool WasPublished { get; set; }
    public DateTime CreatedOn { get; set; }
    public long CreatedBy { get; set; }
}
