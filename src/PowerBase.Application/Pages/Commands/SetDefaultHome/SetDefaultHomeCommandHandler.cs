using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pages.Commands.SetDefaultHome;

public class SetDefaultHomeCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IPageRepository _pageRepo;
    private readonly IAuditRepository _auditRepo;

    public SetDefaultHomeCommandHandler(IAppRepository appRepo, IPageRepository pageRepo, IAuditRepository auditRepo)
    {
        _appRepo = appRepo;
        _pageRepo = pageRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(SetDefaultHomeCommand command, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        if (command.PagePublicId.HasValue)
        {
            var page = await _pageRepo.GetByPublicIdAsync(command.PagePublicId.Value, ct);
            if (page.AppId != appId)
                throw new NotFoundException("Page", command.PagePublicId.Value);
            if (page.PageType != PageTypes.Dashboard)
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["pagePublicId"] = ["Only a Dashboard page can be the app's home page."]
                });
        }

        await _pageRepo.SetDefaultHomeAsync(appId, command.PagePublicId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.SchemaChanged, AuditEntityTypes.Page, command.PagePublicId?.ToString() ?? "none",
            command.PagePublicId.HasValue ? "Set as app default home page" : "Cleared app default home page",
            appId: appId, ct: ct);
    }
}
