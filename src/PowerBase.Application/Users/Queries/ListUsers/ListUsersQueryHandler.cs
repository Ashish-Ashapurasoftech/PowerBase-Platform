using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Users.Queries.ListUsers;

public class ListUsersQueryHandler
{
    private readonly ITenantRepository _tenantRepo;

    public ListUsersQueryHandler(ITenantRepository tenantRepo) => _tenantRepo = tenantRepo;

    public Task<IReadOnlyList<TenantUserDetail>> HandleAsync(ListUsersQuery query, CancellationToken ct = default)
        => _tenantRepo.ListUsersAsync(query.SearchTerm, query.RoleName, query.Status, ct);
}
