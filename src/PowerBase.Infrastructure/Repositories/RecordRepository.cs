using System.Text.Json;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class RecordRepository : TenantRepositoryBase, IRecordRepository
{
    private readonly IAzureSearchService _searchService;
    private readonly IEncryptionService _encryptionService;
    private readonly IControlConnectionFactory _controlFactory;

    public RecordRepository(
        ITenantConnectionFactory connectionFactory, 
        IQueryContext queryContext,
        IAzureSearchService searchService,
        IEncryptionService encryptionService,
        IControlConnectionFactory controlFactory)
        : base(connectionFactory, queryContext) 
    { 
        _searchService = searchService;
        _encryptionService = encryptionService;
        _controlFactory = controlFactory;
    }

    private async Task<string?> GetDekIfAppLevelEncryptionAsync(System.Data.IDbConnection connection, long appId, CancellationToken ct)
    {
        var sqlConn = connection as Microsoft.Data.SqlClient.SqlConnection;
        if (sqlConn != null)
        {
            var builder = new Microsoft.Data.SqlClient.SqlConnectionStringBuilder(sqlConn.ConnectionString);
            if (builder.ColumnEncryptionSetting == Microsoft.Data.SqlClient.SqlConnectionColumnEncryptionSetting.Enabled)
            {
                return null;
            }
        }
        
        await using var controlConn = _controlFactory.Create();
        await controlConn.OpenAsync(ct);
        var sql = "SELECT SecurityOptions FROM meta.App WHERE Id = @appId";
        return await controlConn.ExecuteScalarAsync<string>(new CommandDefinition(sql, new { appId }, cancellationToken: ct));
    }

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

        var fieldLookup = fields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
        var filterWhere = BuildFilterTreeWhere(filterTree, parameters, fieldLookup) + BuildOwnerWhere(restrictToCreatedBy, parameters);
        var orderBy = sortFields?.Count > 0
            ? string.Join(", ", sortFields
                .Where(s => !fieldLookup.TryGetValue(s.FieldId, out var sf2) || !PhysicalNaming.IsComputedTypeCode(sf2.TypeCode))
                .Select(s =>
                {
                    var colName = fieldLookup.TryGetValue(s.FieldId, out var sf) && sf.IsSystem
                        ? sf.PhysicalColumnName!
                        : PhysicalNaming.ColumnName((int)s.FieldId);
                    return $"{colName} {(s.Desc ? "DESC" : "ASC")}";
                })
                .DefaultIfEmpty("Id"))
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
        var resultList = rows.Select(ToDictionary).ToList();

        var dek = await GetDekIfAppLevelEncryptionAsync(connection, table.AppId, ct);
        if (!string.IsNullOrEmpty(dek) && fields.Any(f => f.IsEncrypted))
        {
            var encryptedFields = fields.Where(f => f.IsEncrypted).ToList();
            foreach (var dict in resultList)
            {
                foreach (var f in encryptedFields)
                {
                    var col = PhysicalNaming.ColumnName(f.Fid!.Value);
                    if (dict.TryGetValue(col, out var val) && val is string cipherText && !string.IsNullOrEmpty(cipherText))
                    {
                        var mutDict = (System.Collections.Generic.IDictionary<string, object?>)dict;
                        mutDict[col] = await _encryptionService.DecryptDataAsync(cipherText, dek, QueryContext.TenantId, table.AppId, ct);
                    }
                }
            }
        }
        
        return resultList;
    }

    public async Task<int> CountAsync(AppTable table, IReadOnlyList<AppField> fields, FilterGroup? filterTree = null, long? restrictToCreatedBy = null, CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        var fieldLookup = fields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
        var filterWhere = BuildFilterTreeWhere(filterTree, parameters, fieldLookup) + BuildOwnerWhere(restrictToCreatedBy, parameters);
        var sql = $"SELECT COUNT(*) FROM {PhysicalNaming.FullTableName(table.Id)} WHERE IsDeleted = 0{filterWhere}";
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, parameters, cancellationToken: ct));
    }

    public async Task<bool> ExistsAsync(AppTable table, long recordId, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM {PhysicalNaming.FullTableName(table.Id)} WHERE Id = @recordId AND IsDeleted = 0
            ) THEN 1 ELSE 0 END AS BIT)
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(sql, new { recordId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<long>> GetIdsByPublicIdsAsync(AppTable table, IReadOnlyCollection<Guid> publicIds, CancellationToken ct = default)
    {
        if (publicIds.Count == 0) return [];
        var sql = $"SELECT Id FROM {PhysicalNaming.FullTableName(table.Id)} WHERE PublicId IN @publicIds AND IsDeleted = 0";
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var ids = await connection.QueryAsync<long>(new CommandDefinition(sql, new { publicIds }, cancellationToken: ct));
        return ids.AsList();
    }

    public async Task<int> CountReferencingAsync(AppTable childTable, int referenceFid, long parentRecordId, CancellationToken ct = default)
    {
        var col = PhysicalNaming.ColumnName(referenceFid);
        var sql = $"SELECT COUNT(*) FROM {PhysicalNaming.FullTableName(childTable.Id)} WHERE IsDeleted = 0 AND {col} = @parentRecordId";
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { parentRecordId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ReferenceOption>> SearchForReferenceAsync(
        AppTable parentTable, IReadOnlyList<AppField> labelFields, string? search, int take, CancellationToken ct = default)
    {
        take = Math.Clamp(take, 1, 200);

        var parameters = new DynamicParameters();
        var where = "IsDeleted = 0";

        var searchColExpr = labelFields.Count > 0 ? LabelColumnExpr(labelFields[0]) : "CAST(Id AS NVARCHAR(400))";
        if (!string.IsNullOrWhiteSpace(search))
        {
            parameters.Add("search", $"%{search}%");
            if (labelFields.Count > 0)
            {
                var searchConditions = labelFields.Select(f => $"{LabelColumnExpr(f)} LIKE @search");
                where += $" AND ({string.Join(" OR ", searchConditions)})";
            }
            else
            {
                where += $" AND {searchColExpr} LIKE @search";
            }
        }
        parameters.Add("take", take);

        // Id is returned as text: the row Id by default, or (translated by the caller) the parent
        // table's Set-Key key-field value — either way, exactly what the reference column stores.
        var selectCols = new List<string> { "CAST(Id AS NVARCHAR(400)) AS Id" };
        if (labelFields.Count > 0) selectCols.Add($"{LabelColumnExpr(labelFields[0])} AS Value1");
        if (labelFields.Count > 1) selectCols.Add($"{LabelColumnExpr(labelFields[1])} AS Value2");
        if (labelFields.Count > 2) selectCols.Add($"{LabelColumnExpr(labelFields[2])} AS Value3");
        
        if (labelFields.Count == 0) selectCols.Add($"{searchColExpr} AS Value1");

        var sql = $"""
            SELECT TOP (@take) {string.Join(", ", selectCols)}
            FROM {PhysicalNaming.FullTableName(parentTable.Id)}
            WHERE {where}
            ORDER BY {searchColExpr}
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<ReferenceOption>(new CommandDefinition(sql, parameters, cancellationToken: ct));
        return rows.AsList();
    }

    public async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>>> GetRowsByIdsAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        var result = new Dictionary<long, IReadOnlyDictionary<string, object?>>();
        if (ids.Count == 0) return result;

        var fieldCols = BuildFieldColumnList(fields);
        var sql = $"""
            SELECT Id, PublicId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy{fieldCols}
            FROM {PhysicalNaming.FullTableName(table.Id)}
            WHERE IsDeleted = 0 AND Id IN @ids
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync(new CommandDefinition(sql, new { ids }, cancellationToken: ct));
        
        var dek = await GetDekIfAppLevelEncryptionAsync(connection, table.AppId, ct);
        var encryptedFields = fields.Where(f => f.IsEncrypted).ToList();
        
        foreach (var row in rows)
        {
            IReadOnlyDictionary<string, object?> dict = ToDictionary(row);
            
            if (!string.IsNullOrEmpty(dek) && encryptedFields.Count > 0)
            {
                foreach (var f in encryptedFields)
                {
                    var col = PhysicalNaming.ColumnName(f.Fid!.Value);
                    if (dict.TryGetValue(col, out var val) && val is string cipherText && !string.IsNullOrEmpty(cipherText))
                    {
                        var mutDict = (System.Collections.Generic.IDictionary<string, object?>)dict;
                        mutDict[col] = await _encryptionService.DecryptDataAsync(cipherText, dek, QueryContext.TenantId, table.AppId, ct);
                    }
                }
            }
            
            if (dict.TryGetValue("Id", out var idVal) && idVal is not null)
                result[Convert.ToInt64(idVal)] = dict;
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<object, object?>> AggregateByReferenceAsync(
        AppTable childTable, int referenceFid, string function, int? targetFid,
        IReadOnlyCollection<object> parentKeyValues, FilterGroup? filterTree, string? targetSubField = null,
        CancellationToken ct = default)
    {
        var result = new Dictionary<object, object?>();
        if (parentKeyValues.Count == 0) return result;

        var refCol = PhysicalNaming.ColumnName(referenceFid);
        // Address sub-field targeting: aggregate the JSON_VALUE-extracted sub-key instead of the
        // raw column, same JSON_VALUE pattern already used for Address report filters below.
        string TargetColExpr() => targetFid.HasValue
            ? (string.IsNullOrWhiteSpace(targetSubField)
                ? PhysicalNaming.ColumnName(targetFid.Value)
                : $"JSON_VALUE({PhysicalNaming.ColumnName(targetFid.Value)}, '$.{System.Text.RegularExpressions.Regex.Replace(targetSubField, "[^a-zA-Z0-9_]", "")}')")
            : "";
        var aggExpr = function switch
        {
            "Count" => "COUNT(*)",
            "Exists" => "CAST(CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END AS BIT)",
            "Sum" when targetFid.HasValue => $"SUM(CAST({TargetColExpr()} AS DECIMAL(18,4)))",
            "Avg" when targetFid.HasValue => $"AVG(CAST({TargetColExpr()} AS DECIMAL(18,4)))",
            "Min" when targetFid.HasValue => $"MIN({TargetColExpr()})",
            "Max" when targetFid.HasValue => $"MAX({TargetColExpr()})",
            _ => "COUNT(*)",
        };

        var parameters = new DynamicParameters();
        parameters.Add("parentKeyValues", parentKeyValues);
        var filterWhere = BuildFilterTreeWhere(filterTree, parameters);

        var sql = $"""
            SELECT {refCol} AS ParentKey, {aggExpr} AS Value
            FROM {PhysicalNaming.FullTableName(childTable.Id)}
            WHERE IsDeleted = 0 AND {refCol} IN @parentKeyValues{filterWhere}
            GROUP BY {refCol}
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        foreach (var row in rows)
        {
            var dict = (IDictionary<string, object>)row;
            if (dict.TryGetValue("ParentKey", out var pk) && pk is not null && pk != DBNull.Value)
                result[pk] = dict.TryGetValue("Value", out var v) && v != DBNull.Value ? v : null;
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<long, object?>> GetColumnValuesByIdsAsync(
        AppTable table, string columnName, IReadOnlyCollection<long> ids, CancellationToken ct = default)
    {
        var result = new Dictionary<long, object?>();
        if (ids.Count == 0) return result;

        var sql = $"""
            SELECT Id, {columnName} AS KeyColumnValue
            FROM {PhysicalNaming.FullTableName(table.Id)}
            WHERE IsDeleted = 0 AND Id IN @ids
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync(new CommandDefinition(sql, new { ids }, cancellationToken: ct));
        foreach (var row in rows)
        {
            var dict = (IDictionary<string, object>)row;
            if (dict.TryGetValue("Id", out var idVal) && idVal is not null)
                result[Convert.ToInt64(idVal)] = dict.TryGetValue("KeyColumnValue", out var v) && v != DBNull.Value ? v : null;
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<object, long>> GetIdsByColumnValuesAsync(
        AppTable table, string columnName, IReadOnlyCollection<object> values, CancellationToken ct = default)
    {
        var result = new Dictionary<object, long>();
        if (values.Count == 0) return result;

        // Compare native-typed values directly (no string cast) to avoid any SQL-vs-.NET formatting
        // mismatch for DECIMAL/DATE columns.
        var sql = $"""
            SELECT Id, {columnName} AS KeyColumnValue
            FROM {PhysicalNaming.FullTableName(table.Id)}
            WHERE IsDeleted = 0 AND {columnName} IN @values
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync(new CommandDefinition(sql, new { values }, cancellationToken: ct));
        foreach (var row in rows)
        {
            var dict = (IDictionary<string, object>)row;
            if (dict.TryGetValue("KeyColumnValue", out var kv) && kv is not null && kv != DBNull.Value
                && dict.TryGetValue("Id", out var idVal) && idVal is not null)
                result[kv] = Convert.ToInt64(idVal);
        }
        return result;
    }

    private static string LabelColumnExpr(AppField? labelField)
    {
        if (labelField is null) return "CAST(Id AS NVARCHAR(400))";
        var col = labelField.IsSystem && !string.IsNullOrEmpty(labelField.PhysicalColumnName)
            ? labelField.PhysicalColumnName!
            : PhysicalNaming.ColumnName(labelField.Fid!.Value);
        return $"CAST({col} AS NVARCHAR(400))";
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
        
        var dict = ToDictionary(row);
        var dek = await GetDekIfAppLevelEncryptionAsync(connection, table.AppId, ct);
        if (!string.IsNullOrEmpty(dek) && fields.Any(f => f.IsEncrypted))
        {
            foreach (var f in fields.Where(f => f.IsEncrypted))
            {
                var col = PhysicalNaming.ColumnName(f.Fid!.Value);
                if (dict.TryGetValue(col, out var val) && val is string cipherText && !string.IsNullOrEmpty(cipherText))
                {
                    var mutDict = (System.Collections.Generic.IDictionary<string, object?>)dict;
                    mutDict[col] = await _encryptionService.DecryptDataAsync(cipherText, dek, QueryContext.TenantId, table.AppId, ct);
                }
            }
        }
        
        return dict;
    }

    public async Task<Guid> CreateAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, object?> values, CancellationToken ct = default)
    {
        var relevantFields = fields.Where(f => f.Fid.HasValue && values.ContainsKey((long)f.Fid.Value) && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)).ToList();
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
            var colParts = new List<string>();
            var paramParts = new List<string>();
            foreach (var f in relevantFields)
            {
                var col = PhysicalNaming.ColumnName(f.Fid!.Value);
                if (PhysicalNaming.IsRangeTypeCode(f.TypeCode))
                {
                    var endCol = PhysicalNaming.EndColumnName(f.Fid!.Value);
                    var (startVal, endVal) = SplitRangeValue(values[(long)f.Fid.Value]);
                    colParts.Add(col); paramParts.Add($"@{col}"); parameters.Add(col, startVal);
                    colParts.Add(endCol); paramParts.Add($"@{endCol}"); parameters.Add(endCol, endVal);
                }
                else
                {
                    colParts.Add(col); paramParts.Add($"@{col}"); parameters.Add(col, values[(long)f.Fid.Value]);
                }
            }

            sql = $"""
                INSERT INTO {PhysicalNaming.FullTableName(table.Id)} (CreatedBy, {string.Join(", ", colParts)})
                OUTPUT INSERTED.PublicId
                VALUES (@createdBy, {string.Join(", ", paramParts)})
                """;
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        
        var dek = await GetDekIfAppLevelEncryptionAsync(connection, table.AppId, ct);
        if (!string.IsNullOrEmpty(dek) && fields.Any(f => f.IsEncrypted))
        {
            foreach (var f in relevantFields.Where(x => x.IsEncrypted))
            {
                var col = PhysicalNaming.ColumnName(f.Fid!.Value);
                if (parameters.Get<object>(col) is string plainText && !string.IsNullOrEmpty(plainText))
                {
                    var cipher = await _encryptionService.EncryptDataAsync(plainText, dek, QueryContext.TenantId, table.AppId, ct);
                    // Replace the parameter with ciphertext
                    var valDict = (System.Collections.Generic.IDictionary<string, object>)((Dapper.DynamicParameters)parameters).GetType().GetField("parameters", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!.GetValue(parameters)!;
                    valDict[col] = valDict[col].GetType().GetProperty("Value")!.GetValue(valDict[col])!;
                    // The above reflection is unsafe for Dapper. A safer way is simply re-creating parameters or we can just replace values dictionary beforehand.
                    // Wait, we can just call Add(col, cipher) to overwrite!
                    parameters.Add(col, cipher);
                }
            }
        }
        
        var insertedPublicId = await connection.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, parameters, cancellationToken: ct));
        
        // Push only searchable fields to Azure AI Search (using ORIGINAL values, not ciphertext)
        var searchableValues = fields
            .Where(f => f.IsSearchable && f.Fid.HasValue && values.ContainsKey((long)f.Fid.Value))
            .ToDictionary(f => (long)f.Fid.Value, f => values[(long)f.Fid.Value]);

        await _searchService.IndexRecordAsync(table.Id, insertedPublicId, searchableValues, ct);
        
        return insertedPublicId;
    }

    public async Task UpdateAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid publicId,
        IReadOnlyDictionary<long, object?> values, CancellationToken ct = default)
    {
        var relevantFields = fields.Where(f => f.Fid.HasValue && values.ContainsKey((long)f.Fid.Value) && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)).ToList();
        if (relevantFields.Count == 0) return;

        var parameters = new DynamicParameters();
        parameters.Add("publicId", publicId);
        parameters.Add("modifiedBy", QueryContext.UserId);

        var setClauses = new List<string>();
        foreach (var f in relevantFields)
        {
            var col = PhysicalNaming.ColumnName(f.Fid!.Value);
            if (PhysicalNaming.IsRangeTypeCode(f.TypeCode))
            {
                var endCol = PhysicalNaming.EndColumnName(f.Fid!.Value);
                var (startVal, endVal) = SplitRangeValue(values[(long)f.Fid.Value]);
                setClauses.Add($"{col} = @{col}"); parameters.Add(col, startVal);
                setClauses.Add($"{endCol} = @{endCol}"); parameters.Add(endCol, endVal);
            }
            else
            {
                setClauses.Add($"{col} = @{col}"); parameters.Add(col, values[(long)f.Fid.Value]);
            }
        }

        var sql = $"""
            UPDATE {PhysicalNaming.FullTableName(table.Id)}
            SET {string.Join(", ", setClauses)}, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
            WHERE PublicId = @publicId AND IsDeleted = 0
            """;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        
        var dek = await GetDekIfAppLevelEncryptionAsync(connection, table.AppId, ct);
        if (!string.IsNullOrEmpty(dek) && fields.Any(f => f.IsEncrypted))
        {
            foreach (var f in relevantFields.Where(x => x.IsEncrypted))
            {
                var col = PhysicalNaming.ColumnName(f.Fid!.Value);
                if (parameters.Get<object>(col) is string plainText && !string.IsNullOrEmpty(plainText))
                {
                    var cipher = await _encryptionService.EncryptDataAsync(plainText, dek, QueryContext.TenantId, table.AppId, ct);
                    parameters.Add(col, cipher); // Dapper Add overwrites existing keys
                }
            }
        }
        
        var affected = await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        if (affected == 0) throw new NotFoundException("Record", publicId);

        // Update Azure AI Search with only searchable fields (using ORIGINAL plaintext values)
        var searchableValues = fields
            .Where(f => f.IsSearchable && f.Fid.HasValue && values.ContainsKey((long)f.Fid.Value))
            .ToDictionary(f => (long)f.Fid.Value, f => values[(long)f.Fid.Value]);

        await _searchService.IndexRecordAsync(table.Id, publicId, searchableValues, ct);
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

        // Remove from Azure AI Search
        await _searchService.DeleteRecordAsync(table.Id, publicId, ct);
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

        // Remove from Azure AI Search
        await _searchService.BulkDeleteRecordsAsync(table.Id, publicIds, ct);
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
        AppField? seriesField = null,
        string seriesMode = "EqualValues",
        CancellationToken ct = default)
    {
        var groupCol = groupByField.IsSystem && !string.IsNullOrEmpty(groupByField.PhysicalColumnName)
            ? groupByField.PhysicalColumnName!
            : PhysicalNaming.ColumnName(groupByField.Fid!.Value);
        var groupExpr = BuildGroupByExpr(groupCol, groupByMode);
        var fieldMap = allFields.GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());

        string? seriesExpr = null;
        if (seriesField is not null)
        {
            var seriesCol = seriesField.IsSystem && !string.IsNullOrEmpty(seriesField.PhysicalColumnName)
                ? seriesField.PhysicalColumnName!
                : PhysicalNaming.ColumnName(seriesField.Fid!.Value);
            seriesExpr = BuildGroupByExpr(seriesCol, seriesMode);
        }

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
        // fieldMap guards against computed/Formula-type conditions reaching SQL as a reference
        // to a nonexistent f_{fid} column — see BuildConditionClause's IsComputedTypeCode check.
        var filterWhere = BuildFilterTreeWhere(filterTree, parameters, fieldMap);

        var selectList = seriesExpr is null
            ? $"{groupExpr} AS GroupValue, {string.Join(", ", aggClauses)}"
            : $"{groupExpr} AS GroupValue, {seriesExpr} AS SeriesValue, {string.Join(", ", aggClauses)}";
        var groupByList = seriesExpr is null ? groupExpr : $"{groupExpr}, {seriesExpr}";

        var sql = $"""
            SELECT {selectList}
            FROM {PhysicalNaming.FullTableName(table.Id)}
            WHERE IsDeleted = 0{ownerWhere}{filterWhere}
            GROUP BY {groupByList}
            ORDER BY {groupByList}
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
        var cols = new List<string>();
        // Computed (Formula) fields have no physical column — they are projected in at read time.
        foreach (var f in fields.Where(f => !f.IsSystem && f.Fid.HasValue && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)))
        {
            cols.Add(PhysicalNaming.ColumnName(f.Fid!.Value));
            if (PhysicalNaming.IsRangeTypeCode(f.TypeCode))
                cols.Add(PhysicalNaming.EndColumnName(f.Fid!.Value));
        }
        return cols.Count > 0 ? ", " + string.Join(", ", cols) : string.Empty;
    }

    private static string BuildOwnerWhere(long? restrictToCreatedBy, DynamicParameters parameters)
    {
        if (restrictToCreatedBy is null) return string.Empty;
        parameters.Add("ownerUserId", restrictToCreatedBy.Value);
        return " AND CreatedBy = @ownerUserId";
    }

    private static string BuildFilterTreeWhere(FilterGroup? group, DynamicParameters parameters,
        IReadOnlyDictionary<long, AppField>? fieldLookup = null)
    {
        if (group is null || group.Nodes.Count == 0) return string.Empty;
        var paramIdx = 0;
        var fragment = BuildTreeFragment(group, parameters, ref paramIdx, fieldLookup);
        return string.IsNullOrEmpty(fragment) ? string.Empty : $" AND ({fragment})";
    }

    private static string BuildTreeFragment(FilterGroup group, DynamicParameters parameters, ref int paramIdx,
        IReadOnlyDictionary<long, AppField>? fieldLookup = null)
    {
        var parts = new List<string>();
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } cond)
            {
                var clause = BuildConditionClause(cond, parameters, ref paramIdx, fieldLookup);
                if (clause is not null) parts.Add(clause);
            }
            else if (node.Group is { } sub && sub.Nodes.Count > 0)
            {
                var subSql = BuildTreeFragment(sub, parameters, ref paramIdx, fieldLookup);
                if (!string.IsNullOrEmpty(subSql)) parts.Add($"({subSql})");
            }
        }
        if (parts.Count == 0) return string.Empty;
        var joiner = group.Logic?.ToLowerInvariant() == "or" ? " OR " : " AND ";
        return string.Join(joiner, parts);
    }

    /// <summary>
    /// Splits a range field value (sent as JSON object or IDictionary) into start and end SQL parameters.
    /// Accepts: JsonElement {"start":x,"end":y}, Dictionary, or null.
    /// </summary>
    private static (object? start, object? end) SplitRangeValue(object? value)
    {
        if (value is null) return (null, null);
        if (value is JsonElement je)
        {
            var startVal = je.TryGetProperty("start", out var s) ? (object?)s.ToString() : null;
            var endVal   = je.TryGetProperty("end",   out var e) ? (object?)e.ToString() : null;
            // Normalise empty strings to null
            if (startVal is string ss && string.IsNullOrEmpty(ss)) startVal = null;
            if (endVal   is string es && string.IsNullOrEmpty(es)) endVal   = null;
            return (startVal, endVal);
        }
        if (value is System.Collections.IDictionary dict)
        {
            return (dict.Contains("start") ? dict["start"] : null,
                    dict.Contains("end")   ? dict["end"]   : null);
        }
        // Scalar fallback — treat as start only
        return (value, null);
    }

    private static string? BuildConditionClause(FilterCondition cond, DynamicParameters p, ref int i,
        IReadOnlyDictionary<long, AppField>? fieldLookup = null)
    {
        // Skip formula/computed fields — they have no physical column; filtered in-memory instead.
        if (fieldLookup != null && fieldLookup.TryGetValue(cond.FieldId, out var checkField)
            && PhysicalNaming.IsComputedTypeCode(checkField.TypeCode))
            return null;

        // Use the physical column name for system fields (Id, CreatedOn, etc.) rather than f_{fid}
        AppField? resolvedField = null;
        string col;
        if (fieldLookup != null && fieldLookup.TryGetValue(cond.FieldId, out var f))
        {
            resolvedField = f;
            col = f.IsSystem ? f.PhysicalColumnName! : PhysicalNaming.ColumnName((int)cond.FieldId);
        }
        else
        {
            col = PhysicalNaming.ColumnName((int)cond.FieldId);
        }

        // Range field: SubField "start" targets f_{fid}, "end" targets f_{fid}_e
        if (resolvedField != null && PhysicalNaming.IsRangeTypeCode(resolvedField.TypeCode) && !string.IsNullOrWhiteSpace(cond.SubField))
        {
            col = cond.SubField == "end"
                ? PhysicalNaming.EndColumnName((int)cond.FieldId)
                : PhysicalNaming.ColumnName((int)cond.FieldId);
        }
        var pname = $"fv{i++}";

        // For Address JSON sub-fields use JSON_VALUE; range fields already have the correct column resolved above
        string colExpr;
        var isRangeSubField = resolvedField != null && PhysicalNaming.IsRangeTypeCode(resolvedField.TypeCode);
        if (!string.IsNullOrWhiteSpace(cond.SubField) && !isRangeSubField)
        {
            var safeSubField = System.Text.RegularExpressions.Regex.Replace(cond.SubField, "[^a-zA-Z0-9_]", "");
            colExpr = $"JSON_VALUE({col}, '$.{safeSubField}')";
        }
        else
        {
            colExpr = col;
        }
        
        switch (cond.Operator)
        {
            case "eq":             p.Add(pname, cond.Value);        return $"{colExpr} = @{pname}";
            case "ne":             p.Add(pname, cond.Value);        return $"{colExpr} <> @{pname}";
            case "gt":              p.Add(pname, cond.Value);        return $"{colExpr} > @{pname}";
            case "gte":            p.Add(pname, cond.Value);        return $"{colExpr} >= @{pname}";
            case "lt":              p.Add(pname, cond.Value);        return $"{colExpr} < @{pname}";
            case "lte":            p.Add(pname, cond.Value);        return $"{colExpr} <= @{pname}";
            case "date_eq":        p.Add(pname, cond.Value);        return $"CAST({colExpr} AS DATE) = @{pname}";
            case "contains":       p.Add(pname, $"%{cond.Value}%"); return $"{colExpr} LIKE @{pname}";
            case "notContains":    p.Add(pname, $"%{cond.Value}%"); return $"{colExpr} NOT LIKE @{pname}";
            case "startsWith":     p.Add(pname, $"{cond.Value}%");  return $"{colExpr} LIKE @{pname}";
            case "notStartsWith":  p.Add(pname, $"{cond.Value}%");  return $"{colExpr} NOT LIKE @{pname}";
            case "isEmpty":    i--; return $"({colExpr} IS NULL OR {colExpr} = '')";
            case "isNotEmpty": i--; return $"({colExpr} IS NOT NULL AND {colExpr} <> '')";
            case "in":
            case "notIn":
            {
                var values = ParseValueList(cond.Value);
                if (values.Count == 0) { i--; return null; }
                var names = new List<string>(values.Count);
                foreach (var v in values)
                {
                    var pn = $"fv{i++}";
                    p.Add(pn, v);
                    names.Add($"@{pn}");
                }
                var op = cond.Operator == "in" ? "IN" : "NOT IN";
                return $"{colExpr} {op} ({string.Join(",", names)})";
            }
            default: i--; return null;
        }
    }

    /// <summary>
    /// Parses a filter condition's Value as a list for the "in"/"notIn" operators. The wire
    /// format is a JSON string array (e.g. ["a","b"]) so FilterCondition.Value stays a single
    /// string end to end, no model/schema change needed. Falls back to a comma split for any
    /// caller that sends a plain delimited string instead of JSON.
    /// </summary>
    private static List<string> ParseValueList(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(raw);
            if (arr != null) return arr.Where(v => !string.IsNullOrEmpty(v)).ToList();
        }
        catch (JsonException) { /* not JSON — fall through to comma split */ }
        return raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
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
            SELECT DISTINCT CAST({selectExpr} AS NVARCHAR(MAX)) 
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
                     using var doc = System.Text.Json.JsonDocument.Parse(v);
                    if (doc.RootElement.TryGetProperty("number", out var numProp))
                    {
                       var num = numProp.GetString();
                    if (!string.IsNullOrWhiteSpace(num)) return $"{v}|{num}";
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

    public async Task<bool> HasNullsAsync(AppTable table, AppField field, CancellationToken ct = default)
    {
        var col = PhysicalNaming.ColumnName(field.Fid!.Value);
        var sql = $"""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM {PhysicalNaming.FullTableName(table.Id)}
                WHERE IsDeleted = 0 AND ({col} IS NULL OR CAST({col} AS NVARCHAR(MAX)) = '')
            ) THEN 1 ELSE 0 END AS BIT)
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task RewriteReferenceColumnAsync(
        AppTable childTable, string oldColumn, string newColumn,
        IReadOnlyDictionary<object, object?> oldToNewValue, CancellationToken ct = default)
    {
        if (oldToNewValue.Count == 0) return;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        // Chunked to stay well under SQL Server's ~2100-parameter limit (2 params per mapped row).
        const int chunkSize = 500;
        foreach (var chunk in oldToNewValue.Chunk(chunkSize))
        {
            var parameters = new DynamicParameters();
            var valueRows = new List<string>(chunk.Length);
            for (var i = 0; i < chunk.Length; i++)
            {
                parameters.Add($"oldId{i}", chunk[i].Key);
                parameters.Add($"newVal{i}", chunk[i].Value ?? (object)DBNull.Value);
                valueRows.Add($"(@oldId{i}, @newVal{i})");
            }

            var sql = $"""
                UPDATE t SET t.{newColumn} = m.NewValue
                FROM {PhysicalNaming.FullTableName(childTable.Id)} t
                JOIN (VALUES {string.Join(", ", valueRows)}) AS m(OldParentId, NewValue) ON t.{oldColumn} = m.OldParentId
                WHERE t.IsDeleted = 0
                """;
            await connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        }
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
