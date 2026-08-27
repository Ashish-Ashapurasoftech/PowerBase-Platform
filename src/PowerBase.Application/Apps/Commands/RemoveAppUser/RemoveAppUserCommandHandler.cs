using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.RemoveAppUser;

public class RemoveAppUserCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public RemoveAppUserCommandHandler(
        IAppRepository appRepo,
        IAppUserRepository appUserRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _appRepo = appRepo;
        _appUserRepo = appUserRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(RemoveAppUserCommand command, CancellationToken ct = default)
    {
        var app = await _appRepo.GetByPublicIdAsync(command.AppPublicId, ct);
        var appId = app.Id;

        // Resolve by AppUser assignment PublicId.
        // Multi-role: each AppUser row = one specific role assignment (GUID-ROW-A, GUID-ROW-B...).
        // Callers MUST pass AppUser.PublicId (not User.PublicId) to remove a specific role.
        // This prevents accidentally deleting ALL role assignments for a multi-role user.
        var appUser = await _appUserRepo.GetByPublicIdAsync(command.UserPublicId, ct);
        if (appUser == null || appUser.AppId != appId)
            throw new NotFoundException("AppUser", command.UserPublicId);

        if (app.OwnerId == appUser.UserId)
            throw new UnauthorizedActionException("Cannot remove the app owner.");

        if (appUser.UserId == _queryContext.UserId)
            throw new UnauthorizedActionException("Cannot remove yourself from the app.");

        await _appUserRepo.RemoveAssignmentAsync(appId, appUser.PublicId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Deleted, AuditEntityTypes.AppUser, appUser.UserId.ToString(),
            $"User role assignment removed from app: {appUser.UserEmail}", appId: appId, ct: ct);
    }
}
