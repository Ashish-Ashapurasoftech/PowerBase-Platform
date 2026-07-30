namespace PowerBase.Application.Pages.Commands.UpdatePage;

public record UpdatePageCommand(
    Guid PagePublicId,
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
    string? NavIcon,
    string ChangeNotes);
