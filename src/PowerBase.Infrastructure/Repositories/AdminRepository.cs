using Dapper;
using PowerBase.Application.Admin;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AdminRepository : ControlRepositoryBase, IAdminRepository
{
    public AdminRepository(IControlConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    // ── Tenants ──────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdminTenantDto>> ListTenantsAsync(int page, int pageSize, string? search, CancellationToken ct = default)
    {
        var where = string.IsNullOrWhiteSpace(search)
            ? "WHERE t.IsDeleted = 0"
            : "WHERE t.IsDeleted = 0 AND (t.Name LIKE @search OR t.Slug LIKE @search)";

        var sql = $"""
            SELECT t.Id, t.PublicId, t.Name, t.Slug, t.Status,
                   ISNULL(t.ProvisioningState, 'Ready') AS ProvisioningState,
                   t.DatabaseName, ISNULL(t.SchemaVersion, 0) AS SchemaVersion,
                   COUNT(tu.Id) AS MemberCount,
                   t.CreatedOn
            FROM meta.Tenant t
            LEFT JOIN meta.TenantUser tu ON tu.TenantId = t.Id AND tu.IsDeleted = 0
            {where}
            GROUP BY t.Id, t.PublicId, t.Name, t.Slug, t.Status,
                     t.ProvisioningState, t.DatabaseName, t.SchemaVersion, t.CreatedOn
            ORDER BY t.CreatedOn DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        await using var conn = await OpenNewConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdminTenantDto>(new CommandDefinition(sql,
            new { search = $"%{search}%", offset = (page - 1) * pageSize, pageSize }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<int> CountTenantsAsync(string? search, CancellationToken ct = default)
    {
        var where = string.IsNullOrWhiteSpace(search)
            ? "WHERE IsDeleted = 0"
            : "WHERE IsDeleted = 0 AND (Name LIKE @search OR Slug LIKE @search)";
        var sql = $"SELECT COUNT(1) FROM meta.Tenant {where}";
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql,
            new { search = $"%{search}%" }, cancellationToken: ct));
    }

    public async Task<long?> GetTenantIdByPublicIdAsync(Guid tenantPublicId, CancellationToken ct = default)
    {
        const string sql = "SELECT Id FROM meta.Tenant WHERE PublicId = @tenantPublicId AND IsDeleted = 0";
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(sql, new { tenantPublicId }, cancellationToken: ct));
    }

    public async Task UpdateTenantStatusAsync(long tenantId, string status, CancellationToken ct = default)
    {
        const string sql = "UPDATE meta.Tenant SET Status = @status, ModifiedOn = SYSUTCDATETIME() WHERE Id = @tenantId";
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { tenantId, status }, cancellationToken: ct));
    }

    // ── Users ─────────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdminUserDto>> ListUsersAsync(int page, int pageSize, string? search, CancellationToken ct = default)
    {
        var where = string.IsNullOrWhiteSpace(search)
            ? "WHERE u.IsDeleted = 0"
            : "WHERE u.IsDeleted = 0 AND (u.Name LIKE @search OR u.Email LIKE @search)";

        var sql = $"""
            SELECT u.Id, u.PublicId, u.Email, u.Name, u.IsActive, u.IsEmailVerified,
                   sr.Code AS SystemRoleCode,
                   COUNT(DISTINCT tu.TenantId) AS TenantCount,
                   u.CreatedOn, u.LastLoginOn
            FROM core.[User] u
            LEFT JOIN core.SystemRole sr ON sr.Id = u.SystemRoleId
            LEFT JOIN meta.TenantUser tu ON tu.UserId = u.Id AND tu.IsDeleted = 0
            {where}
            GROUP BY u.Id, u.PublicId, u.Email, u.Name, u.IsActive, u.IsEmailVerified,
                     sr.Code, u.CreatedOn, u.LastLoginOn
            ORDER BY u.CreatedOn DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        await using var conn = await OpenNewConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdminUserDto>(new CommandDefinition(sql,
            new { search = $"%{search}%", offset = (page - 1) * pageSize, pageSize }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<int> CountUsersAsync(string? search, CancellationToken ct = default)
    {
        var where = string.IsNullOrWhiteSpace(search)
            ? "WHERE IsDeleted = 0"
            : "WHERE IsDeleted = 0 AND (Name LIKE @search OR Email LIKE @search)";
        var sql = $"SELECT COUNT(1) FROM core.[User] {where}";
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(sql,
            new { search = $"%{search}%" }, cancellationToken: ct));
    }

    public async Task UpdateUserActiveStateAsync(Guid userPublicId, bool isActive, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE core.[User] SET IsActive = @isActive, ModifiedOn = SYSUTCDATETIME()
            WHERE PublicId = @userPublicId AND IsDeleted = 0
            """;
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userPublicId, isActive }, cancellationToken: ct));
    }

    public async Task UpdateUserSystemRoleAsync(Guid userPublicId, int? systemRoleId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE core.[User] SET SystemRoleId = @systemRoleId, ModifiedOn = SYSUTCDATETIME()
            WHERE PublicId = @userPublicId AND IsDeleted = 0
            """;
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { userPublicId, systemRoleId }, cancellationToken: ct));
    }

    // ── Tenant members ────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdminTenantMemberDto>> ListTenantMembersAsync(long tenantId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT u.PublicId AS UserPublicId, u.Email, u.Name, u.IsActive,
                   r.PublicId AS RolePublicId, r.Name AS RoleName, tu.IsOwner, tu.JoinedOn
            FROM meta.TenantUser tu
            JOIN core.[User] u ON u.Id = tu.UserId
            LEFT JOIN meta.TenantRole r ON r.Id = tu.TenantRoleId AND r.IsDeleted = 0
            WHERE tu.TenantId = @tenantId AND tu.IsDeleted = 0
            ORDER BY tu.IsOwner DESC, u.Name
            """;
        await using var conn = await OpenNewConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdminTenantMemberDto>(new CommandDefinition(sql, new { tenantId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<long?> GetUserIdByEmailAsync(string email, CancellationToken ct = default)
    {
        const string sql = "SELECT Id FROM core.[User] WHERE Email = @email AND IsDeleted = 0";
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(sql, new { email }, cancellationToken: ct));
    }

    public async Task AssignUserToTenantAsync(long tenantId, long userId, long roleId, long assignedBy, bool isActive = true, CancellationToken ct = default)
    {
        const string sql = """
            MERGE meta.TenantUser AS target
            USING (VALUES (@tenantId, @userId)) AS source (TenantId, UserId)
            ON target.TenantId = source.TenantId AND target.UserId = source.UserId
            WHEN MATCHED THEN
                UPDATE SET IsDeleted = 0, DeletedOn = NULL, DeletedBy = NULL,
                           TenantRoleId = @roleId, IsActive = @isActive,
                           InvitedBy = @assignedBy, ModifiedOn = SYSUTCDATETIME()
            WHEN NOT MATCHED THEN
                INSERT (TenantId, UserId, TenantRoleId, IsOwner, IsActive, IsDeleted, JoinedOn, InvitedBy, CreatedOn, CreatedBy)
                VALUES (@tenantId, @userId, @roleId, 0, @isActive, 0, SYSUTCDATETIME(), @assignedBy, SYSUTCDATETIME(), @assignedBy);
            """;
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { tenantId, userId, roleId, assignedBy, isActive }, cancellationToken: ct));
    }

    public async Task RemoveUserFromTenantAsync(long tenantId, Guid userPublicId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE tu SET tu.IsDeleted = 1, tu.DeletedOn = SYSUTCDATETIME()
            FROM meta.TenantUser tu
            JOIN core.[User] u ON u.Id = tu.UserId
            WHERE tu.TenantId = @tenantId AND u.PublicId = @userPublicId AND tu.IsDeleted = 0
            """;
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { tenantId, userPublicId }, cancellationToken: ct));
    }

    // ── Tenant roles ────────────────────────────────────────────────────────────

    public async Task<IReadOnlyList<AdminTenantRoleDto>> ListTenantRolesAsync(long tenantId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT PublicId, Name, IsDefault, IsSystem
            FROM meta.TenantRole
            WHERE TenantId = @tenantId AND IsDeleted = 0
            ORDER BY IsSystem DESC, Name
            """;
        await using var conn = await OpenNewConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdminTenantRoleDto>(new CommandDefinition(sql, new { tenantId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<long?> GetTenantRoleIdByPublicIdAsync(long tenantId, Guid rolePublicId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT Id FROM meta.TenantRole
            WHERE TenantId = @tenantId AND PublicId = @rolePublicId AND IsDeleted = 0
            """;
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition(sql, new { tenantId, rolePublicId }, cancellationToken: ct));
    }

    // ── New: status guard, assignable users, role change, permissions ─────────

    public async Task<string?> GetTenantStatusAsync(long tenantId, CancellationToken ct = default)
    {
        const string sql = "SELECT Status FROM meta.Tenant WHERE Id = @tenantId AND IsDeleted = 0";
        await using var conn = await OpenNewConnectionAsync(ct);
        return await conn.ExecuteScalarAsync<string?>(new CommandDefinition(sql, new { tenantId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AdminUserDto>> ListUsersNotInTenantAsync(long tenantId, string? search, CancellationToken ct = default)
    {
        var where = string.IsNullOrWhiteSpace(search)
            ? "WHERE u.IsDeleted = 0 AND NOT EXISTS (SELECT 1 FROM meta.TenantUser tu WHERE tu.UserId = u.Id AND tu.TenantId = @tenantId AND tu.IsDeleted = 0)"
            : "WHERE u.IsDeleted = 0 AND NOT EXISTS (SELECT 1 FROM meta.TenantUser tu WHERE tu.UserId = u.Id AND tu.TenantId = @tenantId AND tu.IsDeleted = 0) AND (u.Name LIKE @search OR u.Email LIKE @search)";

        var sql = $"""
            SELECT TOP 50 u.Id, u.PublicId, u.Email, u.Name, u.IsActive, u.IsEmailVerified,
                   sr.Code AS SystemRoleCode, 0 AS TenantCount, u.CreatedOn, u.LastLoginOn
            FROM core.[User] u
            LEFT JOIN core.SystemRole sr ON sr.Id = u.SystemRoleId
            {where}
            ORDER BY u.Name
            """;

        await using var conn = await OpenNewConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdminUserDto>(new CommandDefinition(sql,
            new { tenantId, search = $"%{search}%" }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task ChangeMemberRoleAsync(long tenantId, Guid userPublicId, long newRoleId, CancellationToken ct = default)
    {
        const string sql = """
            UPDATE tu SET tu.TenantRoleId = @newRoleId, tu.ModifiedOn = SYSUTCDATETIME()
            FROM meta.TenantUser tu
            JOIN core.[User] u ON u.Id = tu.UserId
            WHERE tu.TenantId = @tenantId AND u.PublicId = @userPublicId AND tu.IsDeleted = 0
            """;
        await using var conn = await OpenNewConnectionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(sql, new { tenantId, userPublicId, newRoleId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<AdminRolePermissionDto>> GetTenantRolePermissionsAsync(long tenantId, Guid rolePublicId, CancellationToken ct = default)
    {
        const string sql = """
            SELECT p.Code, p.DisplayName, p.Description
            FROM meta.Permission p
            JOIN meta.RolePermission rp ON rp.PermissionId = p.Id
            JOIN meta.TenantRole r ON r.Id = rp.TenantRoleId
            WHERE r.TenantId = @tenantId AND r.PublicId = @rolePublicId AND r.IsDeleted = 0
            ORDER BY p.Id
            """;
        await using var conn = await OpenNewConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdminRolePermissionDto>(new CommandDefinition(sql, new { tenantId, rolePublicId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task ReplaceRolePermissionsAdminAsync(long tenantId, Guid rolePublicId, IReadOnlyList<string> permissionCodes, CancellationToken ct = default)
    {
        var roleId = await GetTenantRoleIdByPublicIdAsync(tenantId, rolePublicId, ct)
            ?? throw new KeyNotFoundException($"Role {rolePublicId} not found in tenant {tenantId}.");

        await using var conn = await OpenNewConnectionAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "DELETE FROM meta.RolePermission WHERE TenantRoleId = @roleId",
                new { roleId }, tx, cancellationToken: ct));

            if (permissionCodes.Count > 0)
            {
                const string getPermSql = "SELECT Id FROM meta.Permission WHERE Code IN @codes";
                var permIds = (await conn.QueryAsync<long>(new CommandDefinition(getPermSql,
                    new { codes = permissionCodes }, tx, cancellationToken: ct))).AsList();

                foreach (var permId in permIds)
                {
                    await conn.ExecuteAsync(new CommandDefinition(
                        "INSERT INTO meta.RolePermission (TenantRoleId, PermissionId) VALUES (@roleId, @permId)",
                        new { roleId, permId }, tx, cancellationToken: ct));
                }
            }

            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<AdminPermissionDto>> ListAllPermissionsAsync(CancellationToken ct = default)
    {
        const string sql = "SELECT Code, DisplayName, Description FROM meta.Permission ORDER BY Id";
        await using var conn = await OpenNewConnectionAsync(ct);
        var rows = await conn.QueryAsync<AdminPermissionDto>(new CommandDefinition(sql, cancellationToken: ct));
        return rows.AsList();
    }

    // ── Audit ─────────────────────────────────────────────────────────────────

    public async Task<(IReadOnlyList<AdminLoginAttemptDto> Items, int Total)> ListLoginAttemptsAsync(
        int page, int pageSize, string? email, CancellationToken ct = default)
    {
        var where = string.IsNullOrWhiteSpace(email) ? "" : "WHERE EmailAttempted LIKE @email";

        var countSql = $"SELECT COUNT(1) FROM audit.LoginAttempt {where}";
        var dataSql = $"""
            SELECT Id, EmailAttempted, IpAddress, WasSuccessful, FailureReason, AttemptedOn
            FROM audit.LoginAttempt
            {where}
            ORDER BY AttemptedOn DESC
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        var p = new { email = $"%{email}%", offset = (page - 1) * pageSize, pageSize };
        await using var conn = await OpenNewConnectionAsync(ct);
        var total = await conn.ExecuteScalarAsync<int>(new CommandDefinition(countSql, p, cancellationToken: ct));
        var items = (await conn.QueryAsync<AdminLoginAttemptDto>(new CommandDefinition(dataSql, p, cancellationToken: ct))).AsList();
        return (items, total);
    }
}
