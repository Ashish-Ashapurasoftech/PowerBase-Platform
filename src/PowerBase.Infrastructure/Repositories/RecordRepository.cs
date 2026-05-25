using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
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
        AppTable table, IReadOnlyList<AppField> fields, int page, int pageSize,
        IReadOnlyList<ReportFilter>? filters = null,
        long? sortFieldId = null, bool sortDesc = false,
        CancellationToken ct = default)
    {
        var fieldCols = BuildFieldColumnList(fields);
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", QueryContext.TenantId);
        parameters.Add("offset", (page - 1) * pageSize);
        parameters.Add("pageSize", pageSize);

        var filterWhere = BuildFilterWhere(filters, parameters);
        var orderBy = sortFieldId.HasValue
            ? $"{PhysicalNaming.ColumnName(sortFieldId.Value)} {(sortDesc ? "DESC" : "ASC")}"
            : "Id";

        var sql = $"""
            SELECT Id, PublicId, TenantId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy{fieldCols}
            FROM {PhysicalNaming.FullTableName(table.Id)}
            WHERE TenantId = @tenantId AND IsDeleted = 0{filterWhere}
            ORDER BY {orderBy}
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        await using var connection = ConnectionFactory.Create();
        var rows = await connection.QueryAsync(
            new CommandDefinition(sql, parameters, cancellationToken: ct));
        return rows.Select(ToDictionary).ToList();
    }

    public async Task<int> CountAsync(AppTable table, IReadOnlyList<ReportFilter>? filters = null, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("tenantId", QueryContext.TenantId);
        var filterWhere = BuildFilterWhere(filters, parameters);

        var sql = $"SELECT COUNT(*) FROM {PhysicalNaming.FullTableName(table.Id)} WHERE TenantId = @tenantId AND IsDeleted = 0{filterWhere}";
        await using var connection = ConnectionFactory.Create();
        return await connection.ExecuteScalarAsync<int>(
            new CommandDefinition(sql, parameters, cancellationToken: ct));
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

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SummarizeAsync(
        AppTable table,
        AppField groupByField,
        IReadOnlyList<SummaryAggregation> aggregations,
        IReadOnlyList<AppField> allFields,
        CancellationToken ct = default)
    {
        var groupCol = PhysicalNaming.ColumnName(groupByField.Id);
        var fieldMap = allFields.ToDictionary(f => f.Id);

        var aggClauses = new List<string> { "COUNT(*) AS [Count]" };
        foreach (var agg in aggregations)
        {
            if (!fieldMap.TryGetValue(agg.FieldId, out var aggField))
                continue;

            var col = PhysicalNaming.ColumnName(agg.FieldId);
            var alias = $"[{agg.Function}_{aggField.Name.Replace(" ", "_")}]";
            var clause = agg.Function switch
            {
                "Sum" => $"SUM(CAST({col} AS DECIMAL(18,4))) AS {alias}",
                "Avg" => $"AVG(CAST({col} AS DECIMAL(18,4))) AS {alias}",
                "Min" => $"MIN({col}) AS {alias}",
                "Max" => $"MAX({col}) AS {alias}",
                _ => null,
            };
            if (clause is not null)
                aggClauses.Add(clause);
        }

        var aggSql = string.Join(", ", aggClauses);
        var sql = $"""
            SELECT {groupCol} AS GroupValue, {aggSql}
            FROM {PhysicalNaming.FullTableName(table.Id)}
            WHERE TenantId = @tenantId AND IsDeleted = 0
            GROUP BY {groupCol}
            ORDER BY {groupCol}
            """;

        await using var connection = ConnectionFactory.Create();
        var rows = await connection.QueryAsync(
            new CommandDefinition(sql, new { tenantId = QueryContext.TenantId }, cancellationToken: ct));
        return rows.Select(ToDictionary).ToList();
    }

    // --- Helpers ---

    private static string BuildFieldColumnList(IReadOnlyList<AppField> fields) =>
        fields.Count > 0
            ? ", " + string.Join(", ", fields.Select(f => PhysicalNaming.ColumnName(f.Id)))
            : string.Empty;

    /// <summary>
    /// Builds a SQL WHERE fragment from report filters.
    /// Column names are derived from integer field IDs (safe — no user input enters the SQL string).
    /// Values are passed as numbered Dapper parameters (@fv0, @fv1, …).
    /// </summary>
    private static string BuildFilterWhere(IReadOnlyList<ReportFilter>? filters, DynamicParameters parameters)
    {
        if (filters is null || filters.Count == 0)
            return string.Empty;

        var clauses = new List<string>();
        for (var i = 0; i < filters.Count; i++)
        {
            var filter = filters[i];
            var col = PhysicalNaming.ColumnName(filter.FieldId); // safe: integer field ID
            var paramName = $"fv{i}";

            switch (filter.Operator)
            {
                case "eq":
                    clauses.Add($"{col} = @{paramName}");
                    parameters.Add(paramName, filter.Value);
                    break;
                case "ne":
                    clauses.Add($"{col} <> @{paramName}");
                    parameters.Add(paramName, filter.Value);
                    break;
                case "gt":
                    clauses.Add($"{col} > @{paramName}");
                    parameters.Add(paramName, filter.Value);
                    break;
                case "gte":
                    clauses.Add($"{col} >= @{paramName}");
                    parameters.Add(paramName, filter.Value);
                    break;
                case "lt":
                    clauses.Add($"{col} < @{paramName}");
                    parameters.Add(paramName, filter.Value);
                    break;
                case "lte":
                    clauses.Add($"{col} <= @{paramName}");
                    parameters.Add(paramName, filter.Value);
                    break;
                case "contains":
                    clauses.Add($"{col} LIKE @{paramName}");
                    parameters.Add(paramName, $"%{filter.Value}%");
                    break;
                case "startsWith":
                    clauses.Add($"{col} LIKE @{paramName}");
                    parameters.Add(paramName, $"{filter.Value}%");
                    break;
                // Unknown operators are skipped (validated in command handler)
            }
        }

        return clauses.Count > 0 ? " AND " + string.Join(" AND ", clauses) : string.Empty;
    }

    private static IReadOnlyDictionary<string, object?> ToDictionary(dynamic row)
    {
        var dict = (IDictionary<string, object>)row;
        return dict.ToDictionary(
            kvp => kvp.Key,
            kvp => kvp.Value == DBNull.Value ? null : (object?)kvp.Value);
    }
}
