namespace PowerBase.Application.Pages;

public class PageListItemDto
{
    public Guid Id { get; init; }
    public int PageNumber { get; init; }
    public string PageType { get; init; } = "Dashboard";
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Visibility { get; init; } = "Personal";
    public bool IsPublished { get; init; }
    public bool ShowInNav { get; init; }
    public int NavOrder { get; init; }
    public bool IsDefaultHome { get; init; }
    public IReadOnlyList<string> HomePageForRoles { get; init; } = [];
    public DateTime CreatedOn { get; init; }
    public DateTime? ModifiedOn { get; init; }
}

public class PageDetailDto
{
    public Guid Id { get; init; }
    public int PageNumber { get; init; }
    public string PageType { get; init; } = "Dashboard";
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Visibility { get; init; } = "Personal";
    public IReadOnlyList<Guid> VisibleToRoleIds { get; init; } = [];
    public string Definition { get; init; } = "{}";
    public string? ContentType { get; init; }
    public string? CodeHtml { get; init; }
    public string? CodeCss { get; init; }
    public string? CodeJs { get; init; }
    public bool IsPublished { get; init; }
    public int CurrentVersionNo { get; init; }
    public int? PublishedVersionNo { get; init; }
    public bool ShowInNav { get; init; }
    public int NavOrder { get; init; }
    public string? NavIcon { get; init; }
    public bool IsDefaultHome { get; init; }
    public DateTime CreatedOn { get; init; }
    public DateTime? ModifiedOn { get; init; }
}

public class PageVersionDto
{
    public Guid Id { get; init; }
    public int VersionNo { get; init; }
    public string PageType { get; init; } = "Dashboard";
    public string Name { get; init; } = string.Empty;
    public string? Description { get; init; }
    public string Definition { get; init; } = "{}";
    public string? CodeHtml { get; init; }
    public string? CodeCss { get; init; }
    public string? CodeJs { get; init; }
    public string ChangeNotes { get; init; } = string.Empty;
    public bool WasPublished { get; init; }
    public DateTime CreatedOn { get; init; }
    public long CreatedBy { get; init; }
}
