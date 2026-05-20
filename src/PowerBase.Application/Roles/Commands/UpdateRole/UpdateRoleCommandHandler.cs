using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Roles.Commands.UpdateRole;

public class UpdateRoleCommandHandler
{
    private readonly ITenantRepository _tenantRepo;

    public UpdateRoleCommandHandler(ITenantRepository tenantRepo)
    {
        _tenantRepo = tenantRepo;
    }

    public async Task HandleAsync(UpdateRoleCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Name is required."] });

        var role = await _tenantRepo.GetRoleByPublicIdAsync(command.PublicId, ct)
            ?? throw new NotFoundException("TenantRole", command.PublicId);

        if (role.IsSystem)
            throw new UnauthorizedActionException("System roles cannot be renamed.");

        if (!string.Equals(role.Name, command.Name, StringComparison.OrdinalIgnoreCase)
            && await _tenantRepo.RoleNameExistsAsync(command.Name, ct))
            throw new DuplicateException("TenantRole", "name", command.Name);

        var affected = await _tenantRepo.UpdateRoleAsync(command.PublicId, command.Name, command.Description, ct);
        if (affected == 0)
            throw new NotFoundException("TenantRole", command.PublicId);
    }
}
