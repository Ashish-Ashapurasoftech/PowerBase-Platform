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
        var fieldLookup = fields.GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
        var orderBy = sortFields?.Count > 0
            ? string.Join(", ", sortFields.Select(s =>
            {
                var colName = fieldLookup.TryGetValue(s.FieldId, out var sf) && sf.IsSystem
                    ? sf.PhysicalColumnName!
                    : PhysicalNaming.ColumnName((int)s.FieldId);
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
        var relevantFields = fields.Where(f => f.Fid.HasValue && values.ContainsKey((long)f.Fid.Value)).ToList();
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
            var colList = string.Join(", ", relevantFields.Select(f => PhysicalNaming.ColumnName(f.Fid!.Value)));
            var paramList = string.Join(", ", relevantFields.Select(f => $"@{PhysicalNaming.ColumnName(f.Fid!.Value)}"));
            foreach (var f in relevantFields)
                parameters.Add(PhysicalNaming.ColumnName(f.Fid!.Value), values[(long)f.Fid.Value]);

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
        var relevantFields = fields.Where(f => f.Fid.HasValue && values.ContainsKey((long)f.Fid.Value)).ToList();
        if (relevantFields.Count == 0) return;

        var setClauses = string.Join(", ", relevantFields.Select(f => $"{PhysicalNaming.ColumnName(f.Fid!.Value)} = @{PhysicalNaming.ColumnName(f.Fid!.Value)}"));
        var sql = $"""
            UPDATE {PhysicalNaming.FullTableName(table.Id)}
            SET {setClauses}, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
            WHERE PublicId = @publicId AND IsDeleted = 0
            """;

        var parameters = new DynamicParameters();
        parameters.Add("publicId", publicId);
        parameters.Add("modifiedBy", QueryContext.UserId);
        foreach (var f in relevantFields)
            parameters.Add(PhysicalNaming.ColumnName(f.Fid!.Value), values[(long)f.Fid.Value]);

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
            : PhysicalNaming.ColumnName(field.Fid!.Value);

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
        var groupCol = groupByField.IsSystem && !string.IsNullOrEmpty(groupByField.PhysicalColumnName)
            ? groupByField.PhysicalColumnName!
            : PhysicalNaming.ColumnName(groupByField.Fid!.Value);
        var groupExpr = BuildGroupByExpr(groupCol, groupByMode);
        var fieldMap = allFields.GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());

        var aggClauses = new List<string> { "COUNT(*) AS [Count]" };
        foreach (var agg in aggregations)
        {
            if (!fieldMap.TryGetValue(agg.FieldId, out var aggField)) continue;
            var col = PhysicalNaming.ColumnName((int)agg.FieldId);
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
        var customCols = fields.Where(f => !f.IsSystem && f.Fid.HasValue).Select(f => PhysicalNaming.ColumnName(f.Fid!.Value)).ToList();
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
        var col = PhysicalNaming.ColumnName((int)cond.FieldId);
        var pname = $"fv{i++}";
        
        // For JSON sub-field (Address sub-field filtering)
        string colExpr;
        if (!string.IsNullOrWhiteSpace(cond.SubField))
        {
            // Sanitize sub-field name to prevent SQL injection (only allow alphanumeric)
            var safeSubField = System.Text.RegularExpressions.Regex.Replace(cond.SubField, "[^a-zA-Z0-9_]", "");
            colExpr = $"JSON_VALUE({col}, '$.{safeSubField}')";
        }
        else
        {
            colExpr = col;
        }
        
        switch (cond.Operator)
        {
            case "eq":         p.Add(pname, cond.Value);        return $"{colExpr} = @{pname}";
            case "ne":         p.Add(pname, cond.Value);        return $"{colExpr} <> @{pname}";
            case "gt":         p.Add(pname, cond.Value);        return $"{colExpr} > @{pname}";
            case "gte":        p.Add(pname, cond.Value);        return $"{colExpr} >= @{pname}";
            case "lt":         p.Add(pname, cond.Value);        return $"{colExpr} < @{pname}";
            case "lte":        p.Add(pname, cond.Value);        return $"{colExpr} <= @{pname}";
            case "contains":   p.Add(pname, $"%{cond.Value}%"); return $"{colExpr} LIKE @{pname}";
            case "startsWith": p.Add(pname, $"{cond.Value}%");  return $"{colExpr} LIKE @{pname}";
            case "isEmpty":    i--; return $"({colExpr} IS NULL OR {colExpr} = '')";
            case "isNotEmpty": i--; return $"({colExpr} IS NOT NULL AND {colExpr} <> '')";
            default: i--; return null;
        }
    }

    public async Task<(IReadOnlyList<string> Values, bool ExceedsLimit)> GetDistinctFieldValuesAsync(
        AppTable table, AppField field, int limit, string? subField = null, CancellationToken ct = default)
    {
        var col = field.IsSystem && !string.IsNullOrEmpty(field.PhysicalColumnName)
            ? field.PhysicalColumnName!
            : PhysicalNaming.ColumnName(field.Fid!.Value);

        string selectExpr;
        string whereExtra = "";
        
        // For Address with a sub-field, use JSON_VALUE
        if (field.TypeCode == "Address" && !string.IsNullOrWhiteSpace(subField))
        {
            var safeSubField = System.Text.RegularExpressions.Regex.Replace(subField, "[^a-zA-Z0-9_]", "");
            selectExpr = $"JSON_VALUE({col}, '$.{safeSubField}')";
            whereExtra = $" AND JSON_VALUE({col}, '$.{safeSubField}') IS NOT NULL AND JSON_VALUE({col}, '$.{safeSubField}') <> ''";
        }
        else
        {
            selectExpr = col;
        }
        
        var sql = $"""
            SELECT DISTINCT {selectExpr} 
            FROM {PhysicalNaming.FullTableName(table.Id)}
            WHERE IsDeleted = 0 AND {col} IS NOT NULL AND CAST({col} AS NVARCHAR(MAX)) <> ''{whereExtra}
            """;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rawValues = await connection.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct));
        
        IEnumerable<string> processedValues = rawValues.Where(v => !string.IsNullOrWhiteSpace(v));
        
        if (field.TypeCode == "MultiSelect")
        {
            processedValues = processedValues
                .SelectMany(v => 
                {
                    try 
                    {
                        if (v.TrimStart().StartsWith("["))
                        {
                            return System.Text.Json.JsonSerializer.Deserialize<string[]>(v) ?? Array.Empty<string>();
                        }
                    } 
                    catch { }
                    return v.Split(',');
                })
                .Select(v => v.Trim())
                .Where(v => !string.IsNullOrWhiteSpace(v));
        }
        else if (field.TypeCode == "Phone")
        {
            // Extract just the phone number from stored JSON {"number":"...","ext":"..."}
            // Ignore the extension completely for dropdown filters, as requested by user
            processedValues = processedValues.Select(v =>
            {
                try
                {
                    var numMatch = System.Text.RegularExpressions.Regex.Match(v, @"number[^:]*:\s*\\?""([^\\""]*)");
                    if (numMatch.Success)
                    {
                        var num = numMatch.Groups[1].Value;
                        if (string.IsNullOrWhiteSpace(num)) return null;
                        return $"{v}|{num}";
                    }
                }
                catch { }
                return v;
            }).Where(v => v != null && !string.IsNullOrWhiteSpace(v));
        }
        else if (field.TypeCode == "Address" && string.IsNullOrWhiteSpace(subField))
        {
            // No sub-field specified: return empty so the frontend uses text mode
            return (new List<string>(), false);
        }
        else if (field.TypeCode == "User")
        {
            // Resolve stored PublicId GUIDs to UserName via meta.AppUser
            var guidList = processedValues.ToList();
            if (guidList.Count > 0)
            {
                var inList = string.Join(",", guidList.Select((_, idx) => $"@uid{idx}"));
                var userNameSql = $"""
                    SELECT CAST(UserPublicId AS NVARCHAR(36)) AS UserPublicId, UserName
                    FROM meta.AppUser
                    WHERE CAST(UserPublicId AS NVARCHAR(36)) IN ({inList}) AND IsDeleted = 0
                    """;
                var nameParams = new DynamicParameters();
                for (int idx = 0; idx < guidList.Count; idx++)
                    nameParams.Add($"uid{idx}", guidList[idx]);
                
                var userRows = await connection.QueryAsync<(string UserPublicId, string UserName)>(
                    new CommandDefinition(userNameSql, nameParams, cancellationToken: ct));
                var nameMap = userRows.ToDictionary(r => r.UserPublicId, r => r.UserName, StringComparer.OrdinalIgnoreCase);
                processedValues = guidList.Select(id => 
                {
                    var name = nameMap.TryGetValue(id, out var n) ? n : id;
                    return $"{id}|{name}";
                });
            }
        }

        var distinctList = processedValues.Distinct().ToList();
        bool exceedsLimit = distinctList.Count > limit;
        
        var results = distinctList.Take(limit).OrderBy(v => v).ToList();
        
        return (results, exceedsLimit);
    }

    public async Task<bool> HasDuplicatesAsync(AppTable table, AppField field, CancellationToken ct = default)
    {
        var col = PhysicalNaming.ColumnName(field.Fid!.Value);
        var sql = $"""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT {col} FROM {PhysicalNaming.FullTableName(table.Id)}
                WHERE IsDeleted = 0 AND {col} IS NOT NULL
                GROUP BY {col} HAVING COUNT(*) > 1
            ) THEN 1 ELSE 0 END AS BIT)
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task<bool> HasAnyDataAsync(AppTable table, AppField field, CancellationToken ct = default)
    {
        var col = PhysicalNaming.ColumnName(field.Fid!.Value);
        var sql = $"""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM {PhysicalNaming.FullTableName(table.Id)}
                WHERE IsDeleted = 0 AND {col} IS NOT NULL AND CAST({col} AS NVARCHAR(MAX)) <> ''
            ) THEN 1 ELSE 0 END AS BIT)
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(sql, cancellationToken: ct));
    }

    private static IReadOnlyDictionary<string, object?> ToDictionary(dynamic row)
    {
        var dict = (IDictionary<string, object>)row;
        return dict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value == DBNull.Value ? null : (object?)kvp.Value);
    }
}
