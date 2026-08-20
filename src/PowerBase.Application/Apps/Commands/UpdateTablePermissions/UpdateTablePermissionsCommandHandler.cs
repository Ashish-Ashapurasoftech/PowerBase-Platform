using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.UpdateTablePermissions;

public class UpdateTablePermissionsCommandHandler
{
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppRolePermissionRepository _permRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAppUserRepository _appUserRepo;

    public UpdateTablePermissionsCommandHandler(
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

    public async Task HandleAsync(UpdateTablePermissionsCommand command, CancellationToken ct = default)
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
                throw new UnauthorizedActionException("modify table permissions for your own app role");
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

        var rows = new List<AppRoleTablePermission>(command.Tables.Count);
        foreach (var t in command.Tables)
        {
            var table = await _tableRepo.GetByPublicIdAsync(t.TablePublicId, ct);
            rows.Add(new AppRoleTablePermission
            {
                AppRoleId = role.Id,
                AppTableId = table.Id,
                ViewScope = Normalize(t.ViewScope, RecordScopes.AllRecords),
                ModifyScope = Normalize(t.ModifyScope, RecordScopes.None),
                CanAdd = t.CanAdd,
                CanDelete = t.CanDelete,
                CanSaveSharedReports = t.CanSaveSharedReports,
                CanEditFieldProperties = t.CanEditFieldProperties,
                FieldAccessLevel = t.FieldAccessLevel == TableFieldAccessLevels.CustomAccess
                    ? TableFieldAccessLevels.CustomAccess
                    : TableFieldAccessLevels.FullAccess,
            });
        }

        await _permRepo.SetTablePermissionsAsync(role.Id, rows, null, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.AppRole, role.Id.ToString(),
            $"Table permissions updated for role: {role.Name}", appId: role.AppId, ct: ct);
    }

    private static string Normalize(string scope, string fallback) => scope switch
    {
        RecordScopes.None or RecordScopes.OwnRecords or RecordScopes.AllRecords => scope,
        _ => fallback,
    };
}
