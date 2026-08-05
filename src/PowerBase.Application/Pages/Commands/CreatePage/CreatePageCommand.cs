namespace PowerBase.Application.Pages.Commands.CreatePage;

public record CreatePageCommand(
    Guid AppPublicId,
    string PageType,
    string Name,
    string? Description,
    string Visibility,
    IReadOnlyList<Guid>? VisibleToRoleIds,
    string? Definition,
    string? ContentType,
    string? CodeHtml,
    string? CodeCss,
    string? CodeJs,
    bool ShowInNav,
    int NavOrder,
    string? NavIcon);
