using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class ReportRepository : BaseRepository, IReportRepository
{
    private const string SelectColumns = """
        r.Id, r.PublicId, r.TenantId, r.AppTableId, r.OwnerId, r.Name, r.Description,
        r.ReportType, r.Visibility, r.Definition, r.IsDefault, r.DisplayOrder,
        r.IsDeleted, r.CreatedOn, r.CreatedBy, r.ModifiedOn, r.ModifiedBy, r.ViewEditFormId,
        f.PublicId AS ViewEditFormPublicId
        """;

    private const string GetByPublicIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.Report r
        LEFT JOIN meta.Form f ON f.Id = r.ViewEditFormId
        WHERE r.TenantId = @tenantId
          AND r.PublicId = @publicId
          AND r.IsDeleted = 0
        """;

    private const string GetAppIdByPublicIdSql = """
        SELECT t.AppId
        FROM meta.Report r
        JOIN meta.AppTable t ON t.Id = r.AppTableId
        WHERE r.TenantId = @tenantId AND r.PublicId = @publicId AND r.IsDeleted = 0
        """;

    private const string ListByTableSql = $"""
        SELECT {SelectColumns}
        FROM meta.Report r
        LEFT JOIN meta.Form f ON f.Id = r.ViewEditFormId
        WHERE r.TenantId = @tenantId
          AND r.AppTableId = (SELECT Id FROM meta.AppTable
                              WHERE PublicId = @tablePublicId
                                AND TenantId = @tenantId
                                AND IsDeleted = 0)
          AND r.IsDeleted = 0
          AND (
              r.Visibility = 'Shared'
              OR (r.Visibility = 'Personal' AND r.OwnerId = @userId)
              OR (r.Visibility IN ('MyRole', 'SpecificRoles', 'Role') AND EXISTS (
                  SELECT 1 FROM meta.AppRoleReport arr
                  JOIN meta.AppUser au ON au.AppRoleId = arr.AppRoleId
                  WHERE arr.ReportId = r.Id AND au.UserId = @userId AND au.TenantId = @tenantId AND au.IsDeleted = 0
              ))
          )
        ORDER BY r.DisplayOrder, r.Name
        """;

    private const string ListByAppSql = $"""
        SELECT {SelectColumns}
        FROM meta.Report r
        JOIN meta.AppTable t ON t.Id = r.AppTableId
        LEFT JOIN meta.Form f ON f.Id = r.ViewEditFormId
        WHERE r.TenantId = @tenantId
          AND t.AppId = @appId
          AND r.IsDeleted = 0
          AND (
              r.Visibility = 'Shared'
              OR (r.Visibility = 'Personal' AND r.OwnerId = @userId)
              OR (r.Visibility IN ('MyRole', 'SpecificRoles', 'Role') AND EXISTS (
                  SELECT 1 FROM meta.AppRoleReport arr
                  JOIN meta.AppUser au ON au.AppRoleId = arr.AppRoleId
                  WHERE arr.ReportId = r.Id AND au.UserId = @userId AND au.TenantId = @tenantId AND au.IsDeleted = 0
              ))
          )
        ORDER BY r.DisplayOrder, r.Name
        """;

    private const string GetDefaultByTableSql = $"""
        SELECT {SelectColumns}
        FROM meta.Report r
        LEFT JOIN meta.Form f ON f.Id = r.ViewEditFormId
        WHERE r.TenantId = @tenantId
          AND r.AppTableId = (SELECT Id FROM meta.AppTable
                              WHERE PublicId = @tablePublicId
                                AND TenantId = @tenantId
                                AND IsDeleted = 0)
          AND r.IsDefault = 1
          AND r.IsDeleted = 0
        """;

    private const string BelongsToTableSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1
            FROM meta.Report r
            JOIN meta.AppTable t ON t.Id = r.AppTableId
            WHERE r.TenantId = @tenantId
              AND t.TenantId = @tenantId
              AND t.PublicId = @tablePublicId
              AND r.PublicId = @reportPublicId
              AND r.IsDeleted = 0
              AND t.IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string InsertSql = """
        INSERT INTO meta.Report
            (TenantId, AppTableId, OwnerId, Name, Description, ReportType, Visibility,
             Definition, IsDefault, DisplayOrder, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.Id, INSERTED.PublicId
        VALUES
            (@tenantId, @appTableId, @ownerId, @name, @description, @reportType, @visibility,
             @definition, @isDefault, @displayOrder, 0, SYSUTCDATETIME(), @createdBy)
        """;

    private const string UpdateReportSql = """
        UPDATE meta.Report
        SET Name = @name, Description = @description, Visibility = @visibility,
            Definition = @definition, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE TenantId = @tenantId AND PublicId = @publicId AND IsDeleted = 0
        """;

    private const string UnsetDefaultSql = """
        UPDATE meta.Report
        SET IsDefault = 0, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE AppTableId = (SELECT Id FROM meta.AppTable WHERE PublicId = @tablePublicId AND TenantId = @tenantId AND IsDeleted = 0)
          AND TenantId = @tenantId AND IsDeleted = 0
        """;

    private const string SetDefaultSql = """
        UPDATE meta.Report
        SET IsDefault = 1, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE TenantId = @tenantId AND PublicId = @reportPublicId AND IsDeleted = 0
        """;

    private const string SoftDeleteReportSql = """
        UPDATE meta.Report
        SET IsDeleted = 1, DeletedOn = SYSUTCDATETIME(), DeletedBy = @deletedBy
        WHERE TenantId = @tenantId AND PublicId = @publicId AND IsDeleted = 0
        """;

    private const string UpdateReportFormOverrideSql = """
        UPDATE r
        SET r.ViewEditFormId = (SELECT Id FROM meta.Form WHERE PublicId = @viewEditFormPublicId AND TenantId = @tenantId AND IsDeleted = 0),
            r.ModifiedOn = SYSUTCDATETIME(),
            r.ModifiedBy = @modifiedBy
        FROM meta.Report r
        WHERE r.TenantId = @tenantId AND r.PublicId = @reportPublicId AND r.IsDeleted = 0
        """;

    private const string ClearReportFormOverrideSql = """
        UPDATE r
        SET r.ViewEditFormId = NULL,
            r.ModifiedOn = SYSUTCDATETIME(),
            r.ModifiedBy = @modifiedBy
        FROM meta.Report r
        WHERE r.TenantId = @tenantId AND r.PublicId = @reportPublicId AND r.IsDeleted = 0
        """;


    public ReportRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<Report> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var report = await connection.QuerySingleOrDefaultAsync<Report>(
            new CommandDefinition(GetByPublicIdSql,
                new { tenantId = QueryContext.TenantId, publicId },
                cancellationToken: ct));
        return report ?? throw new NotFoundException("Report", publicId);
    }

    public async Task<long> GetAppIdByPublicIdAsync(Guid reportPublicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var appId = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(GetAppIdByPublicIdSql, new { tenantId = QueryContext.TenantId, publicId = reportPublicId }, cancellationToken: ct));
        return appId ?? throw new NotFoundException("Report", reportPublicId);
    }

    public async Task<IReadOnlyList<Report>> ListByAppAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var results = await connection.QueryAsync<Report>(
            new CommandDefinition(ListByAppSql,
                new { tenantId = QueryContext.TenantId, appId, userId = QueryContext.UserId },
                cancellationToken: ct));
        return results.AsList();
    }

    public async Task<IReadOnlyList<Report>> ListByTableAsync(Guid tablePublicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var results = await connection.QueryAsync<Report>(
            new CommandDefinition(ListByTableSql,
                new { tenantId = QueryContext.TenantId, tablePublicId, userId = QueryContext.UserId },
                cancellationToken: ct));
        return results.AsList();
    }

    public async Task<Report?> GetDefaultByTableAsync(Guid tablePublicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<Report>(
            new CommandDefinition(GetDefaultByTableSql,
                new { tenantId = QueryContext.TenantId, tablePublicId },
                cancellationToken: ct));
    }

    public async Task<bool> BelongsToTableAsync(Guid tablePublicId, Guid reportPublicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(BelongsToTableSql,
                new { tenantId = QueryContext.TenantId, tablePublicId, reportPublicId },
                cancellationToken: ct));
    }

    public async Task<(long Id, Guid PublicId)> CreateAsync(Report report, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var row = await connection.QuerySingleAsync(
            new CommandDefinition(InsertSql, new
            {
                tenantId = report.TenantId,
                appTableId = report.AppTableId,
                ownerId = report.OwnerId,
                name = report.Name,
                description = report.Description,
                reportType = report.ReportType,
                visibility = report.Visibility,
                definition = report.Definition,
                isDefault = report.IsDefault,
                displayOrder = report.DisplayOrder,
                createdBy = QueryContext.UserId,
            }, cancellationToken: ct));
        return ((long)row.Id, (Guid)row.PublicId);
    }

    public async Task<int> UpdateAsync(Guid publicId, string name, string? description,
        string visibility, string definition, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteAsync(
            new CommandDefinition(UpdateReportSql,
                new { tenantId = QueryContext.TenantId, publicId, name, description, visibility, definition, modifiedBy = QueryContext.UserId },
                cancellationToken: ct));
    }

    public async Task SetDefaultAsync(Guid tablePublicId, Guid reportPublicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await connection.ExecuteAsync(
                new CommandDefinition(UnsetDefaultSql,
                    new { tenantId = QueryContext.TenantId, tablePublicId, modifiedBy = QueryContext.UserId },
                    transaction: transaction, cancellationToken: ct));

            await connection.ExecuteAsync(
                new CommandDefinition(SetDefaultSql,
                    new { tenantId = QueryContext.TenantId, reportPublicId, modifiedBy = QueryContext.UserId },
                    transaction: transaction, cancellationToken: ct));

            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<int> DeleteAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteReportSql, new { tenantId = QueryContext.TenantId, publicId, deletedBy = QueryContext.UserId }, cancellationToken: ct));
    }

    public async Task UpdateFormOverridesAsync(Guid tablePublicId, IEnumerable<(Guid ReportPublicId, Guid? ViewEditFormPublicId)> overrides, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            foreach (var (reportPublicId, viewEditFormPublicId) in overrides)
            {
                if (viewEditFormPublicId.HasValue)
                {
                    await connection.ExecuteAsync(
                        new CommandDefinition(UpdateReportFormOverrideSql,
                            new { tenantId = QueryContext.TenantId, reportPublicId, viewEditFormPublicId, modifiedBy = QueryContext.UserId },
                            transaction: transaction, cancellationToken: ct));
                }
                else
                {
                    await connection.ExecuteAsync(
                        new CommandDefinition(ClearReportFormOverrideSql,
                            new { tenantId = QueryContext.TenantId, reportPublicId, modifiedBy = QueryContext.UserId },
                            transaction: transaction, cancellationToken: ct));
                }
            }
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task SetReportRolesAsync(long reportId, IEnumerable<long> roleIds, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.OpenAsync(ct);
        await using var transaction = await connection.BeginTransactionAsync(ct);
        try
        {
            await connection.ExecuteAsync(
                new CommandDefinition("DELETE FROM meta.AppRoleReport WHERE ReportId = @reportId",
                    new { reportId }, transaction: transaction, cancellationToken: ct));

            if (roleIds.Any())
            {
                var parameters = roleIds.Select(roleId => new { reportId, roleId }).ToList();
                await connection.ExecuteAsync(
                    new CommandDefinition("INSERT INTO meta.AppRoleReport (ReportId, AppRoleId) VALUES (@reportId, @roleId)",
                        parameters, transaction: transaction, cancellationToken: ct));
            }
            
            await transaction.CommitAsync(ct);
        }
        catch
        {
            await transaction.RollbackAsync(ct);
            throw;
        }
    }

    public async Task<IReadOnlyList<long>> GetReportRoleIdsAsync(long reportId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var results = await connection.QueryAsync<long>(
            new CommandDefinition("SELECT AppRoleId FROM meta.AppRoleReport WHERE ReportId = @reportId",
                new { reportId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<Dictionary<long, List<long>>> GetAppRoleReportsMapAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var sql = """
            SELECT arr.ReportId, arr.AppRoleId 
            FROM meta.AppRoleReport arr
            JOIN meta.Report r ON r.Id = arr.ReportId
            JOIN meta.AppTable t ON t.Id = r.AppTableId
            WHERE t.AppId = @appId AND r.IsDeleted = 0 AND r.TenantId = @tenantId
            """;
        var results = await connection.QueryAsync<(long ReportId, long AppRoleId)>(
            new CommandDefinition(sql, new { appId, tenantId = QueryContext.TenantId }, cancellationToken: ct));
        
        return results.GroupBy(x => x.ReportId)
                      .ToDictionary(g => g.Key, g => g.Select(x => x.AppRoleId).ToList());
    }
}
