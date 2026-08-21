using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.CreateAppRole;

public record CreateAppRoleResult(
    Guid PublicId, 
    string Name, 
    bool IsDefault,
    string ManageableRolesType,
    int? Rank,
    IReadOnlyList<Guid> ManageableRolePublicIds);

public class CreateAppRoleCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;
    private readonly IAppRolePermissionRepository _permRepo;
    private readonly IAppUserRepository _appUserRepo;

    public CreateAppRoleCommandHandler(
        IAppRepository appRepo,
        IAppRoleRepository appRoleRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo,
        IAppRolePermissionRepository permRepo,
        IAppUserRepository appUserRepo)
    {
        _appRepo = appRepo;
        _appRoleRepo = appRoleRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
        _permRepo = permRepo;
        _appUserRepo = appUserRepo;
    }

    public async Task<CreateAppRoleResult> HandleAsync(CreateAppRoleCommand command, CancellationToken ct = default)
    {
        var validator = new CreateAppRoleCommandValidator();
        var validation = await validator.ValidateAsync(command, ct);
        if (!validation.IsValid)
            throw new ValidationException(
                validation.Errors
                    .GroupBy(e => e.PropertyName)
                    .ToDictionary(g => g.Key, g => g.Select(e => e.ErrorMessage).ToArray()));

        var app = await _appRepo.GetByPublicIdAsync(command.AppPublicId, ct);
        if (app == null)
            throw new NotFoundException("App", command.AppPublicId);
        var appId = app.Id;

        if (await _appRoleRepo.NameExistsInAppAsync(appId, command.Name, ct))
            throw new DuplicateException("AppRole", "name", command.Name);

        bool isAuthorizedToConfigure = _queryContext.IsSuperAdmin || _queryContext.UserId == app.OwnerId;
        int? actorRank = null;

        if (!isAuthorizedToConfigure)
        {
            var actorRolePublicId = await _appUserRepo.GetUserRolePublicIdAsync(appId, _queryContext.UserId, ct);
            if (actorRolePublicId.HasValue)
            {
                var actorRole = await _appRoleRepo.GetByPublicIdAsync(actorRolePublicId.Value, ct);
                if (actorRole != null)
                {
                    actorRank = actorRole.Rank;
                    if (actorRole.Name == "Administrator")
                    {
                        isAuthorizedToConfigure = true;
                    }
                }
            }
        }

        if (!isAuthorizedToConfigure && 
            (command.ManageableRolesType != null || command.Rank != null || command.ManageableRolePublicIds != null))
        {
            throw new UnauthorizedActionException("Only platform Super Admins, App Owners, or App Administrators can configure role hierarchy settings.");
        }

        int targetRank = command.Rank ?? 3;
        string targetType = command.ManageableRolesType ?? "None";

        // Hard system rule: actor cannot create/assign a role equal to or superior to their own rank
        if (actorRank.HasValue && targetRank <= actorRank.Value)
        {
            throw new UnauthorizedActionException("You cannot create a role with a rank equal to or superior to your own.");
        }

        if (isAuthorizedToConfigure && targetType == "Manual" && command.ManageableRolePublicIds != null)
        {
            foreach (var rPublicId in command.ManageableRolePublicIds)
            {
                var targetRole = await _appRoleRepo.GetByPublicIdAsync(rPublicId, ct)
                    ?? throw new NotFoundException("AppRole", rPublicId);

                if (targetRole.Rank <= targetRank)
                {
                    throw new ValidationException(new Dictionary<string, string[]>
                    {
                        ["ManageableRolePublicIds"] = [$"Cannot add role '{targetRole.Name}' (Rank {targetRole.Rank}) to the manageable list. It must be ranked lower (higher number) than the created role (Rank {targetRank})."]
                    });
                }
            }
        }

        var (id, publicId) = await _appRoleRepo.CreateAsync(new AppRole
        {
            AppId = appId,
            Name = command.Name,
            IsDefault = command.IsDefault,
            IsSystem = false,
            ManageableRolesType = targetType,
            Rank = targetRank
        }, ct: ct);

        if (isAuthorizedToConfigure && command.ManageableRolePublicIds != null && command.ManageableRolePublicIds.Any())
        {
            await _appRoleRepo.UpdateRoleHierarchyAsync(publicId, targetType, targetRank, command.ManageableRolePublicIds, ct);
        }

        // Default permissions: structural reads only. Record data access is governed by
        // table-level permissions (ViewScope / CanAdd / ModifyScope / CanDelete) on each table.
        var defaultPermissions = new[]
        {
            PermissionCodes.TablesRead,
            PermissionCodes.FieldsRead,
            PermissionCodes.ReportsRead,
            PermissionCodes.ReportsRun,
            PermissionCodes.FormsRead,
            PermissionCodes.PowerFlowsRead
        };
        await _appRoleRepo.SetPermissionsAsync(id, defaultPermissions, null, ct);

        // Seed default table-level permission rows for every existing table in the app
        await _permRepo.SeedDefaultsForRoleAsync(id, appId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.AppRole, id.ToString(), $"App role added: {command.Name}", appId: appId, ct: ct);

        return new CreateAppRoleResult(
            publicId, 
            command.Name, 
            command.IsDefault, 
            targetType, 
            targetRank, 
            command.ManageableRolePublicIds ?? Array.Empty<Guid>());
    }
}
