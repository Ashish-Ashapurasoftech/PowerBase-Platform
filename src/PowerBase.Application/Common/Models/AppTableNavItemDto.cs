namespace PowerBase.Application.Common.Models;

/// <summary>Slim projection of <see cref="PowerBase.Domain.Entities.AppTable"/> for nav surfaces
/// (sidebar, top nav, table switcher) — just enough to render a table link/pill. Unlike
/// <see cref="AppTableListItemDto"/> (the paged "manage tables" listing), this always covers every
/// table in the app in one shot: no paging, no search/sort — nav surfaces filter (isShowInBar) and
/// search (by name) client-side over the full set.</summary>
public class AppTableNavItemDto
{
    public Guid PublicId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string? SingularLabel { get; set; }
    public string? Icon { get; set; }
    public bool IsShowInBar { get; set; }
}
