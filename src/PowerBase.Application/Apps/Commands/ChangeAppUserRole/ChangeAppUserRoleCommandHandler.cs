using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.ChangeAppUserRole;

public class ChangeAppUserRoleCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IUserRepository _userRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public ChangeAppUserRoleCommandHandler(
        IAppRepository appRepo,
        IAppRoleRepository appRoleRepo,
        IAppUserRepository appUserRepo,
        IUserRepository userRepo,
        IAppAccessService appAccessService,
        IQueryContext queryContext,
        IAuditRepository auditRepo)
    {
        _appRepo = appRepo;
        _appRoleRepo = appRoleRepo;
        _appUserRepo = appUserRepo;
        _userRepo = userRepo;
        _appAccessService = appAccessService;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(ChangeAppUserRoleCommand command, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        var user = await _userRepo.GetByPublicIdAsync(command.UserPublicId, ct)
            ?? throw new NotFoundException("User", command.UserPublicId);

        var appUser = await _appUserRepo.GetByAppAndUserAsync(appId, user.Id, ct)
            ?? throw new NotFoundException("AppUser", command.UserPublicId);

        var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId, ct)
            ?? throw new NotFoundException("AppRole", command.RolePublicId);

        await _appUserRepo.UpdateRoleAsync(appId, user.Id, role.Id, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.AppUser, user.Id.ToString(), $"User role changed in app: {user.Email} to {role.Name}", appId: appId, ct: ct);
    }
}
