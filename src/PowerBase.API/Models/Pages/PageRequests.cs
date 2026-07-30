namespace PowerBase.API.Models.Pages;

public class CreatePageRequest
{
    public string PageType { get; set; } = "Dashboard";
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Visibility { get; set; } = "Personal";
    public List<Guid>? VisibleToRoleIds { get; set; }
    public string? Definition { get; set; }
    public string? ContentType { get; set; }
    public string? CodeHtml { get; set; }
    public string? CodeCss { get; set; }
    public string? CodeJs { get; set; }
    public bool ShowInNav { get; set; }
    public int NavOrder { get; set; }
    public string? NavIcon { get; set; }
}

public class UpdatePageRequest
{
    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }
    public string Visibility { get; set; } = "Personal";
    public List<Guid>? VisibleToRoleIds { get; set; }
    public string? Definition { get; set; }
    public string? ContentType { get; set; }
    public string? CodeHtml { get; set; }
    public string? CodeCss { get; set; }
    public string? CodeJs { get; set; }
    public bool ShowInNav { get; set; }
    public int NavOrder { get; set; }
    public string? NavIcon { get; set; }
    public string ChangeNotes { get; set; } = string.Empty;
}

public class DuplicatePageRequest
{
    public string? NewName { get; set; }
}

public class PublishPageRequest
{
    public bool IsPublished { get; set; }
}

public class RestorePageVersionRequest
{
    public string ChangeNotes { get; set; } = string.Empty;
}

public class BulkDeletePagesRequest
{
    public List<Guid> PublicIds { get; set; } = [];
}

public class SetDefaultHomeRequest
{
    /// <summary>Null clears the app's default home page.</summary>
    public Guid? PagePublicId { get; set; }
}
