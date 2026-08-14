using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.AddAppUser;

public class AddAppUserCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IUserRepository _userRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;

    public AddAppUserCommandHandler(
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

    public async Task HandleAsync(AddAppUserCommand command, CancellationToken ct = default)
    {
        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        var user = await _userRepo.GetByEmailAsync(command.Email)
            ?? throw new NotFoundException("User", command.Email);

        var existing = await _appUserRepo.GetByAppAndUserAsync(appId, user.Id, ct);
        if (existing is not null)
            throw new DuplicateException("AppUser", $"User '{user.Email}' is already added to this application.");

        long roleId;
        if (command.RolePublicId.HasValue)
        {
            var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId.Value, ct)
                ?? throw new NotFoundException("AppRole", command.RolePublicId.Value);
            roleId = role.Id;
        }
        else
        {
            roleId = await _appRepo.GetDefaultRoleIdAsync(appId, ct)
                ?? throw new NotFoundException("DefaultAppRole", appId);
        }

        await _appUserRepo.CreateAsync(new AppUser
        {
            AppId        = appId,
            UserId       = user.Id,
            UserPublicId = user.PublicId,
            UserName     = user.Name,
            UserEmail    = user.Email,
            AppRoleId    = roleId,
            Status       = "Active",
            AddedBy      = _queryContext.UserId,
        }, ct: ct);
        
        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.AppUser, user.Id.ToString(), $"User added to app: {user.Email}", appId: appId, ct: ct);
    }
}
