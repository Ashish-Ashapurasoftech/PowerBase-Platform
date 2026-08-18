using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Application.Fields.Queries.GetFieldUsage;

namespace PowerBase.Infrastructure.Repositories;

public class AppFieldRepository : TenantRepositoryBase, IAppFieldRepository
{
    private const string SelectColumns = """
        af.Id, af.PublicId, af.AppTableId, af.FieldTypeId, ft.Code AS TypeCode,
        af.Name, af.Label, af.Description, af.PhysicalColumnName, af.DefaultValue,
        af.IsRequired, af.IsSearchable, af.IsSortable, af.IsFilterable, af.IsReportable, af.IsAuditable,
        af.IsUnique, af.IsSystem, af.Fid, af.Settings, af.IsEncrypted, af.IsDeleted, af.CreatedOn, af.CreatedBy
        """;

    private const string GetByIdInTableSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppField af
        JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
        WHERE af.Id         = @fieldId
          AND af.AppTableId = @tableId
          AND af.IsDeleted  = 0
        """;

    private const string ListByTableSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppField af
        JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
        WHERE af.AppTableId = @tableId
          AND af.IsDeleted  = 0
        ORDER BY af.Id
        """;

    private const string NameExistsSql = """
        SELECT CAST(CASE WHEN EXISTS (
            SELECT 1 FROM meta.AppField
            WHERE AppTableId = @tableId AND Name = @name AND IsDeleted = 0
        ) THEN 1 ELSE 0 END AS BIT)
        """;

    private const string InsertSql = """
        INSERT INTO meta.AppField
            (AppTableId, FieldTypeId, Name, Label, Description, IsRequired, DefaultValue,
             IsSystem, PhysicalColumnName, IsSearchable, IsSortable, IsFilterable, IsReportable, IsAuditable, IsEncrypted,
             Fid, Settings, DisplayOrder, IsDeleted, CreatedOn, CreatedBy)
        OUTPUT INSERTED.Id, INSERTED.PublicId
        VALUES (@tableId, @fieldTypeId, @name, @label, @description, @isRequired, @defaultValue,
                @isSystem, @physicalColumnName, @isSearchable, @isSortable, @isFilterable, @isReportable, @isAuditable, @isEncrypted,
                @fid, @settings, @displayOrder, 0, SYSUTCDATETIME(), @createdBy)
        """;

    private const string GetNextFidSql = """
        SELECT ISNULL(MAX(Fid), 5) + 1 FROM meta.AppField WHERE AppTableId = @tableId
        """;

    private const string GetByFidInTableSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppField af
        JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
        WHERE af.AppTableId = @tableId AND af.Fid = @fid AND af.IsDeleted = 0
        """;

    private const string UpdatePhysicalColumnNameSql = """
        UPDATE meta.AppField SET PhysicalColumnName = @physicalColumnName WHERE Id = @id
        """;

    private const string UpdateSettingsSql = """
        UPDATE meta.AppField SET Settings = @settings, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy WHERE Id = @id
        """;

    // TypeCode is not a physical column on meta.AppField — it's derived via the ft.Code join in
    // SelectColumns, so changing type is just a FieldTypeId swap.
    private const string UpdateFieldTypeSql = """
        UPDATE meta.AppField
        SET FieldTypeId = @fieldTypeId, Settings = @settings, IsRequired = @isRequired,
            ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE Id = @id
        """;

    private const string GetByPublicIdSql = $"""
        SELECT {SelectColumns}
        FROM meta.AppField af
        JOIN core.FieldType ft ON ft.Id = af.FieldTypeId
        WHERE af.PublicId = @publicId AND af.IsDeleted = 0
        """;

    private const string UpdateFieldSql = """
        UPDATE meta.AppField
        SET Name = @name, Label = @label, Description = @description,
            IsRequired = @isRequired, DefaultValue = @defaultValue,
            IsSearchable = @isSearchable, IsSortable = @isSortable,
            IsFilterable = @isFilterable, IsReportable = @isReportable, IsAuditable = @isAuditable,
            IsUnique = @isUnique,
            IsEncrypted = @isEncrypted,
            Settings = @settings,
            ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE PublicId = @publicId AND AppTableId = @tableId AND IsSystem = 0 AND IsDeleted = 0
        """;

    private const string SoftDeleteFieldSql = """
        DECLARE @fid INT = (
                   SELECT Fid FROM meta.AppField
                   WHERE PublicId = @publicId AND AppTableId = @tableId AND IsSystem = 0 AND IsDeleted = 0);
               -- Remove orphaned FormElement rows that point to this field's Fid
               DELETE fe
               FROM meta.FormElement fe
               JOIN meta.FormSection fs ON fs.Id = fe.FormSectionId
               JOIN meta.Form f ON f.Id = fs.FormId
               WHERE f.AppTableId = @tableId
                 AND fe.AppFieldId = @fid;
               -- Soft-delete the field itself
               UPDATE meta.AppField
               SET IsDeleted = 1, DeletedOn = SYSUTCDATETIME(), DeletedBy = @deletedBy
               WHERE PublicId = @publicId AND AppTableId = @tableId AND IsSystem = 0 AND IsDeleted = 0
        """;

    private const string SoftBulkDeleteFieldsSql = """
        -- Collect the Fids of all fields being deleted
        DECLARE @fids TABLE (Fid INT);
        INSERT INTO @fids
        SELECT Fid FROM meta.AppField
        WHERE PublicId IN @publicIds AND AppTableId = @tableId AND IsSystem = 0 AND IsDeleted = 0;
        -- Remove orphaned FormElement rows that point to any of those Fids
        DELETE fe
        FROM meta.FormElement fe
        JOIN meta.FormSection fs ON fs.Id = fe.FormSectionId
        JOIN meta.Form f ON f.Id = fs.FormId
        WHERE f.AppTableId = @tableId
          AND fe.AppFieldId IN (SELECT Fid FROM @fids);
        -- Soft-delete the fields themselves
        UPDATE meta.AppField                        
        SET IsDeleted = 1, DeletedOn = SYSUTCDATETIME(), DeletedBy = @deletedBy
        WHERE PublicId IN @publicIds AND AppTableId = @tableId AND IsSystem = 0 AND IsDeleted = 0
        """;

    public AppFieldRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<AppField> GetByIdInTableAsync(long fieldId, long tableId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var field = await connection.QuerySingleOrDefaultAsync<AppField>(
            new CommandDefinition(GetByIdInTableSql, new { fieldId, tableId }, cancellationToken: ct));
        return field ?? throw new NotFoundException("Field", fieldId);
    }

    public async Task<IReadOnlyList<AppField>> ListByTableAsync(long tableId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var results = await connection.QueryAsync<AppField>(
            new CommandDefinition(ListByTableSql, new { tableId }, cancellationToken: ct));
        return results.AsList();
    }

    public async Task<bool> NameExistsInTableAsync(long tableId, string name, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(NameExistsSql, new { tableId, name }, cancellationToken: ct));
    }

    public async Task<int> GetNextFidAsync(long tableId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(GetNextFidSql, new { tableId }, cancellationToken: ct));
    }

    public async Task<AppField?> GetByFidInTableAsync(long tableId, int fid, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<AppField>(
            new CommandDefinition(GetByFidInTableSql, new { tableId, fid }, cancellationToken: ct));
    }

    public async Task<(long Id, Guid PublicId)> CreateAsync(AppField field, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var row = await connection.QuerySingleAsync(
            new CommandDefinition(InsertSql, new
            {
                tableId = field.AppTableId,
                fieldTypeId = field.FieldTypeId,
                name = field.Name,
                label = field.Label,
                description = field.Description,
                isRequired = field.IsRequired,
                defaultValue = field.DefaultValue,
                isSystem = field.IsSystem,
                physicalColumnName = field.PhysicalColumnName,
                isSearchable = field.IsSearchable,
                isSortable = field.IsSortable,
                isFilterable = field.IsFilterable,
                isReportable = field.IsReportable,
                isAuditable = field.IsAuditable,
                isEncrypted = field.IsEncrypted,
                fid = field.Fid,
                settings = field.Settings,
                displayOrder = field.DisplayOrder,
                createdBy = QueryContext.UserId,
            }, cancellationToken: ct));
        return ((long)row.Id, (Guid)row.PublicId);
    }

    public async Task UpdatePhysicalColumnNameAsync(long id, string physicalColumnName, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(UpdatePhysicalColumnNameSql, new { id, physicalColumnName }, cancellationToken: ct));
    }

    public async Task UpdateSettingsAsync(long id, string? settings, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(UpdateSettingsSql, new { id, settings, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
    }

    public async Task UpdateFieldTypeAsync(long id, long fieldTypeId, string? settings, bool isRequired, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(UpdateFieldTypeSql,
                new { id, fieldTypeId, settings, isRequired, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
    }

    public async Task RevertToNumberFieldAsync(long fieldId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        
        var numberTypeId = await connection.QuerySingleAsync<long>(
            new CommandDefinition(
                "SELECT Id FROM core.FieldType WHERE Code = 'Number' AND IsActive = 1",
                cancellationToken: ct));

        await connection.ExecuteAsync(
            new CommandDefinition(
                """
                UPDATE meta.AppField
                SET FieldTypeId = @numberTypeId,
                    Settings    = NULL,
                    ModifiedOn  = SYSUTCDATETIME(),
                    ModifiedBy  = @modifiedBy
                WHERE Id = @fieldId
                """,
                new { fieldId, numberTypeId, modifiedBy = QueryContext.UserId },
                cancellationToken: ct));
    }


    public async Task<AppField?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleOrDefaultAsync<AppField>(
            new CommandDefinition(GetByPublicIdSql, new { publicId }, cancellationToken: ct));
    }

    public async Task<int> UpdateAsync(Guid publicId, long tableId, string name, string? label, string? description,
        bool isRequired, string? defaultValue, bool isSearchable, bool isSortable,
        bool isFilterable, bool isReportable, bool isAuditable, bool isUnique, bool isEncrypted, string? settings, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(
            new CommandDefinition(UpdateFieldSql, new
            {
                publicId, tableId, name, label, description,
                isRequired, defaultValue, isSearchable, isSortable,
                isFilterable, isReportable, isAuditable, isUnique, isEncrypted, settings,
                modifiedBy = QueryContext.UserId,
            }, cancellationToken: ct));
    }

    public async Task<int> DeleteAsync(Guid publicId, long tableId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(
            new CommandDefinition(SoftDeleteFieldSql, new
            {
                publicId, tableId, deletedBy = QueryContext.UserId,
            }, cancellationToken: ct));
    }

    public async Task<int> BulkDeleteAsync(IEnumerable<Guid> publicIds, long tableId, CancellationToken ct = default)
    {
        if (!publicIds.Any()) return 0;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(
            new CommandDefinition(SoftBulkDeleteFieldsSql, new
            {
                publicIds, tableId, deletedBy = QueryContext.UserId,
            }, cancellationToken: ct));
    }

    public async Task<FieldUsageDto> GetFieldUsageAsync(long tableId, long fieldId, int fid, long appId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);

        const string formsSql = """
            SELECT f.PublicId AS Id, f.Name, CAST(1 AS BIT) AS IsExplicitlyPlaced
            FROM   meta.Form f
            JOIN   meta.FormSection fs   ON fs.FormId = f.Id
            JOIN   meta.FormElement  fe  ON fe.FormSectionId = fs.Id
            WHERE  f.AppTableId = @tableId
              AND  fe.AppFieldId = @fieldId
              AND  f.IsDeleted  = 0
            GROUP BY f.PublicId, f.Name
            
            UNION
            
            SELECT f.PublicId, f.Name, CAST(0 AS BIT) AS IsExplicitlyPlaced
            FROM   meta.Form f
            WHERE  f.AppTableId = @tableId
              AND  f.IsDeleted  = 0
              AND  f.PublicId NOT IN (
                  SELECT f2.PublicId
                  FROM   meta.Form f2
                  JOIN   meta.FormSection fs ON fs.FormId = f2.Id
                  JOIN   meta.FormElement  fe ON fe.FormSectionId = fs.Id
                  WHERE  f2.AppTableId = @tableId AND fe.AppFieldId = @fieldId AND f2.IsDeleted = 0
              )
            """;

        const string reportsSql = """
            SELECT r.PublicId AS Id, r.Name,
                CAST(CASE WHEN cols.value IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS IsColumn,
                CAST(CASE WHEN filters.fieldId IS NOT NULL 
                    OR r.Definition LIKE '%"FieldId":' + @fid + '[,}]%' 
                    OR r.Definition LIKE '%"FieldId": ' + @fid + '[,}]%' 
                    OR r.Definition LIKE '%"FieldId":' + @fid + CHAR(13) + '%' 
                    OR r.Definition LIKE '%"FieldId": ' + @fid + CHAR(13) + '%' THEN 1 ELSE 0 END AS BIT) AS IsFilter,
                CAST(CASE WHEN JSON_VALUE(r.Definition, '$.SortFieldId') = @fid OR sortFields.fieldId IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS IsSort,
                CAST(CASE WHEN JSON_VALUE(r.Definition, '$.GroupByFieldId') = @fid THEN 1 ELSE 0 END AS BIT) AS IsGroupBy
            FROM meta.Report r
            OUTER APPLY OPENJSON(r.Definition, '$.Columns')
                WITH (value INT '$') AS cols
            OUTER APPLY OPENJSON(r.Definition, '$.Filters')
                WITH (fieldId INT '$.FieldId') AS filters
            OUTER APPLY OPENJSON(r.Definition, '$.SortFields')
                WITH (fieldId INT '$.FieldId') AS sortFields
            WHERE r.AppTableId = @tableId
              AND r.IsDeleted  = 0
              AND (
                  cols.value = @fid
                  OR filters.fieldId = @fid
                  OR JSON_VALUE(r.Definition, '$.SortFieldId') = @fid
                  OR sortFields.fieldId = @fid
                  OR JSON_VALUE(r.Definition, '$.GroupByFieldId') = @fid
                  OR r.Definition LIKE '%"FieldId":' + @fid + '[,}]%'
                  OR r.Definition LIKE '%"FieldId": ' + @fid + '[,}]%'
                  OR r.Definition LIKE '%"FieldId":' + @fid + CHAR(13) + '%'
                  OR r.Definition LIKE '%"FieldId": ' + @fid + CHAR(13) + '%'
              )
            """;

        const string rolesSql = """
            SELECT ar.PublicId AS RoleId, ar.Name AS RoleName,
                ISNULL(fp.Access, 'Default') AS EffectiveAccess,
                CAST(CASE WHEN fp.Id IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS IsCustom
            FROM meta.AppRole ar
            LEFT JOIN meta.AppRoleFieldPermission fp
                ON fp.AppRoleId = ar.Id AND fp.AppFieldId = @fieldId
            WHERE ar.AppId = @appId
              AND ar.IsDeleted = 0
            """;

        using var multi = await connection.QueryMultipleAsync(
            new CommandDefinition(
                formsSql + ";" + reportsSql + ";" + rolesSql, 
                new { tableId, fieldId, fid = fid.ToString(), appId }, 
                cancellationToken: ct)
        );

        var formsRaw = await multi.ReadAsync<FieldUsageFormItem>();
        
        var reportsRaw = await multi.ReadAsync();
        var reportItems = new List<FieldUsageReportItem>();
        foreach (var r in reportsRaw)
        {
            var reasons = new List<string>();
            if ((bool)r.IsColumn) reasons.Add("column");
            if ((bool)r.IsFilter) reasons.Add("filter");
            if ((bool)r.IsSort) reasons.Add("sort");
            if ((bool)r.IsGroupBy) reasons.Add("group-by");
            
            reportItems.Add(new FieldUsageReportItem((Guid)r.Id, (string)r.Name, reasons));
        }
        
        var rolesRaw = await multi.ReadAsync<FieldUsageRoleItem>();

        return new FieldUsageDto
        {
            Forms = formsRaw.ToList(),
            Reports = reportItems,
            Roles = rolesRaw.ToList()
        };
    }
}
