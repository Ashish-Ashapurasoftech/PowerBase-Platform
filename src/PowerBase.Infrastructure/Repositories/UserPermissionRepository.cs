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

    public UserPermissionRepository(IControlConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<IReadOnlySet<string>> GetPermissionsAsync(long userId, long tenantId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var codes = await connection.QueryAsync<string>(
            new CommandDefinition(GetPermissionsSql, new { userId, tenantId }, cancellationToken: ct));

        return codes.ToHashSet();
    }
}
