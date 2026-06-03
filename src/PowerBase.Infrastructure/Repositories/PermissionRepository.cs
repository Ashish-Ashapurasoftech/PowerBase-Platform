using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class PermissionRepository : ControlRepositoryBase, IPermissionRepository
{
    private const string GetAllSql = """
        SELECT Id, Code, DisplayName, Description
        FROM meta.Permission
        ORDER BY Id
        """;

    private const string GetByRoleIdSql = """
        SELECT p.Id, p.Code, p.DisplayName, p.Description
        FROM meta.Permission p
        JOIN meta.RolePermission rp ON rp.PermissionId = p.Id
        WHERE rp.TenantRoleId = @roleId
        ORDER BY p.Id
        """;

    private const string InsertRolePermissionSql = """
        INSERT INTO meta.RolePermission (TenantRoleId, PermissionId)
        VALUES (@roleId, @permissionId)
        """;

    private const string DeleteRolePermissionsSql = """
        DELETE FROM meta.RolePermission WHERE TenantRoleId = @roleId
        """;

    public PermissionRepository(IControlConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<IReadOnlyList<Permission>> GetAllAsync(CancellationToken ct = default)
    {
        await using var connection = await OpenNewConnectionAsync(ct);
        var results = await connection.QueryAsync<Permission>(
            new CommandDefinition(GetAllSql, cancellationToken: ct));
        return results.ToList();
    }

    public async Task<IReadOnlyList<Permission>> GetByRoleIdAsync(long roleId, CancellationToken ct = default)
    {
        await using var connection = await OpenNewConnectionAsync(ct);
        var results = await connection.QueryAsync<Permission>(
            new CommandDefinition(GetByRoleIdSql, new { roleId }, cancellationToken: ct));
        return results.ToList();
    }

    public async Task AssignToRoleAsync(long roleId, IReadOnlyList<long> permissionIds, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        if (transaction is not null)
        {
            foreach (var permId in permissionIds)
                await transaction.Connection!.ExecuteAsync(
                    new CommandDefinition(InsertRolePermissionSql, new { roleId, permissionId = permId }, transaction, cancellationToken: ct));
            return;
        }

        await using var connection = await OpenNewConnectionAsync(ct);
        foreach (var permId in permissionIds)
            await connection.ExecuteAsync(
                new CommandDefinition(InsertRolePermissionSql, new { roleId, permissionId = permId }, cancellationToken: ct));
    }

    public async Task ReplaceRolePermissionsAsync(long roleId, IReadOnlyList<long> permissionIds, CancellationToken ct = default)
    {
        await using var connection = await OpenNewConnectionAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await connection.ExecuteAsync(
                new CommandDefinition(DeleteRolePermissionsSql, new { roleId }, transaction, cancellationToken: ct));

            foreach (var permId in permissionIds)
            {
                await connection.ExecuteAsync(
                    new CommandDefinition(InsertRolePermissionSql, new { roleId, permissionId = permId }, transaction, cancellationToken: ct));
            }

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }
}
