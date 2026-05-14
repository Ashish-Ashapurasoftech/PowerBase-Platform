using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class RecordRepository : BaseRepository, IRecordRepository
{
    public RecordRepository(DbConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListAsync(
        AppTable table, IReadOnlyList<AppField> fields, int page, int pageSize, CancellationToken ct = default)
    {
        var fieldCols = BuildFieldColumnList(fields);
        var sql = $"""
            SELECT Id, PublicId, TenantId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy{fieldCols}
            FROM {PhysicalNaming.FullTableName(table.Id)}
            WHERE TenantId = @tenantId AND IsDeleted = 0
            ORDER BY Id
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        var offset = (page - 1) * pageSize;
        await using var connection = ConnectionFactory.Create();
        var rows = await connection.QueryAsync(
            new CommandDefinition(sql, new { tenantId = QueryContext.TenantId, offset, pageSize }, cancellationToken: ct));
        return rows.Select(ToDictionary).ToList();
    }

    public async Task<int> CountAsync(AppTable table, CancellationToken ct = default)
    {
        var sql = $"SELECT COUNT(*) FROM {PhysicalNaming.FullTableName(table.Id)} WHERE TenantId = @tenantId AND IsDeleted = 0";
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, new { tenantId = QueryContext.TenantId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<string, object?>> GetByPublicIdAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid publicId, CancellationToken ct = default)
    {
        var fieldCols = BuildFieldColumnList(fields);
        var sql = $"""
            SELECT Id, PublicId, TenantId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy{fieldCols}
            FROM {PhysicalNaming.FullTableName(table.Id)}
            WHERE TenantId = @tenantId AND PublicId = @publicId AND IsDeleted = 0
            """;

        await using var connection = ConnectionFactory.Create();
        var row = await connection.QuerySingleOrDefaultAsync(
            new CommandDefinition(sql, new { tenantId = QueryContext.TenantId, publicId }, cancellationToken: ct));

        if (row is null)
            throw new NotFoundException("Record", publicId);

        return ToDictionary(row);
    }

    public async Task<Guid> CreateAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, object?> values, CancellationToken ct = default)
    {
        var relevantFields = fields.Where(f => values.ContainsKey(f.Id)).ToList();
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", QueryContext.TenantId);
        parameters.Add("createdBy", QueryContext.UserId);

        string sql;
        if (relevantFields.Count == 0)
        {
            sql = $"""
                INSERT INTO {PhysicalNaming.FullTableName(table.Id)} (TenantId, CreatedBy)
                OUTPUT INSERTED.PublicId
                VALUES (@tenantId, @createdBy)
                """;
        }
        else
        {
            var colList = string.Join(", ", relevantFields.Select(f => PhysicalNaming.ColumnName(f.Id)));
            var paramList = string.Join(", ", relevantFields.Select(f => $"@{PhysicalNaming.ColumnName(f.Id)}"));
            foreach (var f in relevantFields)
                parameters.Add(PhysicalNaming.ColumnName(f.Id), values[f.Id]);

            sql = $"""
                INSERT INTO {PhysicalNaming.FullTableName(table.Id)} (TenantId, CreatedBy, {colList})
                OUTPUT INSERTED.PublicId
                VALUES (@tenantId, @createdBy, {paramList})
                """;
        }

        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<Guid>(
            new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    public async Task UpdateAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid publicId,
        IReadOnlyDictionary<long, object?> values, CancellationToken ct = default)
    {
        var relevantFields = fields.Where(f => values.ContainsKey(f.Id)).ToList();
        if (relevantFields.Count == 0)
            return;

        var setClauses = string.Join(", ", relevantFields.Select(f => $"{PhysicalNaming.ColumnName(f.Id)} = @{PhysicalNaming.ColumnName(f.Id)}"));
        var sql = $"""
            UPDATE {PhysicalNaming.FullTableName(table.Id)}
            SET {setClauses}, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
            WHERE TenantId = @tenantId AND PublicId = @publicId AND IsDeleted = 0
            """;

        var parameters = new DynamicParameters();
        parameters.Add("tenantId", QueryContext.TenantId);
        parameters.Add("publicId", publicId);
        parameters.Add("modifiedBy", QueryContext.UserId);
        foreach (var f in relevantFields)
            parameters.Add(PhysicalNaming.ColumnName(f.Id), values[f.Id]);

        await using var connection = ConnectionFactory.Create();
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(sql, parameters, cancellationToken: ct));

        if (affected == 0)
            throw new NotFoundException("Record", publicId);
    }

    public async Task DeleteAsync(AppTable table, Guid publicId, CancellationToken ct = default)
    {
        var sql = $"""
            UPDATE {PhysicalNaming.FullTableName(table.Id)}
            SET IsDeleted = 1, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
            WHERE TenantId = @tenantId AND PublicId = @publicId AND IsDeleted = 0
            """;

        await using var connection = ConnectionFactory.Create();
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { tenantId = QueryContext.TenantId, publicId, modifiedBy = QueryContext.UserId }, cancellationToken: ct));

        if (affected == 0)
            throw new NotFoundException("Record", publicId);
    }

    private static string BuildFieldColumnList(IReadOnlyList<AppField> fields) =>
        fields.Count > 0
            ? ", " + string.Join(", ", fields.Select(f => PhysicalNaming.ColumnName(f.Id)))
            : string.Empty;

    private static IReadOnlyDictionary<string, object?> ToDictionary(dynamic row)
    {
        var dict = (IDictionary<string, object>)row;
        return dict.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value == DBNull.Value ? null : (object?)kvp.Value);
    }
}
