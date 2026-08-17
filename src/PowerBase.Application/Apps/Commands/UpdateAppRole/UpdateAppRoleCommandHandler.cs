using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.UpdateAppRole;

public class UpdateAppRoleCommandHandler
{
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppAccessService _appAccessService;
    private readonly IAuditRepository _auditRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAppUserRepository _appUserRepo;
    private readonly IAppRepository _appRepo;

    public UpdateAppRoleCommandHandler(
        IAppRoleRepository appRoleRepo, 
        IAppAccessService appAccessService, 
        IAuditRepository auditRepo,
        IQueryContext queryContext,
        IAppUserRepository appUserRepo,
        IAppRepository appRepo)
    {
        _appRoleRepo = appRoleRepo;
        _appAccessService = appAccessService;
        _auditRepo = auditRepo;
        _queryContext = queryContext;
        _appUserRepo = appUserRepo;
        _appRepo = appRepo;
    }

    public async Task HandleAsync(UpdateAppRoleCommand command, CancellationToken ct = default)
    {
        var validator = new UpdateAppRoleCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId, ct);
        if (role is null)
            throw new NotFoundException("AppRole", command.RolePublicId);

        var currentUserRolePublicId = await _appUserRepo.GetUserRolePublicIdAsync(role.AppId, _queryContext.UserId, ct);
        if (currentUserRolePublicId == command.RolePublicId)
        {
            throw new UnauthorizedActionException("modify your own app role");
        }

        var app = await _appRepo.GetByPublicIdAsync(command.AppPublicId, ct);
        if (app == null)
            throw new NotFoundException("App", command.AppPublicId);

        bool isAuthorizedToConfigure = _queryContext.IsSuperAdmin || _queryContext.UserId == app.OwnerId;
        int? actorRank = null;
        AppRole? actorRole = null;

        var actorAppUser = await _appUserRepo.GetByAppAndUserAsync(role.AppId, _queryContext.UserId, ct);
        if (actorAppUser == null && !_queryContext.IsSuperAdmin)
        {
            throw new UnauthorizedActionException("You are not a member of this application.");
        }

        if (currentUserRolePublicId.HasValue)
        {
            actorRole = await _appRoleRepo.GetByPublicIdAsync(currentUserRolePublicId.Value, ct);
            if (actorRole != null)
            {
                actorRank = actorRole.Rank;
                if (actorRole.Name == "Administrator")
                {
                    isAuthorizedToConfigure = true;
                }
            }
        }

        if (!_queryContext.IsSuperAdmin && _queryContext.UserId != app.OwnerId)
        {
            if (actorRole == null)
            {
                throw new UnauthorizedActionException("Your role was not found.");
            }

            // Hard Rule: Target role's rank must be strictly greater than actor's rank
            int actorRankVal = actorRole.Rank ?? int.MaxValue;
            int targetRankVal = role.Rank ?? int.MaxValue;
            if (targetRankVal <= actorRankVal)
            {
                throw new UnauthorizedActionException("You cannot manage a role equal to or above your own.");
            }

            // Configured setting check
            if (actorRole.ManageableRolesType == "None")
            {
                throw new UnauthorizedActionException("Your role is not allowed to manage any roles.");
            }
            else if (actorRole.ManageableRolesType == "Manual")
            {
                var manageableIds = await _appRoleRepo.GetManageableRolePublicIdsAsync(actorRole.Id, ct);
                if (!manageableIds.Contains(role.PublicId))
                {
                    throw new UnauthorizedActionException("Your role is not allowed to manage this role.");
                }
            }
        }

        if (!isAuthorizedToConfigure && 
            (command.ManageableRolesType != null || command.Rank.HasValue || command.ManageableRolePublicIds != null))
        {
            throw new UnauthorizedActionException("Only platform Super Admins, App Owners, or App Administrators can configure role hierarchy settings.");
        }

        if (command.Rank.HasValue && !(_queryContext.IsSuperAdmin || _queryContext.UserId == app.OwnerId))
        {
            int actorRankVal = actorRank ?? int.MaxValue;
            if (command.Rank.Value <= actorRankVal)
            {
                throw new UnauthorizedActionException("You cannot configure a role rank equal to or superior to your own.");
            }
        }

        if (command.Permissions != null)
        {
            await _appRoleRepo.SetPermissionsAsync(role.Id, command.Permissions, null, ct);
        }

        if (isAuthorizedToConfigure && 
            (command.ManageableRolesType != null || command.Rank.HasValue || command.ManageableRolePublicIds != null))
        {
            string manageableRolesType = command.ManageableRolesType ?? role.ManageableRolesType;
            int? rank = command.Rank.HasValue ? command.Rank.Value : role.Rank;
            var allowedIds = command.ManageableRolePublicIds ?? (await _appRoleRepo.GetManageableRolePublicIdsAsync(role.Id, ct)) ?? new List<Guid>();

            if (manageableRolesType == "Manual" && allowedIds != null)
            {
                if (allowedIds.Contains(role.PublicId))
                {
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["ManageableRolePublicIds"] = ["A role cannot manage itself."]
                    });
                }

                foreach (var rPublicId in allowedIds)
                {
                    var targetRole = await _appRoleRepo.GetByPublicIdAsync(rPublicId, ct)
                        ?? throw new NotFoundException("AppRole", rPublicId);

                    if ((targetRole.Rank ?? int.MaxValue) <= (rank ?? int.MaxValue))
                    {
                        throw new ValidationException(new Dictionary<string, string[]>
                        {
                            ["ManageableRolePublicIds"] = [$"Cannot add role '{targetRole.Name}' (Rank {targetRole.Rank}) to the manageable list. It must be ranked lower (higher number) than the target role (Rank {rank})."]
                        });
                    }
                }
            }

            await _appRoleRepo.UpdateRoleHierarchyAsync(role.PublicId, manageableRolesType, rank, allowedIds, ct);
        }

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.AppRole, role.Id.ToString(), $"App role permissions modified: {role.Name}", appId: role.AppId, ct: ct);
    }
}
