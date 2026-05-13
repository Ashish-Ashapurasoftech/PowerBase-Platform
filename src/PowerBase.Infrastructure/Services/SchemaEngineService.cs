using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.UOW;

namespace PowerBase.Infrastructure.Services;

public class SchemaEngineService : ISchemaEngineService
{
    private readonly DbConnectionFactory _connectionFactory;

    private static readonly IReadOnlyDictionary<string, string> FieldTypeSqlMap =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Text"] = "NVARCHAR(500)",
            ["Number"] = "DECIMAL(18,4)",
            ["Date"] = "DATE",
            ["Boolean"] = "BIT",
        };

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
                    Id          BIGINT IDENTITY(1,1) NOT NULL,
                    PublicId    UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(),
                    TenantId    BIGINT NOT NULL,
                    IsDeleted   BIT NOT NULL DEFAULT 0,
                    CreatedAt   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    UpdatedAt   DATETIME2 NOT NULL DEFAULT SYSUTCDATETIME(),
                    RowVersion  ROWVERSION NOT NULL,
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
        if (!FieldTypeSqlMap.TryGetValue(field.FieldTypeId.ToString(), out var sqlType))
            throw new InvalidOperationException($"Unknown field type id: {field.FieldTypeId}");

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
                ALTER TABLE {physicalTable} ADD {physicalColumn} {sqlType} NULL;
            END
            """;

        await using var connection = _connectionFactory.Create();
        await connection.OpenAsync(ct);
        await connection.ExecuteAsync(new CommandDefinition(sql, cancellationToken: ct));
    }
}
