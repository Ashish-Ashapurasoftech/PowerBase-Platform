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

        var app = await _appRepo.GetByPublicIdAsync(command.AppPublicId, ct);
        if (app == null)
            throw new NotFoundException("App", command.AppPublicId);

        var currentUserRolePublicId = await _appUserRepo.GetUserRolePublicIdAsync(role.AppId, _queryContext.UserId, ct);
        AppRole? actorRole = null;
        if (currentUserRolePublicId.HasValue)
        {
            actorRole = await _appRoleRepo.GetByPublicIdAsync(currentUserRolePublicId.Value, ct);
        }

        bool isAdministrator = actorRole?.Name == "Administrator";
        bool isAuthorizedToConfigure = _queryContext.IsSuperAdmin || _queryContext.IsTenantAdmin || _queryContext.UserId == app.OwnerId || isAdministrator;
        int? actorRank = actorRole?.Rank;

        if (!_queryContext.IsSuperAdmin && !_queryContext.IsTenantAdmin && _queryContext.UserId != app.OwnerId && !isAdministrator && currentUserRolePublicId == command.RolePublicId)
        {
            throw new UnauthorizedActionException("modify your own app role");
        }

        if (actorRole == null && !_queryContext.IsSuperAdmin && !_queryContext.IsTenantAdmin)
        {
            throw new UnauthorizedActionException("You are not a member of this application.");
        }

        if (!_queryContext.IsSuperAdmin && !_queryContext.IsTenantAdmin && _queryContext.UserId != app.OwnerId)
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
            ((command.ManageableRolesType != null && command.ManageableRolesType != role.ManageableRolesType) ||
             (command.ManageableRolePublicIds != null && !command.ManageableRolePublicIds.SequenceEqual(await _appRoleRepo.GetManageableRolePublicIdsAsync(role.Id, ct) ?? Array.Empty<Guid>()))))
        {
            throw new UnauthorizedActionException("Only platform Super Admins, Tenant Administrators, App Owners, or App Administrators can configure role hierarchy settings.");
        }

        if (!isAuthorizedToConfigure && command.Name != null && command.Name != role.Name)
        {
            throw new UnauthorizedActionException("Only platform Super Admins, Tenant Administrators, App Owners, or App Administrators can rename a role.");
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
            if (!isAuthorizedToConfigure)
            {
                var actorPermissions = await _appUserRepo.GetUserAppPermissionsAsync(app.Id, _queryContext.UserId, ct);

                var unauthorizedCodes = command.Permissions
                    .Where(p => !actorPermissions.Contains(p))
                    .ToList();

                if (unauthorizedCodes.Count > 0)
                {
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["Permissions"] = [$"You cannot assign permissions that your own role does not possess: {string.Join(", ", unauthorizedCodes)}"]
                    });
                }
            }

            await _appRoleRepo.SetPermissionsAsync(role.Id, command.Permissions, null, ct);
        }

        string? renamedTo = null;
        if (command.Name != null && command.Name != role.Name)
        {
            if (role.IsSystem)
                throw new UnauthorizedActionException("System roles cannot be renamed.");

            if (await _appRoleRepo.NameExistsInAppAsync(role.AppId, command.Name, excludeRoleId: role.Id, ct: ct))
                throw new DuplicateException("AppRole", "name", command.Name);

            await _appRoleRepo.UpdateNameAsync(role.Id, command.Name, ct);
            renamedTo = command.Name;
        }

        if (isAuthorizedToConfigure && 
            (command.ManageableRolesType != null || command.Rank.HasValue || command.ManageableRolePublicIds != null))
        {
            string manageableRolesType = command.ManageableRolesType ?? role.ManageableRolesType;
            int? rank = command.Rank.HasValue ? command.Rank.Value : role.Rank;
            IReadOnlyList<Guid> allowedIds = command.ManageableRolePublicIds ?? (await _appRoleRepo.GetManageableRolePublicIdsAsync(role.Id, ct)) ?? Array.Empty<Guid>();

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

        var auditMessage = renamedTo != null
            ? $"App role renamed: {role.Name} -> {renamedTo}"
            : $"App role permissions modified: {role.Name}";
        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.AppRole, role.Id.ToString(), auditMessage, appId: role.AppId, ct: ct);
    }
}
