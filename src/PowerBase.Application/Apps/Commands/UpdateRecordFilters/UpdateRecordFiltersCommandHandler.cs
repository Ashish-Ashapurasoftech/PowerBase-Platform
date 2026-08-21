using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.UpdateRecordFilters;

public class UpdateRecordFiltersCommandHandler
{
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppRolePermissionRepository _permRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAppUserRepository _appUserRepo;

    public UpdateRecordFiltersCommandHandler(
        IAppRoleRepository appRoleRepo,
        IAppRolePermissionRepository permRepo,
        IAppTableRepository tableRepo,
        IAuditRepository auditRepo,
        IQueryContext queryContext,
        IAppUserRepository appUserRepo)
    {
        _appRoleRepo = appRoleRepo;
        _permRepo = permRepo;
        _tableRepo = tableRepo;
        _auditRepo = auditRepo;
        _queryContext = queryContext;
        _appUserRepo = appUserRepo;
    }

    public async Task HandleAsync(UpdateRecordFiltersCommand command, CancellationToken ct = default)
    {
        var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId, ct)
                   ?? throw new NotFoundException("AppRole", command.RolePublicId);

        var currentUserRolePublicId = await _appUserRepo.GetUserRolePublicIdAsync(role.AppId, _queryContext.UserId, ct);
        AppRole? actorRole = null;
        if (currentUserRolePublicId.HasValue)
        {
            actorRole = await _appRoleRepo.GetByPublicIdAsync(currentUserRolePublicId.Value, ct);
        }
        bool isAdministrator = actorRole?.Name == "Administrator";

        if (!_queryContext.IsSuperAdmin && !isAdministrator)
        {
            if (currentUserRolePublicId == command.RolePublicId)
            {
                throw new UnauthorizedActionException("modify record filters for your own app role");
            }

            if (actorRole == null)
            {
                throw new UnauthorizedActionException("Your role was not found.");
            }

            int actorRank = actorRole.Rank ?? int.MaxValue;
            int targetRank = role.Rank ?? int.MaxValue;
            if (targetRank <= actorRank)
            {
                throw new UnauthorizedActionException("You cannot manage a role equal to or above your own.");
            }

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

        var rows = new List<AppRoleRecordFilter>();
        foreach (var f in command.Filters)
        {
            if (f.Conditions.Count == 0) continue; // empty filter ⇒ no restriction; don't store
            var table = await _tableRepo.GetByPublicIdAsync(f.TablePublicId, ct);
            rows.Add(new AppRoleRecordFilter
            {
                AppRoleId = role.Id,
                AppTableId = table.Id,
                Conjunction = f.Conjunction == "OR" ? "OR" : "AND",
                FilterJson = JsonSerializer.Serialize(f.Conditions),
            });
        }

        await _permRepo.SetRecordFiltersAsync(role.Id, rows, null, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.AppRole, role.Id.ToString(),
            $"Record filters updated for role: {role.Name}", appId: role.AppId, ct: ct);
    }
}
