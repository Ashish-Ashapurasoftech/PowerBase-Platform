using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Roles.Queries.ListRoles;

public class ListRolesQueryHandler
{
    private readonly ITenantRepository _tenantRepo;

    public ListRolesQueryHandler(ITenantRepository tenantRepo) => _tenantRepo = tenantRepo;

    public async Task<IReadOnlyList<RoleResult>> HandleAsync(ListRolesQuery query, CancellationToken ct = default)
    {
        var roles = await _tenantRepo.ListRolesAsync(ct);
        return roles.Select(r => new RoleResult(r.PublicId, r.Name, r.Description, r.IsDefault, r.IsSystem, r.CreatedOn)).ToList();
    }
}
