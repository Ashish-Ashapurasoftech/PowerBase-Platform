using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Apps.Commands.UpdateFieldPermissions;

public class UpdateFieldPermissionsCommandHandler
{
    private readonly IAppRoleRepository _appRoleRepo;
    private readonly IAppRolePermissionRepository _permRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly IAuditRepository _auditRepo;
    private readonly IQueryContext _queryContext;
    private readonly IAppUserRepository _appUserRepo;

    public UpdateFieldPermissionsCommandHandler(
        IAppRoleRepository appRoleRepo,
        IAppRolePermissionRepository permRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        IAuditRepository auditRepo,
        IQueryContext queryContext,
        IAppUserRepository appUserRepo)
    {
        _appRoleRepo = appRoleRepo;
        _permRepo = permRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _auditRepo = auditRepo;
        _queryContext = queryContext;
        _appUserRepo = appUserRepo;
    }

    public async Task HandleAsync(UpdateFieldPermissionsCommand command, CancellationToken ct = default)
    {
        var role = await _appRoleRepo.GetByPublicIdAsync(command.RolePublicId, ct)
                   ?? throw new NotFoundException("AppRole", command.RolePublicId);

        var currentUserRolePublicId = await _appUserRepo.GetUserRolePublicIdAsync(role.AppId, _queryContext.UserId, ct);
        if (currentUserRolePublicId == command.RolePublicId)
        {
            throw new UnauthorizedActionException("modify field permissions for your own app role");
        }
        var table = await _tableRepo.GetByPublicIdAsync(command.TablePublicId, ct);

        // Only persist non-default ('Modify') entries to keep the table lean.
        var rows = new List<AppRoleFieldPermission>();
        foreach (var f in command.Fields)
        {
            var access = Normalize(f.Access);
            if (access == FieldAccessLevels.Modify) continue;

            var field = await _fieldRepo.GetByPublicIdAsync(f.FieldPublicId, ct);
            if (field is null || field.AppTableId != table.Id) continue;

            // Invariant: a required field with no default value cannot be hidden (None) or read-only (View)
            // for a role — those users could never submit a record.
            if ((access == FieldAccessLevels.None || access == FieldAccessLevels.View)
                && field.IsRequired && string.IsNullOrWhiteSpace(field.DefaultValue))
            {
                var name = field.Label ?? field.Name;
                throw new ValidationException(new Dictionary<string, string[]>
                {
                    ["Access"] =
                    [
                        $"'{name}' is a required field with no default value. It cannot be set to '{access}' " +
                        "for this role because users would be unable to submit records. " +
                        "Add a default value to the field first."
                    ],
                });
            }

            rows.Add(new AppRoleFieldPermission { AppRoleId = role.Id, AppFieldId = field.Id, Access = access });
        }

        await _permRepo.SetFieldPermissionsAsync(role.Id, table.Id, rows, null, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.AppRole, role.Id.ToString(),
            $"Field permissions updated for role: {role.Name} on table {table.Name}", appId: role.AppId, ct: ct);
    }

    private static string Normalize(string access) => access switch
    {
        FieldAccessLevels.View or FieldAccessLevels.Modify or FieldAccessLevels.None => access,
        _ => FieldAccessLevels.Modify,
    };
}
