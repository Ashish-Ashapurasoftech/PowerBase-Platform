using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Roles.Commands.CreateRole;

public class CreateRoleCommandHandler
{
    private readonly ITenantRepository _tenantRepo;
    private readonly IPermissionRepository _permissionRepo;
    private readonly IQueryContext _queryContext;

    public CreateRoleCommandHandler(ITenantRepository tenantRepo, IPermissionRepository permissionRepo, IQueryContext queryContext)
    {
        _tenantRepo = tenantRepo;
        _permissionRepo = permissionRepo;
        _queryContext = queryContext;
    }

    public async Task<RoleResult> HandleAsync(CreateRoleCommand command, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(command.Name))
            throw new ValidationException(new Dictionary<string, string[]> { ["Name"] = ["Role name is required."] });

        if (await _tenantRepo.RoleNameExistsAsync(command.Name, ct))
            throw new DuplicateException("TenantRole", "name", command.Name);

        var allPerms = await _permissionRepo.GetAllAsync(ct);
        var allCodes = allPerms.Select(p => p.Code).ToHashSet();
        var invalidCodes = command.PermissionCodes.Where(c => !allCodes.Contains(c)).ToList();
        if (invalidCodes.Count > 0)
            throw new ValidationException(new Dictionary<string, string[]> { ["PermissionCodes"] = [$"Unknown permission codes: {string.Join(", ", invalidCodes)}"] });

        var role = new TenantRole
        {
            TenantId = _queryContext.TenantId,
            Name = command.Name,
            Description = command.Description,
            IsDefault = false,
            IsSystem = false,
            CreatedBy = _queryContext.UserId,
        };

        var roleId = await _tenantRepo.CreateRoleAsync(role, ct: ct);

        if (command.PermissionCodes.Count > 0)
        {
            var permIds = allPerms.Where(p => command.PermissionCodes.Contains(p.Code)).Select(p => p.Id).ToList();
            await _permissionRepo.AssignToRoleAsync(roleId, permIds, ct: ct);
        }

        var created = await _tenantRepo.GetRoleByIdAsync(roleId, ct)
            ?? throw new NotFoundException("TenantRole", roleId);

        return new RoleResult(created.PublicId, created.Name, created.Description, created.IsDefault, created.IsSystem, created.CreatedOn);
    }
}
