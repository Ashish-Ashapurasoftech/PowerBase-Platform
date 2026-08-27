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

        AppRole targetRole;
        if (command.RolePublicId.HasValue)
        {
            targetRole = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId.Value, ct)
                ?? throw new NotFoundException("AppRole", command.RolePublicId.Value);
        }
        else
        {
            var defaultRoleId = await _appRepo.GetDefaultRoleIdAsync(appId, ct)
                ?? throw new NotFoundException("DefaultAppRole", appId);
            var roles = await _appRoleRepo.ListDetailsByAppIdAsync(appId, ct);
            var defaultRoleDetail = roles.FirstOrDefault(r => r.Id == defaultRoleId)
                ?? throw new InvalidOperationException("Default app role not found.");
            targetRole = new AppRole 
            { 
                Id = defaultRoleDetail.Id, 
                PublicId = defaultRoleDetail.PublicId, 
                Name = defaultRoleDetail.Name,
                Rank = defaultRoleDetail.Rank,
                ManageableRolesType = defaultRoleDetail.ManageableRolesType
            };
        }

        var existingSameRole = await _appUserRepo.GetByAppUserAndRoleAsync(appId, user.Id, targetRole.Id, ct);
        if (existingSameRole is not null)
        {
            throw new DuplicateException("AppUser", $"User '{user.Email}' already has the '{targetRole.Name}' role in this application.");
        }

        if (!_queryContext.IsSuperAdmin)
        {
            var actorRolePublicId = await _appUserRepo.GetUserRolePublicIdAsync(appId, _queryContext.UserId, ct);
            if (!actorRolePublicId.HasValue)
            {
                throw new UnauthorizedActionException("Your role in this application was not found.");
            }

            var actorRole = await _appRoleRepo.GetByPublicIdAsync(actorRolePublicId.Value, ct);
            if (actorRole == null)
            {
                throw new UnauthorizedActionException("Your role in this application was not found.");
            }

            // Hard Rule: Target role's rank must be strictly greater than actor's rank
            int actorRank = actorRole.Rank ?? int.MaxValue;
            int targetRank = targetRole.Rank ?? int.MaxValue;
            if (targetRank <= actorRank)
            {
                throw new UnauthorizedActionException("You cannot assign a role equal to or above your own.");
            }

            // Configured setting check
            if (actorRole.ManageableRolesType == "None")
            {
                throw new UnauthorizedActionException("Your role is not allowed to manage or assign any roles.");
            }
            else if (actorRole.ManageableRolesType == "Manual")
            {
                var manageableIds = await _appRoleRepo.GetManageableRolePublicIdsAsync(actorRole.Id, ct);
                if (!manageableIds.Contains(targetRole.PublicId))
                {
                    throw new UnauthorizedActionException("Your role is not allowed to assign this role.");
                }
            }
        }

        long roleId = targetRole.Id;

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
