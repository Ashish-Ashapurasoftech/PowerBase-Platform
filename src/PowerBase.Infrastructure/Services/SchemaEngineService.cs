using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Services;

public class SchemaEngineService : ISchemaEngineService
{
    private readonly ITenantConnectionFactory _connectionFactory;

    private const string GetFieldTypeSqlDataTypeSql = "SELECT SqlDataType FROM core.FieldType WHERE Id = @id AND IsActive = 1";

    public SchemaEngineService(ITenantConnectionFactory connectionFactory)
    {
        _connectionFactory = connectionFactory;
    }

    public async Task CreateTableAsync(AppTable table, CancellationToken ct = default)
    {
        var physicalName = PhysicalNaming.FullTableName(table.Id);
        var sql = $"""
            IF NOT EXISTS (SELECT 1 FROM sys.tables t
                          JOIN sys.schemas s ON s.schema_id = t.schema_id
                          WHERE s.name = 'data' AND t.name = '{PhysicalNaming.TableName(table.Id)}')
            BEGIN
                CREATE TABLE {physicalName} (
                    Id         BIGINT IDENTITY(1,1) NOT NULL,
                    PublicId   UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(),
                    IsDeleted  BIT NOT NULL DEFAULT 0,
                    CreatedOn  DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(),
                    CreatedBy  BIGINT NOT NULL DEFAULT 0,
                    ModifiedOn DATETIME2(3) NULL,
                    ModifiedBy BIGINT NULL,
                    RowVersion ROWVERSION NOT NULL,
                    CONSTRAINT PK_{PhysicalNaming.TableName(table.Id)} PRIMARY KEY CLUSTERED (Id)
                );
                CREATE UNIQUE NONCLUSTERED INDEX UX_{PhysicalNaming.TableName(table.Id)}_PublicId
                    ON {physicalName}(PublicId)
                    WHERE IsDeleted = 0;
            END
            """;

        await using var connection = await _connectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task AddColumnAsync(AppTable table, AppField field, CancellationToken ct = default)
    {
        // Computed fields (Formula) are evaluated at read time and have no physical column.
        if (PhysicalNaming.IsComputedTypeCode(field.TypeCode))
            return;

        await using var connection = await _connectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);

        var sqlDataType = await connection.ExecuteScalarAsync<string>(
            new CommandDefinition(GetFieldTypeSqlDataTypeSql, new { id = field.FieldTypeId }, cancellationToken: ct))
            ?? throw new InvalidOperationException($"Unknown or inactive field type id: {field.FieldTypeId}");

        var physicalTable = PhysicalNaming.FullTableName(table.Id);
        var tableName = PhysicalNaming.TableName(table.Id);
        var physicalColumn = PhysicalNaming.ColumnName(field.Fid!.Value);

        // Columns always created as NULL — requiredness is enforced by validators, not DDL
        var sql = $"""
            IF NOT EXISTS (
                SELECT 1 FROM sys.columns c
                JOIN sys.tables t ON t.object_id = c.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = 'data'
                  AND t.name = '{tableName}'
                  AND c.name = '{physicalColumn}')
            BEGIN
                ALTER TABLE {physicalTable} ADD {physicalColumn} {sqlDataType} NULL;
            END
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));

        // Range fields get a second column for the end/max value
        if (PhysicalNaming.IsRangeTypeCode(field.TypeCode))
        {
            var endColumn = PhysicalNaming.EndColumnName(field.Fid!.Value);
            var endSql = $"""
                IF NOT EXISTS (
                    SELECT 1 FROM sys.columns c
                    JOIN sys.tables t ON t.object_id = c.object_id
                    JOIN sys.schemas s ON s.schema_id = t.schema_id
                    WHERE s.name = 'data'
                      AND t.name = '{tableName}'
                      AND c.name = '{endColumn}')
                BEGIN
                    ALTER TABLE {physicalTable} ADD {endColumn} {sqlDataType} NULL;
                END
                """;
            await connection.ExecuteAsync(new CommandDefinition(endSql, cancellationToken: ct));
        }
    }

    public async Task SetUniqueAsync(AppTable table, AppField field, bool enable, CancellationToken ct = default)
    {
        var physicalTable = PhysicalNaming.FullTableName(table.Id);
        var physicalColumn = PhysicalNaming.ColumnName(field.Fid!.Value);
        var indexName = $"UX_{PhysicalNaming.TableName(table.Id)}_{physicalColumn}";

        string sql;
        if (enable)
        {
            sql = $"""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = '{indexName}')
                    CREATE UNIQUE NONCLUSTERED INDEX {indexName}
                        ON {physicalTable}({physicalColumn})
                        WHERE {physicalColumn} IS NOT NULL AND IsDeleted = 0;
                """;
        }
        else
        {
            sql = $"DROP INDEX IF EXISTS {indexName} ON {physicalTable};";
        }

        await using var connection = await _connectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }
}
