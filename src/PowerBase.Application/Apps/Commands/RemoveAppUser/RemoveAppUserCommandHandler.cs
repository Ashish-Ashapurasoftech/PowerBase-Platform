using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.RemoveAppUser;

public class RemoveAppUserCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IUserRepository _userRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public RemoveAppUserCommandHandler(
        IAppRepository appRepo,
        IAppUserRepository appUserRepo,
        IUserRepository userRepo,
        IAppAccessService appAccessService,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _appRepo = appRepo;
        _appUserRepo = appUserRepo;
        _userRepo = userRepo;
        _appAccessService = appAccessService;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(RemoveAppUserCommand command, CancellationToken ct = default)
    {
        var app = await _appRepo.GetByPublicIdAsync(command.AppPublicId, ct);
        var appId = app.Id;

        var user = await _userRepo.GetByPublicIdAsync(command.UserPublicId, ct)
            ?? throw new NotFoundException("User", command.UserPublicId);

        if (app.OwnerId == user.Id)
        {
            throw new UnauthorizedActionException("Cannot remove the app owner.");
        }

        if (user.Id == _queryContext.UserId)
            throw new UnauthorizedActionException("Cannot remove yourself from the app.");

        var appUser = await _appUserRepo.GetByAppAndUserAsync(appId, user.Id, ct)
            ?? throw new NotFoundException("AppUser", command.UserPublicId);

        await _appUserRepo.RemoveAsync(appId, user.Id, ct);
        
        await _auditRepo.LogActivityAsync(
            AuditActions.Deleted, AuditEntityTypes.AppUser, user.Id.ToString(), $"User removed from app: {user.Email}", appId: appId, ct: ct);
    }
}
