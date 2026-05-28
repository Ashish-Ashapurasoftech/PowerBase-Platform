using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.RemoveAppUser;

public class RemoveAppUserCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IUserRepository _userRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IQueryContext _queryContext;

    public RemoveAppUserCommandHandler(
        IAppRepository appRepo,
        IAppUserRepository appUserRepo,
        IUserRepository userRepo,
        IAppAccessService appAccessService,
        IQueryContext queryContext)
    {
        _appRepo = appRepo;
        _appUserRepo = appUserRepo;
        _userRepo = userRepo;
        _appAccessService = appAccessService;
        _queryContext = queryContext;
    }

    public async Task HandleAsync(RemoveAppUserCommand command, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        var user = await _userRepo.GetByPublicIdAsync(command.UserPublicId, ct)
            ?? throw new NotFoundException("User", command.UserPublicId);

        if (user.Id == _queryContext.UserId)
            throw new UnauthorizedActionException("Cannot remove yourself from the app.");

        var appUser = await _appUserRepo.GetByAppAndUserAsync(appId, user.Id, ct)
            ?? throw new NotFoundException("AppUser", command.UserPublicId);

        await _appUserRepo.RemoveAsync(appId, user.Id, ct);
    }
}
