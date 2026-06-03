using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Roles.Commands.UpdateRolePermissions;

public class UpdateRolePermissionsCommandHandler
{
    private readonly ITenantRepository _tenantRepo;
    private readonly IPermissionRepository _permissionRepo;
    private readonly IAuditRepository _auditRepo;

    public UpdateRolePermissionsCommandHandler(ITenantRepository tenantRepo, IPermissionRepository permissionRepo, IAuditRepository auditRepo)
    {
        _tenantRepo = tenantRepo;
        _permissionRepo = permissionRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(UpdateRolePermissionsCommand command, CancellationToken ct = default)
    {
        var role = await _tenantRepo.GetRoleByPublicIdAsync(command.RolePublicId, ct)
            ?? throw new NotFoundException("TenantRole", command.RolePublicId);

        var allPerms = await _permissionRepo.GetAllAsync(ct);
        var allCodes = allPerms.Select(p => p.Code).ToHashSet();
        var invalidCodes = command.PermissionCodes.Where(c => !allCodes.Contains(c)).ToList();
        if (invalidCodes.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["PermissionCodes"] = [$"Unknown permission codes: {string.Join(", ", invalidCodes)}"] });

        var permIds = allPerms.Where(p => command.PermissionCodes.Contains(p.Code)).Select(p => p.Id).ToList();
        await _permissionRepo.ReplaceRolePermissionsAsync(role.Id, permIds, ct);

        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.TenantRole, role.Id.ToString(), $"Tenant role permissions modified: {role.Name}", ct: ct);
    }
}
