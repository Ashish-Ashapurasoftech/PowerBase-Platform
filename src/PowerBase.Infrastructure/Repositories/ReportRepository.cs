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
        r.IsDeleted, r.CreatedOn, r.CreatedBy, r.ModifiedOn, r.ModifiedBy
        """;

    private const string GetByPublicIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.Report r
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

    private const string ListByAppSql = $"""
        SELECT {SelectColumns}
        FROM meta.Report r
        JOIN meta.AppTable t ON t.Id = r.AppTableId
        WHERE r.TenantId = @tenantId
          AND t.AppId = @appId
          AND r.IsDeleted = 0
        ORDER BY r.DisplayOrder, r.Name
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

    private const string UpdateSql = """
        UPDATE meta.Report
        SET Name        = @name,
            Description = @description,
            Visibility  = @visibility,
            Definition  = @definition,
            ModifiedOn  = SYSUTCDATETIME(),
            ModifiedBy  = @modifiedBy
        WHERE TenantId = @tenantId
          AND PublicId = @publicId
          AND IsDeleted = 0
        """;

    private const string SoftDeleteSql = """
        UPDATE meta.Report
        SET IsDeleted  = 1,
            ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy,
            DeletedOn  = SYSUTCDATETIME(), DeletedBy  = @modifiedBy
        WHERE TenantId = @tenantId
          AND PublicId = @publicId
          AND IsDeleted = 0
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
                new { tenantId = QueryContext.TenantId, appId },
                cancellationToken: ct));
        return results.AsList();
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

    public async Task UpdateAsync(Report report, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(UpdateSql, new
            {
                tenantId = QueryContext.TenantId,
                publicId = report.PublicId,
                name = report.Name,
                description = report.Description,
                visibility = report.Visibility,
                definition = report.Definition,
                modifiedBy = QueryContext.UserId,
            }, cancellationToken: ct));
        if (affected == 0)
            throw new NotFoundException("Report", report.PublicId);
    }

    public async Task DeleteAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteSql, new { tenantId = QueryContext.TenantId, publicId, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
        if (affected == 0)
            throw new NotFoundException("Report", publicId);
    }
}
