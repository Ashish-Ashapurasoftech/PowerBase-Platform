using PowerBase.Application.Common.Interfaces;
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

    public AddAppUserCommandHandler(
        IAppRepository appRepo,
        IAppRoleRepository appRoleRepo,
        IAppUserRepository appUserRepo,
        IUserRepository userRepo,
        IAppAccessService appAccessService,
        IQueryContext queryContext)
    {
        _appRepo = appRepo;
        _appRoleRepo = appRoleRepo;
        _appUserRepo = appUserRepo;
        _userRepo = userRepo;
        _appAccessService = appAccessService;
        _queryContext = queryContext;
    }

    public async Task HandleAsync(AddAppUserCommand command, CancellationToken ct = default)
    {
        await _appAccessService.RequireByAppPublicIdAsync(command.AppPublicId, AppAccess.Admin, ct);

        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        var user = await _userRepo.GetByEmailAsync(command.Email)
            ?? throw new NotFoundException("User", command.Email);

        var existing = await _appUserRepo.GetByAppAndUserAsync(appId, user.Id, ct);
        if (existing is not null)
            throw new DuplicateException("AppUser", "userId", user.Id.ToString());

        AppRole role;
        if (command.RolePublicId.HasValue)
        {
            role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId.Value, ct)
                ?? throw new NotFoundException("AppRole", command.RolePublicId.Value);
        }
        else
        {
            var defaultRoleId = await _appRepo.GetDefaultRoleIdAsync(appId, ct)
                ?? throw new NotFoundException("DefaultAppRole", appId);
            var allRoles = await _appRoleRepo.ListByAppIdAsync(appId, ct);
            role = allRoles.FirstOrDefault(r => r.Id == defaultRoleId)
                ?? throw new NotFoundException("DefaultAppRole", appId);
        }

        await _appUserRepo.CreateAsync(new AppUser
        {
            AppId = appId,
            TenantId = _queryContext.TenantId,
            UserId = user.Id,
            AppRoleId = role.Id,
            Status = "Active",
            AddedBy = _queryContext.UserId,
        }, ct: ct);
    }
}
