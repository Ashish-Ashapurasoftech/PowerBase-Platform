using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Services;

public class SchemaEngineService : ISchemaEngineService
{
    private readonly DbConnectionFactory _connectionFactory;

    private const string GetFieldTypeSqlDataTypeSql = "SELECT SqlDataType FROM core.FieldType WHERE Id = @id AND IsActive = 1";

    public SchemaEngineService(DbConnectionFactory connectionFactory)
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
                    TenantId   BIGINT NOT NULL,
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

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task AddColumnAsync(AppTable table, AppField field, CancellationToken ct = default)
    {
        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(ct);

        var sqlDataType = await connection.ExecuteScalarAsync<string>(
            new CommandDefinition(GetFieldTypeSqlDataTypeSql, new { id = field.FieldTypeId }, cancellationToken: ct))
            ?? throw new InvalidOperationException($"Unknown or inactive field type id: {field.FieldTypeId}");

        var physicalTable = PhysicalNaming.FullTableName(table.Id);
        var physicalColumn = PhysicalNaming.ColumnName(field.Id);

        // Columns always created as NULL — requiredness is enforced by validators, not DDL
        var sql = $"""
            IF NOT EXISTS (
                SELECT 1 FROM sys.columns c
                JOIN sys.tables t ON t.object_id = c.object_id
                JOIN sys.schemas s ON s.schema_id = t.schema_id
                WHERE s.name = 'data'
                  AND t.name = '{PhysicalNaming.TableName(table.Id)}'
                  AND c.name = '{physicalColumn}')
            BEGIN
                ALTER TABLE {physicalTable} ADD {physicalColumn} {sqlDataType} NULL;
            END
            """;

        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }
}
