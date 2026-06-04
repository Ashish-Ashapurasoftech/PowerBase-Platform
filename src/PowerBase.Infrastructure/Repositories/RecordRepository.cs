using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class RecordRepository : TenantRepositoryBase, IRecordRepository
{
    public RecordRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext) { }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListAsync(
        AppTable table, IReadOnlyList<AppField> fields, int page, int pageSize,
        FilterGroup? filterTree = null,
        IReadOnlyList<SortSpec>? sortFields = null,
        long? restrictToCreatedBy = null,
        CancellationToken ct = default)
    {
        var fieldCols = BuildFieldColumnList(fields);
        var parameters = new DynamicParameters();
        parameters.Add("offset", (page - 1) * pageSize);
        parameters.Add("pageSize", pageSize);

        var filterWhere = BuildFilterTreeWhere(filterTree, parameters) + BuildOwnerWhere(restrictToCreatedBy, parameters);
        var fieldLookup = fields.ToDictionary(f => f.Id);
        var orderBy = sortFields?.Count > 0
            ? string.Join(", ", sortFields.Select(s =>
            {
                var colName = fieldLookup.TryGetValue(s.FieldId, out var sf) && sf.IsSystem
                    ? sf.PhysicalColumnName!
                    : PhysicalNaming.ColumnName(s.FieldId);
                return $"{colName} {(s.Desc ? "DESC" : "ASC")}";
            }))
            : "Id";

        var sql = $"""
            SELECT Id, PublicId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy{fieldCols}
            FROM {PhysicalNaming.FullTableName(table.Id)}
            WHERE IsDeleted = 0{filterWhere}
            ORDER BY {orderBy}
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        return rows.Select(ToDictionary).ToList();
    }

    public async Task<int> CountAsync(AppTable table, FilterGroup? filterTree = null, long? restrictToCreatedBy = null, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        var filterWhere = BuildFilterTreeWhere(filterTree, parameters) + BuildOwnerWhere(restrictToCreatedBy, parameters);
        var sql = $"SELECT COUNT(*) FROM {PhysicalNaming.FullTableName(table.Id)} WHERE IsDeleted = 0{filterWhere}";
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<string, object?>> GetByPublicIdAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid publicId, CancellationToken ct = default)
    {
        var fieldCols = BuildFieldColumnList(fields);
        var sql = $"""
            SELECT Id, PublicId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy{fieldCols}
            FROM {PhysicalNaming.FullTableName(table.Id)}
            WHERE PublicId = @publicId AND IsDeleted = 0
            """;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var row = await connection.QuerySingleOrDefaultAsync(
            new CommandDefinition(sql, new { publicId }, cancellationToken: ct));

        if (row is null) throw new NotFoundException("Record", publicId);
        return ToDictionary(row);
    }

    public async Task<Guid> CreateAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, object?> values, CancellationToken ct = default)
    {
        var relevantFields = fields.Where(f => values.ContainsKey(f.Id)).ToList();
        var parameters = new DynamicParameters();
        parameters.Add("createdBy", QueryContext.UserId);

        string sql;
        if (relevantFields.Count == 0)
        {
            sql = $"""
                INSERT INTO {PhysicalNaming.FullTableName(table.Id)} (CreatedBy)
                OUTPUT INSERTED.PublicId
                VALUES (@createdBy)
                """;
        }
        else
        {
            var colList = string.Join(", ", relevantFields.Select(f => PhysicalNaming.ColumnName(f.Id)));
            var paramList = string.Join(", ", relevantFields.Select(f => $"@{PhysicalNaming.ColumnName(f.Id)}"));
            foreach (var f in relevantFields)
                parameters.Add(PhysicalNaming.ColumnName(f.Id), values[f.Id]);

            sql = $"""
                INSERT INTO {PhysicalNaming.FullTableName(table.Id)} (CreatedBy, {colList})
                OUTPUT INSERTED.PublicId
                VALUES (@createdBy, {paramList})
                """;
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    public async Task UpdateAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid publicId,
        IReadOnlyDictionary<long, object?> values, CancellationToken ct = default)
    {
        var relevantFields = fields.Where(f => values.ContainsKey(f.Id)).ToList();
        if (relevantFields.Count == 0) return;

        var setClauses = string.Join(", ", relevantFields.Select(f => $"{PhysicalNaming.ColumnName(f.Id)} = @{PhysicalNaming.ColumnName(f.Id)}"));
        var sql = $"""
            UPDATE {PhysicalNaming.FullTableName(table.Id)}
            SET {setClauses}, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
            WHERE PublicId = @publicId AND IsDeleted = 0
            """;

        var parameters = new DynamicParameters();
        parameters.Add("publicId", publicId);
        parameters.Add("modifiedBy", QueryContext.UserId);
        foreach (var f in relevantFields)
            parameters.Add(PhysicalNaming.ColumnName(f.Id), values[f.Id]);

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var affected = await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        if (affected == 0) throw new NotFoundException("Record", publicId);
    }

    public async Task DeleteAsync(AppTable table, Guid publicId, CancellationToken ct = default)
    {
        var sql = $"""
            UPDATE {PhysicalNaming.FullTableName(table.Id)}
            SET IsDeleted = 1, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
            WHERE PublicId = @publicId AND IsDeleted = 0
            """;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var affected = await connection.ExecuteAsync(
            new CommandDefinition(sql, new { publicId, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
        if (affected == 0) throw new NotFoundException("Record", publicId);
    }

    public async Task BulkDeleteAsync(AppTable table, IReadOnlyList<Guid> publicIds, CancellationToken ct = default)
    {
        var sql = $"""
            UPDATE {PhysicalNaming.FullTableName(table.Id)}
            SET IsDeleted = 1, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
            WHERE PublicId IN @publicIds AND IsDeleted = 0
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.ExecuteAsync(
            new CommandDefinition(sql, new { publicIds, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
    }

    public async Task<int> BackfillDefaultAsync(AppTable table, AppField field, string defaultValue, CancellationToken ct = default)
    {
        var col = field.IsSystem && !string.IsNullOrEmpty(field.PhysicalColumnName)
            ? field.PhysicalColumnName!
            : PhysicalNaming.ColumnName(field.Id);

        var sql = $"""
            UPDATE {PhysicalNaming.FullTableName(table.Id)}
            SET {col} = @defaultValue
            WHERE IsDeleted = 0 AND ({col} IS NULL OR {col} = '')
            """;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(
            new CommandDefinition(sql, new { defaultValue }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SummarizeAsync(
        AppTable table, AppField groupByField,
        IReadOnlyList<SummaryAggregation> aggregations,
        IReadOnlyList<AppField> allFields,
        string groupByMode = "EqualValues",
        FilterGroup? filterTree = null,
        long? restrictToCreatedBy = null,
        CancellationToken ct = default)
    {
        var groupCol = PhysicalNaming.ColumnName(groupByField.Id);
        var groupExpr = BuildGroupByExpr(groupCol, groupByMode);
        var fieldMap = allFields.ToDictionary(f => f.Id);

        var aggClauses = new List<string> { "COUNT(*) AS [Count]" };
        foreach (var agg in aggregations)
        {
            if (!fieldMap.TryGetValue(agg.FieldId, out var aggField)) continue;
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
            if (clause is not null) aggClauses.Add(clause);
        }

        var parameters = new DynamicParameters();
        var ownerWhere = BuildOwnerWhere(restrictToCreatedBy, parameters);
        var filterWhere = BuildFilterTreeWhere(filterTree, parameters);

        var sql = $"""
            SELECT {groupExpr} AS GroupValue, {string.Join(", ", aggClauses)}
            FROM {PhysicalNaming.FullTableName(table.Id)}
            WHERE IsDeleted = 0{ownerWhere}{filterWhere}
            GROUP BY {groupExpr}
            ORDER BY {groupExpr}
            """;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        return rows.Select(ToDictionary).ToList();
    }

    private static string BuildGroupByExpr(string col, string mode) => mode switch
    {
        "FirstWord" => $"LEFT({col}, CASE WHEN CHARINDEX(' ', {col}) > 0 THEN CHARINDEX(' ', {col}) - 1 ELSE LEN({col}) END)",
        "FirstLetter" => $"LEFT({col}, 1)",
        _ => col,
    };

    private static string BuildFieldColumnList(IReadOnlyList<AppField> fields)
    {
        var customCols = fields.Where(f => !f.IsSystem).Select(f => PhysicalNaming.ColumnName(f.Id)).ToList();
        return customCols.Count > 0 ? ", " + string.Join(", ", customCols) : string.Empty;
    }

    private static string BuildOwnerWhere(long? restrictToCreatedBy, DynamicParameters parameters)
    {
        if (restrictToCreatedBy is null) return string.Empty;
        parameters.Add("ownerUserId", restrictToCreatedBy.Value);
        return " AND CreatedBy = @ownerUserId";
    }

    private static string BuildFilterTreeWhere(FilterGroup? group, DynamicParameters parameters)
    {
        if (group is null || group.Nodes.Count == 0) return string.Empty;
        var paramIdx = 0;
        var fragment = BuildTreeFragment(group, parameters, ref paramIdx);
        return string.IsNullOrEmpty(fragment) ? string.Empty : $" AND ({fragment})";
    }

    private static string BuildTreeFragment(FilterGroup group, DynamicParameters parameters, ref int paramIdx)
    {
        var parts = new List<string>();
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } cond)
            {
                var clause = BuildConditionClause(cond, parameters, ref paramIdx);
                if (clause is not null) parts.Add(clause);
            }
            else if (node.Group is { } sub && sub.Nodes.Count > 0)
            {
                var subSql = BuildTreeFragment(sub, parameters, ref paramIdx);
                if (!string.IsNullOrEmpty(subSql)) parts.Add($"({subSql})");
            }
        }
        if (parts.Count == 0) return string.Empty;
        var joiner = group.Logic?.ToLowerInvariant() == "or" ? " OR " : " AND ";
        return string.Join(joiner, parts);
    }

    private static string? BuildConditionClause(FilterCondition cond, DynamicParameters p, ref int i)
    {
        var col = PhysicalNaming.ColumnName(cond.FieldId);
        var pname = $"fv{i++}";
        switch (cond.Operator)
        {
            case "eq":         p.Add(pname, cond.Value);        return $"{col} = @{pname}";
            case "ne":         p.Add(pname, cond.Value);        return $"{col} <> @{pname}";
            case "gt":         p.Add(pname, cond.Value);        return $"{col} > @{pname}";
            case "gte":        p.Add(pname, cond.Value);        return $"{col} >= @{pname}";
            case "lt":         p.Add(pname, cond.Value);        return $"{col} < @{pname}";
            case "lte":        p.Add(pname, cond.Value);        return $"{col} <= @{pname}";
            case "contains":   p.Add(pname, $"%{cond.Value}%"); return $"{col} LIKE @{pname}";
            case "startsWith": p.Add(pname, $"{cond.Value}%");  return $"{col} LIKE @{pname}";
            case "isEmpty":    i--; return $"({col} IS NULL OR {col} = '')";
            case "isNotEmpty": i--; return $"({col} IS NOT NULL AND {col} <> '')";
            default: i--; return null;
        }
    }

    private static IReadOnlyDictionary<string, object?> ToDictionary(dynamic row)
    {
        var dict = (IDictionary<string, object>)row;
        return dict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value == DBNull.Value ? null : (object?)kvp.Value);
    }
}
