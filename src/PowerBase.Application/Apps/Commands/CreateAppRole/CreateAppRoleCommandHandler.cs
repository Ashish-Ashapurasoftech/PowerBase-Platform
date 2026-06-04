using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.CreateAppRole;

public record CreateAppRoleResult(Guid PublicId, string Name, bool IsDefault);

public class CreateAppRoleCommandHandler
{
    private readonly IAppRepository _appRepo;
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAuditRepository _auditRepo;
    private readonly IAppRolePermissionRepository _permRepo;

    public CreateAppRoleCommandHandler(
        IAppRepository appRepo,
        IAppRoleRepository appRoleRepo,
        IQueryContext queryContext,
        IAuditRepository auditRepo,
        IAppRolePermissionRepository permRepo)
    {
        _appRepo = appRepo;
        _appRoleRepo = appRoleRepo;
        _queryContext = queryContext;
        _auditRepo = auditRepo;
        _permRepo = permRepo;
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

        var appId = await _appRepo.GetIdByPublicIdAsync(command.AppPublicId, ct);

        if (await _appRoleRepo.NameExistsInAppAsync(appId, command.Name, ct))
            throw new DuplicateException("AppRole", "name", command.Name);

        var (id, publicId) = await _appRoleRepo.CreateAsync(new AppRole
        {
            AppId = appId,
            Name = command.Name,
            IsDefault = command.IsDefault,
            IsSystem = false,
        }, ct: ct);

        // Default permissions: structural reads only. Record data access is governed by
        // table-level permissions (ViewScope / CanAdd / ModifyScope / CanDelete) on each table.
        var defaultPermissions = new[]
        {
            PermissionCodes.TablesRead,
            PermissionCodes.FieldsRead,
            PermissionCodes.ReportsRead,
            PermissionCodes.ReportsRun,
            PermissionCodes.FormsRead,
        };
        await _appRoleRepo.SetPermissionsAsync(id, defaultPermissions, null, ct);

        // Seed default table-level permission rows for every existing table in the app
        await _permRepo.SeedDefaultsForRoleAsync(id, appId, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Created, AuditEntityTypes.AppRole, id.ToString(), $"App role added: {command.Name}", appId: appId, ct: ct);

        return new CreateAppRoleResult(publicId, command.Name, command.IsDefault);
    }
}
