using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Roles.Commands.DeleteRole;

public class DeleteRoleCommandHandler
{
    private readonly ITenantRepository _tenantRepo;
    private readonly IAuditRepository _auditRepo;

    public DeleteRoleCommandHandler(ITenantRepository tenantRepo, IAuditRepository auditRepo)
    {
        _tenantRepo = tenantRepo;
        _auditRepo = auditRepo;
    }

    public async Task HandleAsync(DeleteRoleCommand command, CancellationToken ct = default)
    {
        var role = await _tenantRepo.GetRoleByPublicIdAsync(command.PublicId, ct)
            ?? throw new NotFoundException("TenantRole", command.PublicId);

        if (role.IsSystem)
            throw new UnauthorizedActionException("System roles cannot be deleted.");

        var memberCount = await _tenantRepo.CountRoleMembersAsync(role.Id, ct);
        if (memberCount > 0)
            throw new ConflictException($"Cannot delete role '{role.Name}': {memberCount} user(s) are still assigned to it.");

        var affected = await _tenantRepo.DeleteRoleAsync(command.PublicId, ct);
        if (affected == 0)
            throw new NotFoundException("TenantRole", command.PublicId);

        await _auditRepo.LogActivityAsync(
            AuditActions.Deleted, AuditEntityTypes.TenantRole, role.Id.ToString(), $"Tenant role deleted: {role.Name}", ct: ct);
    }
}
