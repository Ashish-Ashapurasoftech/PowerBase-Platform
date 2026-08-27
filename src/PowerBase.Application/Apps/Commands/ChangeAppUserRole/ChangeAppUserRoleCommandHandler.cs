using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
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
        var app = await _appRepo.GetByPublicIdAsync(command.AppPublicId, ct);
        var appId = app.Id;

        var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId, ct)
            ?? throw new NotFoundException("AppRole", command.RolePublicId);

        AppUser? appUser = await _appUserRepo.GetByPublicIdAsync(command.UserPublicId, ct);
        long targetUserId;
        string targetUserEmail;
        bool isAssignment = false;

        if (appUser != null && appUser.AppId == appId)
        {
            targetUserId = appUser.UserId;
            targetUserEmail = appUser.UserEmail ?? string.Empty;
            isAssignment = true;
        }
        else
        {
            var user = await _userRepo.GetByPublicIdAsync(command.UserPublicId, ct)
                ?? throw new NotFoundException("User", command.UserPublicId);

            targetUserId = user.Id;
            targetUserEmail = user.Email;

            appUser = await _appUserRepo.GetByAppAndUserAsync(appId, user.Id, ct)
                ?? throw new NotFoundException("AppUser", command.UserPublicId);
        }

        if (string.IsNullOrWhiteSpace(targetUserEmail))
        {
            var u = await _userRepo.GetByIdAsync(targetUserId, ct);
            if (u != null) targetUserEmail = u.Email;
        }

        if (app.OwnerId == targetUserId)
        {
            throw new UnauthorizedActionException("Cannot change the role of the app owner.");
        }

        if (appUser.AppRoleId == role.Id)
        {
            throw new BadRequestException($"User '{targetUserEmail}' is already assigned the '{role.Name}' role.");
        }

        var existingWithTargetRole = await _appUserRepo.GetByAppUserAndRoleAsync(appId, targetUserId, role.Id, ct);
        if (existingWithTargetRole != null && existingWithTargetRole.Id != appUser.Id)
        {
            throw new DuplicateException("AppUser", $"User '{targetUserEmail}' already has the '{role.Name}' role in this application.");
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

            int actorRank = actorRole.Rank ?? int.MaxValue;

            // Load the target user's current role
            var allAppRoles = await _appRoleRepo.ListDetailsByAppIdAsync(appId, ct);
            var currentRoleDetail = allAppRoles.FirstOrDefault(r => r.Id == appUser.AppRoleId)
                ?? throw new InvalidOperationException("Current app role of the target user was not found.");

            int currentRank = currentRoleDetail.Rank ?? int.MaxValue;

            // 1. Validate permissions on current role
            if (currentRank <= actorRank)
            {
                throw new UnauthorizedActionException("You cannot manage a user with a role equal to or above your own.");
            }

            if (actorRole.ManageableRolesType == "None")
            {
                throw new UnauthorizedActionException("Your role is not allowed to manage any roles.");
            }
            else if (actorRole.ManageableRolesType == "Manual")
            {
                var manageableIds = await _appRoleRepo.GetManageableRolePublicIdsAsync(actorRole.Id, ct);
                if (!manageableIds.Contains(currentRoleDetail.PublicId))
                {
                    throw new UnauthorizedActionException("Your role is not allowed to manage users in this role.");
                }
            }

            // 2. Validate permissions on new role
            int newRank = role.Rank ?? int.MaxValue;
            if (newRank <= actorRank)
            {
                throw new UnauthorizedActionException("You cannot assign a role equal to or above your own.");
            }

            if (actorRole.ManageableRolesType == "Manual")
            {
                var manageableIds = await _appRoleRepo.GetManageableRolePublicIdsAsync(actorRole.Id, ct);
                if (!manageableIds.Contains(role.PublicId))
                {
                    throw new UnauthorizedActionException("Your role is not allowed to assign the selected role.");
                }
            }
        }

        if (isAssignment)
        {
            await _appUserRepo.UpdateRoleByAssignmentAsync(appId, appUser.PublicId, role.Id, ct);
        }
        else
        {
            await _appUserRepo.UpdateRoleAsync(appId, targetUserId, role.Id, ct);
        }

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.AppUser, targetUserId.ToString(), $"User role changed in app: {targetUserEmail} to {role.Name}", appId: appId, ct: ct);
    }
}
