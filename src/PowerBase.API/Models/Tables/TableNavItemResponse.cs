namespace PowerBase.API.Models.Tables;

/// <summary>Slim shape for the dedicated nav-list endpoint (GET /apps/{appId}/tables/nav) — just
/// enough to render a sidebar link, top-nav pill, or table-switcher row. No RecordCount/FieldCount/
/// CreatedOn (see <see cref="TableListItemResponse"/> for the paged "manage tables" listing, or
/// GET /tables/{publicId} for full table details).</summary>
public class TableNavItemResponse
{
    public Guid PublicId { get; init; }
    public string Name { get; init; } = string.Empty;
    public string? SingularLabel { get; init; }
    public string? Icon { get; init; }
    public bool IsShowInBar { get; init; }
}
