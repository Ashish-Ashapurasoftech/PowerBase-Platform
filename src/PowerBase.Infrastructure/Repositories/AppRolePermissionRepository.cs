using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppRolePermissionRepository : TenantRepositoryBase, IAppRolePermissionRepository
{
    public AppRolePermissionRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    // ── Table-level ──────────────────────────────────────────────────────────

    private const string GetTablePermissionsSql = """
        SELECT t.PublicId AS TablePublicId, t.Name AS TableName,
               tp.ViewScope, tp.ModifyScope, tp.CanAdd, tp.CanDelete,
               tp.CanSaveSharedReports, tp.CanEditFieldProperties, tp.FieldAccessLevel
        FROM meta.AppRoleTablePermission tp
        JOIN meta.AppTable t ON t.Id = tp.AppTableId AND t.IsDeleted = 0
        WHERE tp.AppRoleId = @appRoleId
        """;

    private const string DeleteTablePermissionsSql = """
        DELETE FROM meta.AppRoleTablePermission WHERE AppRoleId = @appRoleId
        """;

    private const string InsertTablePermissionSql = """
        INSERT INTO meta.AppRoleTablePermission
            (AppRoleId, AppTableId, ViewScope, ModifyScope, CanAdd, CanDelete,
             CanSaveSharedReports, CanEditFieldProperties, FieldAccessLevel)
        VALUES
            (@AppRoleId, @AppTableId, @ViewScope, @ModifyScope, @CanAdd, @CanDelete,
             @CanSaveSharedReports, @CanEditFieldProperties, @FieldAccessLevel)
        """;

    private const string GetTablePermissionSql = """
        SELECT Id, AppRoleId, AppTableId, ViewScope, ModifyScope, CanAdd, CanDelete,
               CanSaveSharedReports, CanEditFieldProperties, FieldAccessLevel
        FROM meta.AppRoleTablePermission
        WHERE AppRoleId = @appRoleId AND AppTableId = @appTableId
        """;

    public async Task<IReadOnlyList<TablePermissionRow>> GetTablePermissionsAsync(long appRoleId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        var rows = await conn.QueryAsync<TablePermissionRow>(
            new CommandDefinition(GetTablePermissionsSql, new { appRoleId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task SetTablePermissionsAsync(long appRoleId, IReadOnlyList<AppRoleTablePermission> rows, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        async Task Exec(IDbConnection conn, IDbTransaction? tx)
        {
            await conn.ExecuteAsync(new CommandDefinition(DeleteTablePermissionsSql, new { appRoleId }, tx, cancellationToken: ct));
            foreach (var r in rows)
            {
                await conn.ExecuteAsync(new CommandDefinition(InsertTablePermissionSql, new
                {
                    AppRoleId = appRoleId, r.AppTableId, r.ViewScope, r.ModifyScope, r.CanAdd, r.CanDelete,
                    r.CanSaveSharedReports, r.CanEditFieldProperties, r.FieldAccessLevel,
                }, tx, cancellationToken: ct));
            }
        }

        if (transaction is not null) { await Exec(transaction.Connection!, transaction); return; }
        await using var c = await ConnectionFactory.CreateAsync(ct);
        await Exec(c, null);
    }

    public async Task<AppRoleTablePermission?> GetTablePermissionAsync(long appRoleId, long appTableId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<AppRoleTablePermission>(
            new CommandDefinition(GetTablePermissionSql, new { appRoleId, appTableId }, cancellationToken: ct));
    }

    // ── Field-level ──────────────────────────────────────────────────────────

    private const string GetFieldPermissionsSql = """
        SELECT f.PublicId AS FieldPublicId, COALESCE(NULLIF(f.Label, ''), f.Name) AS FieldName, fp.Access
        FROM meta.AppRoleFieldPermission fp
        JOIN meta.AppField f ON f.Id = fp.AppFieldId AND f.IsDeleted = 0
        WHERE fp.AppRoleId = @appRoleId AND f.AppTableId = @appTableId
        """;

    private const string DeleteFieldPermissionsSql = """
        DELETE FROM meta.AppRoleFieldPermission
        WHERE AppRoleId = @appRoleId
          AND AppFieldId IN (SELECT Id FROM meta.AppField WHERE AppTableId = @appTableId)
        """;

    private const string InsertFieldPermissionSql = """
        INSERT INTO meta.AppRoleFieldPermission (AppRoleId, AppFieldId, Access)
        VALUES (@AppRoleId, @AppFieldId, @Access)
        """;

    private const string GetFieldAccessMapSql = """
        SELECT fp.AppFieldId, fp.Access
        FROM meta.AppRoleFieldPermission fp
        JOIN meta.AppField f ON f.Id = fp.AppFieldId AND f.IsDeleted = 0
        WHERE fp.AppRoleId = @appRoleId AND f.AppTableId = @appTableId
        """;

    public async Task<IReadOnlyList<FieldPermissionRow>> GetFieldPermissionsAsync(long appRoleId, long appTableId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        var rows = await conn.QueryAsync<FieldPermissionRow>(
            new CommandDefinition(GetFieldPermissionsSql, new { appRoleId, appTableId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task SetFieldPermissionsAsync(long appRoleId, long appTableId, IReadOnlyList<AppRoleFieldPermission> rows, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        async Task Exec(IDbConnection conn, IDbTransaction? tx)
        {
            await conn.ExecuteAsync(new CommandDefinition(DeleteFieldPermissionsSql, new { appRoleId, appTableId }, tx, cancellationToken: ct));
            foreach (var r in rows)
            {
                await conn.ExecuteAsync(new CommandDefinition(InsertFieldPermissionSql, new
                {
                    AppRoleId = appRoleId, r.AppFieldId, r.Access,
                }, tx, cancellationToken: ct));
            }
        }

        if (transaction is not null) { await Exec(transaction.Connection!, transaction); return; }
        await using var c = await ConnectionFactory.CreateAsync(ct);
        await Exec(c, null);
    }

    public async Task<IReadOnlyDictionary<long, string>> GetFieldAccessMapAsync(long appRoleId, long appTableId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        var rows = await conn.QueryAsync<(long AppFieldId, string Access)>(
            new CommandDefinition(GetFieldAccessMapSql, new { appRoleId, appTableId }, cancellationToken: ct));
        return rows.ToDictionary(r => r.AppFieldId, r => r.Access);
    }

    private const string GetAllFieldPermissionsSql = """
        SELECT t.PublicId AS TablePublicId, f.PublicId AS FieldPublicId, fp.Access
        FROM meta.AppRoleFieldPermission fp
        JOIN meta.AppField f ON f.Id = fp.AppFieldId AND f.IsDeleted = 0
        JOIN meta.AppTable t ON t.Id = f.AppTableId AND t.IsDeleted = 0
        WHERE fp.AppRoleId = @appRoleId
        """;

    public async Task<IReadOnlyList<FieldPermissionScopedRow>> GetAllFieldPermissionsAsync(long appRoleId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        var rows = await conn.QueryAsync<FieldPermissionScopedRow>(
            new CommandDefinition(GetAllFieldPermissionsSql, new { appRoleId }, cancellationToken: ct));
        return rows.AsList();
    }

    private const string CountRolesWithNoneAccessForFieldSql = """
        SELECT COUNT(*) FROM meta.AppRoleFieldPermission WHERE AppFieldId = @appFieldId AND Access = 'None'
        """;

    public async Task<int> CountRolesWithNoneAccessForFieldAsync(long appFieldId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.ExecuteScalarAsync<int>(
            new CommandDefinition(CountRolesWithNoneAccessForFieldSql, new { appFieldId }, cancellationToken: ct));
    }

    // ── Record-level row filters ───────────────────────────────────────────────

    private const string GetRecordFiltersSql = """
        SELECT t.PublicId AS TablePublicId, rf.Conjunction, rf.FilterJson
        FROM meta.AppRoleRecordFilter rf
        JOIN meta.AppTable t ON t.Id = rf.AppTableId AND t.IsDeleted = 0
        WHERE rf.AppRoleId = @appRoleId
        """;

    private const string DeleteRecordFiltersSql = """
        DELETE FROM meta.AppRoleRecordFilter WHERE AppRoleId = @appRoleId
        """;

    private const string InsertRecordFilterSql = """
        INSERT INTO meta.AppRoleRecordFilter (AppRoleId, AppTableId, Conjunction, FilterJson)
        VALUES (@AppRoleId, @AppTableId, @Conjunction, @FilterJson)
        """;

    private const string GetRecordFilterSql = """
        SELECT Id, AppRoleId, AppTableId, Conjunction, FilterJson
        FROM meta.AppRoleRecordFilter
        WHERE AppRoleId = @appRoleId AND AppTableId = @appTableId
        """;

    public async Task<IReadOnlyList<RecordFilterRow>> GetRecordFiltersAsync(long appRoleId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        var rows = await conn.QueryAsync<RecordFilterRow>(
            new CommandDefinition(GetRecordFiltersSql, new { appRoleId }, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task SetRecordFiltersAsync(long appRoleId, IReadOnlyList<AppRoleRecordFilter> rows, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        async Task Exec(IDbConnection conn, IDbTransaction? tx)
        {
            await conn.ExecuteAsync(new CommandDefinition(DeleteRecordFiltersSql, new { appRoleId }, tx, cancellationToken: ct));
            foreach (var r in rows)
            {
                await conn.ExecuteAsync(new CommandDefinition(InsertRecordFilterSql, new
                {
                    AppRoleId = appRoleId, r.AppTableId, r.Conjunction, r.FilterJson,
                }, tx, cancellationToken: ct));
            }
        }

        if (transaction is not null) { await Exec(transaction.Connection!, transaction); return; }
        await using var c = await ConnectionFactory.CreateAsync(ct);
        await Exec(c, null);
    }

    public async Task<AppRoleRecordFilter?> GetRecordFilterAsync(long appRoleId, long appTableId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<AppRoleRecordFilter>(
            new CommandDefinition(GetRecordFilterSql, new { appRoleId, appTableId }, cancellationToken: ct));
    }

    // ── Seeding helpers ──────────────────────────────────────────────────────────

    private const string SeedDefaultsForRoleSql = """
        INSERT INTO meta.AppRoleTablePermission
            (AppRoleId, AppTableId, ViewScope, ModifyScope, CanAdd, CanDelete,
             CanSaveSharedReports, CanEditFieldProperties, FieldAccessLevel)
        SELECT @appRoleId, t.Id,
               'AllRecords', 
               CASE WHEN r.Name = 'Administrator' THEN 'AllRecords' ELSE 'None' END, 
               CASE WHEN r.Name = 'Administrator' THEN 1 ELSE 0 END, 
               CASE WHEN r.Name = 'Administrator' THEN 1 ELSE 0 END, 
               CASE WHEN r.Name = 'Administrator' THEN 1 ELSE 0 END, 
               CASE WHEN r.Name = 'Administrator' THEN 1 ELSE 0 END, 
               'FullAccess'
        FROM meta.AppTable t
        JOIN meta.AppRole r ON r.Id = @appRoleId
        WHERE t.AppId = @appId
          AND t.IsDeleted = 0
          AND NOT EXISTS (
              SELECT 1 FROM meta.AppRoleTablePermission x
              WHERE x.AppRoleId = @appRoleId AND x.AppTableId = t.Id
          )
        """;

    private const string SeedDefaultsForTableSql = """
        INSERT INTO meta.AppRoleTablePermission
            (AppRoleId, AppTableId, ViewScope, ModifyScope, CanAdd, CanDelete,
             CanSaveSharedReports, CanEditFieldProperties, FieldAccessLevel)
        SELECT r.Id, @appTableId,
               'AllRecords', 
               CASE WHEN r.Name = 'Administrator' THEN 'AllRecords' ELSE 'None' END, 
               CASE WHEN r.Name = 'Administrator' THEN 1 ELSE 0 END, 
               CASE WHEN r.Name = 'Administrator' THEN 1 ELSE 0 END, 
               CASE WHEN r.Name = 'Administrator' THEN 1 ELSE 0 END, 
               CASE WHEN r.Name = 'Administrator' THEN 1 ELSE 0 END, 
               'FullAccess'
        FROM meta.AppRole r
        WHERE r.AppId = @appId
          AND r.IsDeleted = 0
          AND NOT EXISTS (
              SELECT 1 FROM meta.AppRoleTablePermission x
              WHERE x.AppRoleId = r.Id AND x.AppTableId = @appTableId
          )
        """;

    public async Task SeedDefaultsForRoleAsync(long appRoleId, long appId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(SeedDefaultsForRoleSql, new { appRoleId, appId }, cancellationToken: ct));
    }

    public async Task SeedDefaultsForTableAsync(long appTableId, long appId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(SeedDefaultsForTableSql, new { appTableId, appId }, cancellationToken: ct));
    }
}
