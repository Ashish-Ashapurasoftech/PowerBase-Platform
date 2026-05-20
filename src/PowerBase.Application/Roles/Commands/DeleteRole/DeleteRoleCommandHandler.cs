using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Roles.Commands.DeleteRole;

public class DeleteRoleCommandHandler
{
    private readonly ITenantRepository _tenantRepo;

    public DeleteRoleCommandHandler(ITenantRepository tenantRepo)
    {
        _tenantRepo = tenantRepo;
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
    }
}
