using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.Enums;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class AppTableRepository : TenantRepositoryBase, IAppTableRepository
{
    private const string SelectColumns = """
        Id, PublicId, AppId, Name, SingularLabel, PluralLabel, Description,
        PhysicalTableName, DefaultReportSettings, DisplayFieldId, KeyFieldId, DefaultRecordPickerField1Id, DefaultRecordPickerField2Id, DefaultRecordPickerField3Id, RecordCount, IsSystem, DisplayOrder,
        IsDeleted, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, DeletedOn, DeletedBy, RowVersion, Icon
        """;

    private const string GetByIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppTable
        WHERE Id = @id
          AND IsDeleted = 0
        """;

    private const string GetByPublicIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppTable
        WHERE PublicId = @publicId
          AND IsDeleted = 0
        """;

    private const string GetAppIdByPublicIdSql = """
        SELECT AppId FROM meta.AppTable
        WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    private const string ListByAppSql = """
        SELECT t.Id, t.PublicId, t.AppId, t.Name, t.SingularLabel, t.PluralLabel, t.Description,
               t.PhysicalTableName, t.DisplayFieldId, t.KeyFieldId, t.DefaultRecordPickerField1Id, t.DefaultRecordPickerField2Id, t.DefaultRecordPickerField3Id, t.RecordCount, t.IsSystem, t.DisplayOrder,
               t.IsDeleted, t.CreatedOn, t.CreatedBy, t.ModifiedOn, t.ModifiedBy, t.DeletedOn, t.DeletedBy, t.RowVersion, t.Icon,
               f.Id, f.PublicId, f.AppTableId, f.FieldTypeId,
               f.Name, f.Label, f.Description, f.PhysicalColumnName, f.DefaultValue,
               f.IsRequired, f.IsSearchable, f.IsSortable, f.IsFilterable, f.IsReportable,
               f.IsUnique, f.IsSystem, f.IsDeleted, f.CreatedOn, f.CreatedBy
        FROM meta.AppTable t
        LEFT JOIN meta.AppField f ON t.Id = f.AppTableId AND f.IsDeleted = 0
        WHERE t.AppId = @appId
          AND t.IsDeleted = 0
        ORDER BY t.DisplayOrder, t.Name, f.Id
        """;

    private const string NameExistsSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1 FROM meta.AppTable
            WHERE AppId = @appId AND Name = @name AND IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string InsertSql = """
        INSERT INTO meta.AppTable (AppId, Name, SingularLabel, PluralLabel, Description, Icon, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.Id, INSERTED.PublicId
        VALUES (@appId, @name, @singularLabel, @pluralLabel, @description, @icon, 0, SYSUTCDATETIME(), @createdBy)
        """;

    private const string UpdatePhysicalNameSql = """
        UPDATE meta.AppTable SET PhysicalTableName = @physicalTableName WHERE Id = @id
        """;

    private const string UpdateTableSql = """
        UPDATE meta.AppTable
        SET Name          = @name,
            SingularLabel = @singularLabel,
            PluralLabel   = @pluralLabel,
            Description   = @description,
            Icon          = @icon,
            DefaultRecordPickerField1Id = @defaultRecordPickerField1Id,
            DefaultRecordPickerField2Id = @defaultRecordPickerField2Id,
            DefaultRecordPickerField3Id = @defaultRecordPickerField3Id,
            ModifiedOn    = SYSUTCDATETIME(),
            ModifiedBy    = @modifiedBy
        WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    private const string SetKeyFieldSql = """
        UPDATE meta.AppTable
        SET KeyFieldId = @keyFieldId,
            ModifiedOn = SYSUTCDATETIME(),
            ModifiedBy = @modifiedBy
        WHERE Id = @tableId AND IsDeleted = 0
        """;

    private const string UpdateDefaultReportSettingsSql = """
        UPDATE meta.AppTable
        SET DefaultReportSettings = @defaultReportSettings,
            ModifiedOn            = SYSUTCDATETIME(),
            ModifiedBy            = @modifiedBy
        WHERE PublicId = @publicId AND IsDeleted = 0
        """;

    private const string SoftDeleteSql = """
        UPDATE meta.AppTable
        SET IsDeleted = 1,
            ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy,
            DeletedOn  = SYSUTCDATETIME(), DeletedBy  = @modifiedBy
        WHERE PublicId = @publicId
          AND IsDeleted = 0
        """;

    private const string IncrementRecordCountSql = """
        UPDATE meta.AppTable SET RecordCount = RecordCount + 1 WHERE Id = @id
        """;

    private const string DecrementRecordCountSql = """
        UPDATE meta.AppTable SET RecordCount = RecordCount - 1 WHERE Id = @id AND RecordCount > 0
        """;

    private const string DecrementRecordCountBySql = """
        UPDATE meta.AppTable SET RecordCount = CASE WHEN RecordCount >= @count THEN RecordCount - @count ELSE 0 END WHERE Id = @id
        """;

    public AppTableRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<AppTable> GetByIdAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var table = await connection.QuerySingleOrDefaultAsync<AppTable>(
            new CommandDefinition(GetByIdSql, new { id }, cancellationToken: ct));
        return table ?? throw new NotFoundException("Table", id);
    }

    public async Task<AppTable> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var table = await connection.QuerySingleOrDefaultAsync<AppTable>(
            new CommandDefinition(GetByPublicIdSql, new { publicId }, cancellationToken: ct));
        return table ?? throw new NotFoundException("Table", publicId);
    }

    public async Task<long> GetAppIdByPublicIdAsync(Guid tablePublicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var appId = await connection.ExecuteScalarAsync<long?>(
            new CommandDefinition(GetAppIdByPublicIdSql, new { publicId = tablePublicId }, cancellationToken: ct));
        return appId ?? throw new NotFoundException("Table", tablePublicId);
    }

    public async Task<IReadOnlyList<AppTable>> ListByAppAsync(long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var lookup = new Dictionary<long, AppTable>();

        await connection.QueryAsync<AppTable, AppField, AppTable>(
            new CommandDefinition(ListByAppSql, new { appId }, cancellationToken: ct),
            (table, field) =>
            {
                if (!lookup.TryGetValue(table.Id, out var currentTable))
                {
                    currentTable = table;
                    currentTable.Fields = new List<AppField>();
                    lookup.Add(currentTable.Id, currentTable);
                }
                if (field != null)
                {
                    field.TypeCode = ((FieldTypeCode)field.FieldTypeId).ToString();
                    currentTable.Fields.Add(field);
                }
                return currentTable;
            },
            splitOn: "Id");

        return lookup.Values.OrderBy(t => t.DisplayOrder).ThenBy(t => t.Name).ToList();
    }

    public async Task<bool> NameExistsInAppAsync(long appId, string name, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(NameExistsSql, new { appId, name }, cancellationToken: ct));
    }

    public async Task<(long Id, Guid PublicId)> CreateAsync(AppTable table, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var row = await connection.QuerySingleAsync(
            new CommandDefinition(InsertSql, new
            {
                appId = table.AppId,
                name = table.Name,
                singularLabel = table.SingularLabel,
                pluralLabel = table.PluralLabel,
                description = table.Description,
                icon = table.Icon,
                createdBy = QueryContext.UserId,
            }, cancellationToken: ct));
        return ((long)row.Id, (Guid)row.PublicId);
    }

    public async Task UpdatePhysicalNameAsync(long id, string physicalTableName, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(UpdatePhysicalNameSql, new { id, physicalTableName }, cancellationToken: ct));
    }

    public async Task<int> UpdateAsync(Guid publicId, string name, string? singularLabel, string? pluralLabel, string? description, string? icon, long? defaultRecordPickerField1Id = null, long? defaultRecordPickerField2Id = null, long? defaultRecordPickerField3Id = null, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(
            new CommandDefinition(UpdateTableSql, new
            {
                publicId, name, singularLabel, pluralLabel, description, icon,
                defaultRecordPickerField1Id, defaultRecordPickerField2Id, defaultRecordPickerField3Id,
                modifiedBy = QueryContext.UserId,
            }, cancellationToken: ct));
    }

    public async Task SetKeyFieldAsync(long tableId, long? keyFieldId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(SetKeyFieldSql, new { tableId, keyFieldId, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
    }

    public async Task UpdateDefaultReportSettingsAsync(Guid publicId, string defaultReportSettings, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(UpdateDefaultReportSettingsSql, new
            {
                publicId, defaultReportSettings,
                modifiedBy = QueryContext.UserId,
            }, cancellationToken: ct));
        if (affected == 0)
            throw new NotFoundException("Table", publicId);
    }

    public async Task DeleteAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteSql, new { publicId, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
        if (affected == 0)
            throw new NotFoundException("Table", publicId);
    }

    public async Task IncrementRecordCountAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(IncrementRecordCountSql, new { id }, cancellationToken: ct));
    }

    public async Task DecrementRecordCountAsync(long id, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(DecrementRecordCountSql, new { id }, cancellationToken: ct));
    }

    public async Task DecrementRecordCountByAsync(long id, int count, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(DecrementRecordCountBySql, new { id, count }, cancellationToken: ct));
    }
}
