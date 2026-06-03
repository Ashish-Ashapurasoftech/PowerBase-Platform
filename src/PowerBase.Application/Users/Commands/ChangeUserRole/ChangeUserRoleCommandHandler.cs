using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Users.Commands.ChangeUserRole;

public class ChangeUserRoleCommandHandler
{
    private readonly ITenantRepository _tenantRepo;
    private readonly IAuditRepository _auditRepo;

    public ChangeUserRoleCommandHandler(ITenantRepository tenantRepo, IAuditRepository auditRepo)
    {
        _tenantRepo = tenantRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(ChangeUserRoleCommand command, CancellationToken ct = default)
    {
        var tenantUser = await _tenantRepo.GetTenantUserByUserPublicIdAsync(command.UserPublicId, ct)
            ?? throw new NotFoundException("TenantUser", command.UserPublicId);

        var role = await _tenantRepo.GetRoleByPublicIdAsync(command.RolePublicId, ct)
            ?? throw new NotFoundException("TenantRole", command.RolePublicId);

        await _tenantRepo.UpdateTenantUserRoleAsync(tenantUser.Id, role.Id, ct);
        
        await _auditRepo.LogActivityAsync(
            AuditActions.Updated, AuditEntityTypes.TenantUser, tenantUser.Id.ToString(), $"User role changed in tenant: {tenantUser.UserId} to {role.Name}", ct: ct);
    }
}
