using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pages.Commands.DeletePages;

public class DeletePagesCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IPageRepository _pageRepo;
    private readonly IAuditRepository _auditRepo;

    public DeletePagesCommandHandler(IAppRepository appRepo, IPageRepository pageRepo, IAuditRepository auditRepo)
    {
        _appRepo = appRepo;
        _pageRepo = pageRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(DeletePagesCommand command, CancellationToken ct = default)
    {
        if (command.PagePublicIds.Count == 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["pagePublicIds"] = ["At least one page id is required."] });

        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        // Verify each page actually belongs to this app before deleting — defense in depth
        // against a bulk-delete request smuggling in a page id from a different app.
        var names = new List<string>();
        foreach (var pagePublicId in command.PagePublicIds)
        {
            var page = await _pageRepo.GetByPublicIdAsync(pagePublicId, ct);
            if (page.AppId != appId)
                throw new UnauthorizedActionException("One or more pages do not belong to this app.");
            names.Add(page.Name);
        }

        await _pageRepo.SoftDeleteManyAsync(command.PagePublicIds, ct);

        foreach (var (pagePublicId, name) in command.PagePublicIds.Zip(names))
        {
            await _auditRepo.LogActivityAsync(
                AuditActions.SchemaChanged, AuditEntityTypes.Page, pagePublicId.ToString(),
                $"Page deleted: {name}", appId: appId, ct: ct);
        }
    }
}
