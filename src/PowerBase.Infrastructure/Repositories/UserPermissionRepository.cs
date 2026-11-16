using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class UserPermissionRepository : ControlRepositoryBase, IUserPermissionRepository
{
    private const string GetPermissionsSql = """
        SELECT p.Code
        FROM meta.TenantUser tu
        JOIN meta.RolePermission rp ON rp.TenantRoleId = tu.TenantRoleId
        JOIN meta.Permission p      ON p.Id = rp.PermissionId
        WHERE tu.UserId   = @userId
          AND tu.TenantId = @tenantId
          AND tu.IsActive = 1
          AND tu.IsDeleted = 0
        """;

    private readonly IMetadataCache _cache;

    public UserPermissionRepository(IControlConnectionFactory connectionFactory, IQueryContext queryContext, IMetadataCache cache)
        : base(connectionFactory, queryContext)
    {
        _cache = cache;
    }

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(long userId, long tenantId, CancellationToken ct = default)
    {
        // Asked on every request by the auth middleware. Dropped by the control-database writes that change a user's role or a role's
        // permissions (TenantRepository, AdminRepository, PermissionRepository); each caller gets its own copy.
        var cached = await _cache.GetOrAddAsync<IReadOnlySet<string>>(MetadataCacheGroups.GlobalTenant, MetadataCacheGroups.UserPermissions, $"{userId}:{tenantId}", async () =>
        {
            await using var connection = ConnectionFactory.Create();
            var codes = await connection.QueryAsync<string>(
                new CommandDefinition(GetPermissionsSql, new { userId, tenantId }, cancellationToken: ct));
            return codes.ToHashSet();
        });
        return cached.ToHashSet();
    }
}
