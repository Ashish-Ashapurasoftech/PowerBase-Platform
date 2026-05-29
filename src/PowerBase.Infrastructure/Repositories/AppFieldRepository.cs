using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppFieldRepository : BaseRepository, IAppFieldRepository
{
    private const string SelectColumns = """
        af.Id, af.PublicId, af.TenantId, af.AppTableId, af.FieldTypeId, ft.Code AS TypeCode,
        af.Name, af.Label, af.Description, af.PhysicalColumnName, af.DefaultValue,
        af.IsRequired, af.IsSearchable, af.IsSortable, af.IsFilterable, af.IsReportable,
        af.IsUnique, af.IsSystem, af.Settings, af.IsDeleted, af.CreatedOn, af.CreatedBy
        """;

    private const string GetByIdInTableSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppField af
        JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
        WHERE af.TenantId    = @tenantId
          AND af.Id          = @fieldId
          AND af.AppTableId  = @tableId
          AND af.IsDeleted   = 0
        """;

    private const string ListByTableSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppField af
        JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
        WHERE af.TenantId = @tenantId
          AND af.AppTableId = @tableId
          AND af.IsDeleted = 0
        ORDER BY af.Id
        """;

    private const string NameExistsSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1 FROM meta.AppField
            WHERE TenantId = @tenantId AND AppTableId = @tableId AND Name = @name AND IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string InsertSql = """
        INSERT INTO meta.AppField
            (TenantId, AppTableId, FieldTypeId, Name, Label, Description, IsRequired,
             IsSystem, PhysicalColumnName, IsSearchable, IsSortable, IsFilterable, IsReportable,
             Settings, DisplayOrder, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.Id, INSERTED.PublicId
        VALUES (@tenantId, @tableId, @fieldTypeId, @name, @label, @description, @isRequired,
                @isSystem, @physicalColumnName, @isSearchable, @isSortable, @isFilterable, @isReportable,
                @settings, @displayOrder, 0, SYSUTCDATETIME(), @createdBy)
        """;

    private const string UpdatePhysicalColumnNameSql = """
        UPDATE meta.AppField SET PhysicalColumnName = @physicalColumnName WHERE Id = @id
        """;

    private const string GetByPublicIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppField af
        JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
        WHERE af.TenantId = @tenantId AND af.PublicId = @publicId AND af.IsDeleted = 0
        """;

    private const string UpdateFieldSql = """
        UPDATE meta.AppField
        SET Name = @name, Label = @label, Description = @description,
            IsRequired = @isRequired, DefaultValue = @defaultValue,
            IsSearchable = @isSearchable, IsSortable = @isSortable,
            IsFilterable = @isFilterable, IsReportable = @isReportable,
            Settings = @settings,
            ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE TenantId = @tenantId AND PublicId = @publicId AND AppTableId = @tableId AND IsSystem = 0 AND IsDeleted = 0
        """;

    private const string SoftDeleteFieldSql = """
        UPDATE meta.AppField
        SET IsDeleted = 1, DeletedOn = SYSUTCDATETIME(), DeletedBy = @deletedBy
        WHERE TenantId = @tenantId AND PublicId = @publicId AND AppTableId = @tableId AND IsSystem = 0 AND IsDeleted = 0
        """;

    public AppFieldRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<AppField> GetByIdInTableAsync(long fieldId, long tableId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var field = await connection.QuerySingleOrDefaultAsync<AppField>(
            new CommandDefinition(GetByIdInTableSql, new { tenantId = QueryContext.TenantId, fieldId, tableId }, cancellationToken: ct));
        return field ?? throw new NotFoundException("Field", fieldId);
    }

    public async Task<IReadOnlyList<AppField>> ListByTableAsync(long tableId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var results = await connection.QueryAsync<AppField>(
            new CommandDefinition(ListByTableSql, new { tenantId = QueryContext.TenantId, tableId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<bool> NameExistsInTableAsync(long tableId, string name, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(NameExistsSql, new { tenantId = QueryContext.TenantId, tableId, name }, cancellationToken: ct));
    }

    public async Task<(long Id, Guid PublicId)> CreateAsync(AppField field, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        var row = await connection.QuerySingleAsync(
            new CommandDefinition(InsertSql, new
            {
                tenantId = QueryContext.TenantId,
                tableId = field.AppTableId,
                fieldTypeId = field.FieldTypeId,
                name = field.Name,
                label = field.Label,
                description = field.Description,
                isRequired = field.IsRequired,
                isSystem = field.IsSystem,
                physicalColumnName = field.PhysicalColumnName,
                isSearchable = field.IsSearchable,
                isSortable = field.IsSortable,
                isFilterable = field.IsFilterable,
                isReportable = field.IsReportable,
                settings = field.Settings,
                displayOrder = field.DisplayOrder,
                createdBy = QueryContext.UserId,
            }, cancellationToken: ct));
        return ((long)row.Id, (Guid)row.PublicId);
    }

    public async Task UpdatePhysicalColumnNameAsync(long id, string physicalColumnName, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        await connection.ExecuteAsync(
            new CommandDefinition(UpdatePhysicalColumnNameSql, new { id, physicalColumnName }, cancellationToken: ct));
    }

    public async Task<AppField?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.QuerySingleOrDefaultAsync<AppField>(
            new CommandDefinition(GetByPublicIdSql, new { tenantId = QueryContext.TenantId, publicId }, cancellationToken: ct));
    }

    public async Task<int> UpdateAsync(Guid publicId, long tableId, string name, string? label, string? description,
        bool isRequired, string? defaultValue, bool isSearchable, bool isSortable,
        bool isFilterable, bool isReportable, string? settings, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteAsync(
            new CommandDefinition(UpdateFieldSql, new
            {
                tenantId = QueryContext.TenantId,
                publicId, tableId, name, label, description,
                isRequired, defaultValue, isSearchable, isSortable,
                isFilterable, isReportable, settings,
                modifiedBy = QueryContext.UserId,
            }, cancellationToken: ct));
    }

    private const string SoftBulkDeleteFieldsSql = """
        UPDATE meta.AppField
        SET IsDeleted = 1, DeletedOn = SYSUTCDATETIME(), DeletedBy = @deletedBy
        WHERE TenantId = @tenantId AND PublicId IN @publicIds AND AppTableId = @tableId AND IsSystem = 0 AND IsDeleted = 0
        """;

    public async Task<int> DeleteAsync(Guid publicId, long tableId, CancellationToken ct = default)
    {
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteFieldSql, new
            {
                tenantId = QueryContext.TenantId,
                publicId, tableId,
                deletedBy = QueryContext.UserId,
            }, cancellationToken: ct));
    }

    public async Task<int> BulkDeleteAsync(IEnumerable<Guid> publicIds, long tableId, CancellationToken ct = default)
    {
        if (!publicIds.Any()) return 0;
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteAsync(
            new CommandDefinition(SoftBulkDeleteFieldsSql, new
            {
                tenantId = QueryContext.TenantId,
                publicIds,
                tableId,
                deletedBy = QueryContext.UserId,
            }, cancellationToken: ct));
    }
}
