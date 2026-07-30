using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.Application.Pages.Commands.PublishPage;

public class PublishPageCommandHandler
{
    private readonly IPageRepository _pageRepo;
    private readonly IAuditRepository _auditRepo;

    public PublishPageCommandHandler(IPageRepository pageRepo, IAuditRepository auditRepo)
    {
        _pageRepo = pageRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(PublishPageCommand command, CancellationToken ct = default)
    {
        var page = await _pageRepo.GetByPublicIdAsync(command.PagePublicId, ct);
        var publishedVersionNo = command.IsPublished ? page.CurrentVersionNo : page.PublishedVersionNo;

        await _pageRepo.SetPublishedAsync(command.PagePublicId, command.IsPublished, publishedVersionNo, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Published, AuditEntityTypes.Page, command.PagePublicId.ToString(),
            $"Page {(command.IsPublished ? "published" : "unpublished")}: {page.Name}",
            appId: page.AppId, ct: ct);
    }
}
