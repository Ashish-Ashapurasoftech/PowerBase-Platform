using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Validation;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class RecordRepository : TenantRepositoryBase, IRecordRepository
{
    private readonly IMessagePublisher _messagePublisher;
    private readonly IEncryptionService _encryptionService;
    private readonly IControlConnectionFactory _controlConnectionFactory;

    public RecordRepository(
        ITenantConnectionFactory connectionFactory, 
        IQueryContext queryContext,
        IMessagePublisher messagePublisher,
        IEncryptionService encryptionService,
        IControlConnectionFactory controlConnectionFactory)
        : base(connectionFactory, queryContext) 
    { 
        _messagePublisher = messagePublisher;
        _encryptionService = encryptionService;
        _controlConnectionFactory = controlConnectionFactory;
    }

    private Task<Services.FieldEncryptionContext> GetEncryptionContextAsync(
        System.Data.IDbConnection connection, long appId, System.Data.IDbTransaction? transaction = null, CancellationToken ct = default)
        => Services.FieldEncryptionContext.ResolveAsync(connection, appId, QueryContext.TenantId, _encryptionService, transaction, ct);

    public async Task<IReadOnlyDictionary<long, object?>> GetSearchableFieldsAsync(Guid recordPublicId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        
        // Find the table that contains this record
        var tableSql = @"SELECT t.Id, t.AppId, t.Name 
                         FROM RecordMetadata rm 
                         JOIN meta.AppTable t ON rm.TableId = t.Id 
                         WHERE rm.PublicId = @publicId AND rm.TenantId = @tenantId";
        var tableInfo = await connection.QueryFirstOrDefaultAsync<dynamic>(tableSql, new { publicId = recordPublicId, tenantId = QueryContext.TenantId });
        if (tableInfo == null) return new Dictionary<long, object?>();

        // Get fields for this table
        var fieldsSql = "SELECT Id, AppTableId, Name, TypeCode, Settings, PhysicalColumnName, Fid, IsSystem, IsSearchable, IsFilterable, IsEncrypted FROM meta.AppField WHERE AppTableId = @tableId";
        var fields = (await connection.QueryAsync<AppField>(fieldsSql, new { tableId = (long)tableInfo.Id })).ToList();
        
        var searchableFields = fields.Where(f => f.IsSearchable || f.IsFilterable).ToList();
        if (searchableFields.Count == 0) return new Dictionary<long, object?>();

        var fieldCols = BuildFieldColumnList(searchableFields);
        var recordSql = $"SELECT {fieldCols} FROM {PhysicalNaming.TableName((long)tableInfo.Id)} WHERE Id = (SELECT RecordId FROM RecordMetadata WHERE PublicId = @publicId AND TenantId = @tenantId)";
        var rawRow = (await connection.QueryAsync<dynamic>(recordSql, new { publicId = recordPublicId, tenantId = QueryContext.TenantId })).FirstOrDefault();
        if (rawRow == null) return new Dictionary<long, object?>();

        var rowDict = (IDictionary<string, object?>)rawRow;
        var result = new Dictionary<long, object?>();

        var enc = await GetEncryptionContextAsync(connection, (long)tableInfo.AppId, null, ct);

        foreach (var f in searchableFields)
        {
            if (!f.Fid.HasValue) continue;
            var colName = f.IsSystem ? f.PhysicalColumnName! : PhysicalNaming.ColumnName((int)f.Fid.Value);
            
            if (rowDict.TryGetValue(colName, out var val))
            {
                if (f.IsEncrypted && val is string cipherHex)
                {
                    result[(long)f.Fid.Value] = await enc.DecryptValueAsync(cipherHex, ct);
                }
                else
                {
                    result[(long)f.Fid.Value] = val;
                }
            }
        }
        
        return result;
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
        
        // Use mutable Dictionary so DecryptRowsAsync can mutate values in-place
        var mutableRows = rows.Select(r => (IDictionary<string, object?>)ToDictionary(r)).ToList();

        var enc = await GetEncryptionContextAsync(connection, table.AppId, null, ct);
        await enc.DecryptRowsAsync(mutableRows, fields, ct);

        return mutableRows.Cast<IReadOnlyDictionary<string, object?>>().ToList();
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

    public async Task<IReadOnlyDictionary<Guid, long>> GetIdsByPublicIdsMapAsync(AppTable table, IReadOnlyCollection<Guid> publicIds, CancellationToken ct = default)
    {
        if (publicIds.Count == 0) return new Dictionary<Guid, long>();
        var sql = $"SELECT PublicId, Id FROM {PhysicalNaming.FullTableName(table.Id)} WHERE PublicId IN @publicIds AND IsDeleted = 0";
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync<(Guid PublicId, long Id)>(new CommandDefinition(sql, new { publicIds }, cancellationToken: ct));
        return rows.ToDictionary(r => r.PublicId, r => r.Id);
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

        var enc = await GetEncryptionContextAsync(connection, table.AppId, null, ct);

        foreach (var row in rows)
        {
            IReadOnlyDictionary<string, object?> dict = ToDictionary(row);
            await enc.DecryptRowAsync((System.Collections.Generic.IDictionary<string, object?>)dict, fields, ct);

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
        var enc = await GetEncryptionContextAsync(connection, table.AppId, null, ct);
        await enc.DecryptRowAsync((System.Collections.Generic.IDictionary<string, object?>)dict, fields, ct);

        return dict;
    }

    public async Task<long> GetRecordIdByPublicIdAsync(AppTable table, Guid publicId, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var sql = $"SELECT Id FROM {PhysicalNaming.FullTableName(table.Id)} WHERE PublicId = @publicId";
        if (transaction is not null)
        {
            return await transaction.Connection!.QuerySingleAsync<long>(new CommandDefinition(sql, new { publicId }, transaction, cancellationToken: ct));
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleAsync<long>(new CommandDefinition(sql, new { publicId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<Guid, long>> GetRecordIdsByPublicIdsAsync(AppTable table, IReadOnlyCollection<Guid> publicIds, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var sql = $"SELECT PublicId, Id FROM {PhysicalNaming.FullTableName(table.Id)} WHERE PublicId IN @publicIds";
        if (transaction is not null)
        {
            var rows = await transaction.Connection!.QueryAsync<(Guid PublicId, long Id)>(new CommandDefinition(sql, new { publicIds }, transaction, cancellationToken: ct));
            return rows.ToDictionary(x => x.PublicId, x => x.Id);
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var resRows = await connection.QueryAsync<(Guid PublicId, long Id)>(new CommandDefinition(sql, new { publicIds }, cancellationToken: ct));
        return resRows.ToDictionary(x => x.PublicId, x => x.Id);
    }

    /// <summary>Runs a single INSERT/UPDATE, translating a unique-index violation into a clean
    /// <see cref="ConflictException"/> instead of letting the raw SqlException reach
    /// ExceptionHandlingMiddleware's generic 500 fallback. This is a backstop for the rare race
    /// where two concurrent writes slip past RecordConstraintValidator's SELECT-then-write
    /// uniqueness pre-check (not atomic with the following INSERT/UPDATE) and both hit the
    /// physical filtered unique index (see SchemaEngineService.SetUniqueAsync) at once — the
    /// normal case (a single write colliding with existing data) is already caught earlier and
    /// reported with a specific field name by RecordConstraintValidator, so this message stays
    /// generic rather than trying to parse the field back out of SQL Server's (locale-dependent)
    /// error text.</summary>
    private static async Task<T> ExecuteTranslatingUniqueViolationsAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (SqlException ex) when (ex.Number is 2601 or 2627)
        {
            throw new ConflictException(
                "This value conflicts with an existing record — a unique field's value is already in use. Please try again.");
        }
    }

    public async Task<long> GetActiveRecordIdByPublicIdAsync(AppTable table, Guid publicId, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var sql = $"SELECT Id FROM {PhysicalNaming.FullTableName(table.Id)} WHERE PublicId = @publicId AND IsDeleted = 0";
        if (transaction is not null)
        {
            return await transaction.Connection!.QuerySingleAsync<long>(new CommandDefinition(sql, new { publicId }, transaction, cancellationToken: ct));
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.QuerySingleAsync<long>(new CommandDefinition(sql, new { publicId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyDictionary<Guid, long>> GetActiveRecordIdsByPublicIdsAsync(AppTable table, IReadOnlyCollection<Guid> publicIds, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var sql = $"SELECT PublicId, Id FROM {PhysicalNaming.FullTableName(table.Id)} WHERE PublicId IN @publicIds AND IsDeleted = 0";
        if (transaction is not null)
        {
            var rows = await transaction.Connection!.QueryAsync<(Guid PublicId, long Id)>(new CommandDefinition(sql, new { publicIds }, transaction, cancellationToken: ct));
            return rows.ToDictionary(x => x.PublicId, x => x.Id);
        }

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var resRows = await connection.QueryAsync<(Guid PublicId, long Id)>(new CommandDefinition(sql, new { publicIds }, cancellationToken: ct));
        return resRows.ToDictionary(x => x.PublicId, x => x.Id);
    }

    public async Task<Guid> CreateAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyDictionary<long, object?> values, IDbTransaction? transaction = null, CancellationToken ct = default, Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null)
    {
        var relevantFields = fields.Where(f => f.Fid.HasValue && values.ContainsKey((long)f.Fid.Value) && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)).ToList();

        // Build the SQL shape first (column names only, no values yet)
        string sql;
        var colParts = new List<string>();
        var paramParts = new List<string>();
        if (relevantFields.Count > 0)
        {
            foreach (var f in relevantFields)
            {
                var col = PhysicalNaming.ColumnName(f.Fid!.Value);
                if (PhysicalNaming.IsRangeTypeCode(f.TypeCode))
                {
                    var endCol = PhysicalNaming.EndColumnName(f.Fid!.Value);
                    colParts.Add(col);    paramParts.Add($"@{col}");
                    colParts.Add(endCol); paramParts.Add($"@{endCol}");
                }
                else
                {
                    colParts.Add(col); paramParts.Add($"@{col}");
                }
            }
            sql = $"""
                INSERT INTO {PhysicalNaming.FullTableName(table.Id)} (CreatedBy, {string.Join(", ", colParts)})
                OUTPUT INSERTED.PublicId
                VALUES (@createdBy, {string.Join(", ", paramParts)})
                """;
        }
        else
        {
            sql = $"""
                INSERT INTO {PhysicalNaming.FullTableName(table.Id)} (CreatedBy)
                OUTPUT INSERTED.PublicId
                VALUES (@createdBy)
                """;
        }


        // Encrypt flagged field values (no-op if app is not encrypted)
        Services.FieldEncryptionContext enc;
        IReadOnlyDictionary<long, object?> encryptedValues;
        if (transaction is not null)
        {
            enc = await GetEncryptionContextAsync(transaction.Connection!, table.AppId, transaction, ct);
            if (!enc.IsActive && (enc.IsAppEncrypted || relevantFields.Any(f => f.IsEncrypted)))
            {
                await enc.EnsureDekAsync(transaction.Connection!, transaction, ct);
            }
            encryptedValues = await enc.EncryptValuesAsync(fields, values, ct);
        }
        else
        {
            await using var connection = await ConnectionFactory.CreateAsync(ct);
            enc = await GetEncryptionContextAsync(connection, table.AppId, null, ct);
            if (!enc.IsActive && (enc.IsAppEncrypted || relevantFields.Any(f => f.IsEncrypted)))
            {
                await using var tenantConn = await ConnectionFactory.CreateAsync(ct);
                await enc.EnsureDekAsync(tenantConn, null, ct);
            }
            encryptedValues = await enc.EncryptValuesAsync(fields, values, ct);
        }

        // Build parameters using encrypted values
        var parameters = new DynamicParameters();
        parameters.Add("createdBy", QueryContext.UserId);
        foreach (var f in relevantFields)
        {
            var col = PhysicalNaming.ColumnName(f.Fid!.Value);
            if (PhysicalNaming.IsRangeTypeCode(f.TypeCode))
            {
                var endCol = PhysicalNaming.EndColumnName(f.Fid!.Value);
                var (startVal, endVal) = SplitRangeValue(encryptedValues.TryGetValue((long)f.Fid.Value, out var rv) ? rv : values[(long)f.Fid.Value]);
                parameters.Add(col, startVal);
                parameters.Add(endCol, endVal);
            }
            else
            {
                parameters.Add(col, encryptedValues.TryGetValue((long)f.Fid.Value, out var ev) ? ev : values[(long)f.Fid.Value]);
            }
        }

        Guid insertedPublicId;
        if (transaction is not null)
        {
            insertedPublicId = await ExecuteTranslatingUniqueViolationsAsync(() =>
                transaction.Connection!.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, parameters, transaction, cancellationToken: ct)));
        }
        else
        {
            await using var connection = await ConnectionFactory.CreateAsync(ct);
            insertedPublicId = await ExecuteTranslatingUniqueViolationsAsync(() =>
                connection.ExecuteScalarAsync<Guid>(new CommandDefinition(sql, parameters, cancellationToken: ct)));
        }

        // Push searchable/filterable fields to Azure AI Search (using ORIGINAL plaintext values)
        var searchableValues = fields
            .Where(f => (f.IsSearchable || f.IsFilterable) && f.Fid.HasValue && values.ContainsKey((long)f.Fid.Value))
            .ToDictionary(f => f.Fid!.Value.ToString(), f => values[(long)f.Fid.Value]);

        var msg = new PowerBase.Application.Common.Models.SearchIndexMessage
        {
            Action = PowerBase.Application.Common.Models.IndexAction.Upsert,
            TenantId = QueryContext.TenantId,
            AppId = table.AppId,
            TableId = table.Id,
            RecordPublicId = insertedPublicId,
            Payload = searchableValues.Count > 0 ? searchableValues : null
        };

        if (onIndexMessageCreated != null)
        {
            onIndexMessageCreated(msg);
        }
        else
        {
            _ = _messagePublisher.PublishAsync(msg, default);
        }

        return insertedPublicId;
    }

    public async Task UpdateAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid publicId,
        IReadOnlyDictionary<long, object?> values, IDbTransaction? transaction = null, CancellationToken ct = default, Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null)
    {
        var relevantFields = fields.Where(f => f.Fid.HasValue && values.ContainsKey((long)f.Fid.Value) && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)).ToList();
        if (relevantFields.Count == 0) return;

        Services.FieldEncryptionContext enc;
        IReadOnlyDictionary<long, object?> encryptedValues;
        
        if (transaction is not null)
        {
            enc = await GetEncryptionContextAsync(transaction.Connection!, table.AppId, transaction, ct);
            if (!enc.IsActive && (enc.IsAppEncrypted || relevantFields.Any(f => f.IsEncrypted)))
            {
                await enc.EnsureDekAsync(transaction.Connection!, transaction, ct);
            }
            encryptedValues = await enc.EncryptValuesAsync(fields, values, ct);
        }
        else
        {
            await using var connection = await ConnectionFactory.CreateAsync(ct);
            enc = await GetEncryptionContextAsync(connection, table.AppId, null, ct);
            if (!enc.IsActive && (enc.IsAppEncrypted || relevantFields.Any(f => f.IsEncrypted)))
            {
                await using var tenantConn = await ConnectionFactory.CreateAsync(ct);
                await enc.EnsureDekAsync(tenantConn, null, ct);
            }
            encryptedValues = await enc.EncryptValuesAsync(fields, values, ct);
        }

        // Build set-clause parameters with potentially-encrypted values
        var parameters = new DynamicParameters();
        parameters.Add("publicId", publicId);
        parameters.Add("modifiedBy", QueryContext.UserId);
        var setClauses = new List<string>();
        
        foreach (var f in relevantFields)
        {
            var col = PhysicalNaming.ColumnName(f.Fid!.Value);
            var valToBind = encryptedValues.TryGetValue((long)f.Fid.Value, out var ev) ? ev : values[(long)f.Fid.Value];
            
            if (PhysicalNaming.IsRangeTypeCode(f.TypeCode))
            {
                var endCol = PhysicalNaming.EndColumnName(f.Fid!.Value);
                var (startVal, endVal) = SplitRangeValue(valToBind);
                setClauses.Add($"{col} = @{col}"); parameters.Add(col, startVal);
                setClauses.Add($"{endCol} = @{endCol}"); parameters.Add(endCol, endVal);
            }
            else
            {
                setClauses.Add($"{col} = @{col}");
                parameters.Add(col, valToBind);
            }
        }

        var updateSql = $"""
            UPDATE {PhysicalNaming.FullTableName(table.Id)}
            SET {string.Join(", ", setClauses)}, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
            WHERE PublicId = @publicId AND IsDeleted = 0
            """;

        if (transaction is not null)
        {
            var affectedTx = await ExecuteTranslatingUniqueViolationsAsync(() =>
                transaction.Connection!.ExecuteAsync(new CommandDefinition(updateSql, parameters, transaction, cancellationToken: ct)));
            if (affectedTx == 0) throw new NotFoundException("Record", publicId);
        }
        else
        {
            await using var connection = await ConnectionFactory.CreateAsync(ct);
            var affected = await ExecuteTranslatingUniqueViolationsAsync(() =>
                connection.ExecuteAsync(new CommandDefinition(updateSql, parameters, cancellationToken: ct)));
            if (affected == 0) throw new NotFoundException("Record", publicId);
        }


        // Update Azure AI Search with searchable/filterable fields (using ORIGINAL plaintext values)
        var searchableValues = fields
            .Where(f => (f.IsSearchable || f.IsFilterable) && f.Fid.HasValue && values.ContainsKey((long)f.Fid.Value))
            .ToDictionary(f => f.Fid!.Value.ToString(), f => values[(long)f.Fid.Value]);

        var msg = new PowerBase.Application.Common.Models.SearchIndexMessage
        {
            Action = PowerBase.Application.Common.Models.IndexAction.Upsert,
            TenantId = QueryContext.TenantId,
            AppId = table.AppId,
            TableId = table.Id,
            RecordPublicId = publicId,
            Payload = searchableValues.Count > 0 ? searchableValues : null
        };

        if (onIndexMessageCreated != null)
        {
            onIndexMessageCreated(msg);
        }
        else
        {
            _ = _messagePublisher.PublishAsync(msg, default);
        }
    }

    public async Task<int> MassUpdateAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyCollection<long> recordIds,
        IReadOnlyDictionary<long, object?> values, CancellationToken ct = default, Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null)
    {
        var relevantFields = fields.Where(f => f.Fid.HasValue && values.ContainsKey((long)f.Fid.Value) && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)).ToList();
        if (relevantFields.Count == 0 || recordIds.Count == 0) return 0;

        Services.FieldEncryptionContext enc;
        IReadOnlyDictionary<long, object?> encryptedValues;

        await using (var connectionForEnc = await ConnectionFactory.CreateAsync(ct))
        {
            enc = await GetEncryptionContextAsync(connectionForEnc, table.AppId, null, ct);
            if (!enc.IsActive && (enc.IsAppEncrypted || relevantFields.Any(f => f.IsEncrypted)))
            {
                await using var tenantConn = await ConnectionFactory.CreateAsync(ct);
                await enc.EnsureDekAsync(tenantConn, null, ct);
            }
            encryptedValues = await enc.EncryptValuesAsync(fields, values, ct);
        }

        var parameters = new DynamicParameters();
        parameters.Add("ids", recordIds);
        parameters.Add("modifiedBy", QueryContext.UserId);

        var setClauses = new List<string>();
        foreach (var f in relevantFields)
        {
            var col = PhysicalNaming.ColumnName(f.Fid!.Value);
            var valToBind = encryptedValues.TryGetValue((long)f.Fid.Value, out var ev) ? ev : values[(long)f.Fid.Value];
            if (PhysicalNaming.IsRangeTypeCode(f.TypeCode))
            {
                var endCol = PhysicalNaming.EndColumnName(f.Fid!.Value);
                var (startVal, endVal) = SplitRangeValue(valToBind);
                setClauses.Add($"{col} = @{col}"); parameters.Add(col, startVal);
                setClauses.Add($"{endCol} = @{endCol}"); parameters.Add(endCol, endVal);
            }
            else
            {
                setClauses.Add($"{col} = @{col}"); parameters.Add(col, valToBind);
            }
        }

        // A single UPDATE statement is implicitly transactional in SQL Server — either every matched
        // row is written or none is, satisfying the all-or-nothing requirement without an explicit
        // BEGIN TRAN (and without needing per-record round trips, since every record gets the same values).
        var sql = $"""
            UPDATE {PhysicalNaming.FullTableName(table.Id)}
            SET {string.Join(", ", setClauses)}, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
            WHERE Id IN @ids AND IsDeleted = 0
            """;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var affected = await ExecuteTranslatingUniqueViolationsAsync(() =>
            connection.ExecuteAsync(new CommandDefinition(sql, parameters, cancellationToken: ct)));

        // GAP #5: Re-index in Azure AI Search after Mass Update
        if (affected > 0 && fields.Any(f => f.IsSearchable || f.IsFilterable))
        {
            var publicIdsSql = $"SELECT PublicId FROM {PhysicalNaming.FullTableName(table.Id)} WHERE Id IN @ids";
            var publicIds = await connection.QueryAsync<Guid>(new CommandDefinition(publicIdsSql, new { ids = recordIds }, cancellationToken: ct));

            var searchableValues = fields
                .Where(f => (f.IsSearchable || f.IsFilterable) && f.Fid.HasValue && values.ContainsKey((long)f.Fid.Value))
                .ToDictionary(f => f.Fid!.Value.ToString(), f => values[(long)f.Fid.Value]);

            foreach (var pubId in publicIds)
            {
                var msg = new PowerBase.Application.Common.Models.SearchIndexMessage
                {
                    Action = PowerBase.Application.Common.Models.IndexAction.Upsert,
                    TenantId = QueryContext.TenantId,
                    AppId = table.AppId,
                    TableId = table.Id,
                    RecordPublicId = pubId,
                    Payload = searchableValues.Count > 0 ? searchableValues : null
                };

                if (onIndexMessageCreated != null)
                {
                    onIndexMessageCreated(msg);
                }
                else
                {
                    _ = _messagePublisher.PublishAsync(msg, default);
                }
            }
        }

        return affected;
    }

    public async Task DeleteAsync(AppTable table, Guid publicId, IDbTransaction? transaction = null, CancellationToken ct = default, Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null)
    {
        var sql = $"""
            UPDATE {PhysicalNaming.FullTableName(table.Id)}
            SET IsDeleted = 1, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
            WHERE PublicId = @publicId AND IsDeleted = 0
            """;

        if (transaction is not null)
        {
            var affectedTx = await transaction.Connection!.ExecuteAsync(
                new CommandDefinition(sql, new { publicId, modifiedBy = QueryContext.UserId }, transaction, cancellationToken: ct));
            if (affectedTx == 0) throw new NotFoundException("Record", publicId);
        }
        else
        {
            await using var connection = await ConnectionFactory.CreateAsync(ct);
            var affected = await connection.ExecuteAsync(
                new CommandDefinition(sql, new { publicId, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
            if (affected == 0) throw new NotFoundException("Record", publicId);
        }

        // Remove from Azure AI Search
        var msg = new PowerBase.Application.Common.Models.SearchIndexMessage
        {
            Action = PowerBase.Application.Common.Models.IndexAction.Delete,
            TenantId = QueryContext.TenantId,
            AppId = table.AppId,
            TableId = table.Id,
            RecordPublicId = publicId
        };

        if (onIndexMessageCreated != null)
        {
            onIndexMessageCreated(msg);
        }
        else
        {
            _ = _messagePublisher.PublishAsync(msg, default);
        }
    }

    public async Task BulkDeleteAsync(AppTable table, IReadOnlyList<Guid> publicIds, IDbTransaction? transaction = null, CancellationToken ct = default, Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null)
    {
        var sql = $"""
            UPDATE {PhysicalNaming.FullTableName(table.Id)}
            SET IsDeleted = 1, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
            WHERE PublicId IN @publicIds AND IsDeleted = 0
            """;

        if (transaction is not null)
        {
            await transaction.Connection!.ExecuteAsync(
                new CommandDefinition(sql, new { publicIds, modifiedBy = QueryContext.UserId }, transaction, cancellationToken: ct));
        }
        else
        {
            await using var connection = await ConnectionFactory.CreateAsync(ct);
            await connection.ExecuteAsync(
                new CommandDefinition(sql, new { publicIds, modifiedBy = QueryContext.UserId }, cancellationToken: ct));
        }

        // Remove from Azure AI Search
        var deleteMessages = publicIds.Select(id => new PowerBase.Application.Common.Models.SearchIndexMessage
        {
            Action = PowerBase.Application.Common.Models.IndexAction.Delete,
            TenantId = QueryContext.TenantId,
            AppId = table.AppId,
            TableId = table.Id,
            RecordPublicId = id
        }).ToList();
        if (onIndexMessageCreated != null)
        {
            foreach (var msg in deleteMessages)
            {
                onIndexMessageCreated(msg);
            }
        }
        else
        {
            _ = _messagePublisher.PublishBatchAsync(deleteMessages, default);
        }
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
        var groupExpr = BuildGroupByExpr(groupCol, groupByMode, groupByField.TypeCode);
        var fieldMap = allFields.GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());

        string? seriesExpr = null;
        if (seriesField is not null)
        {
            var seriesCol = seriesField.IsSystem && !string.IsNullOrEmpty(seriesField.PhysicalColumnName)
                ? seriesField.PhysicalColumnName!
                : PhysicalNaming.ColumnName(seriesField.Fid!.Value);
            seriesExpr = BuildGroupByExpr(seriesCol, seriesMode, seriesField.TypeCode);
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

    /// <summary>Builds the GROUP BY / SELECT expression for a group-by or series field,
    /// branching by the field's type family (<see cref="GroupByModeCategoryHelper"/>) before
    /// interpreting <paramref name="mode"/> — the same mode string means something different
    /// per family (e.g. "Day" buckets a Date column to its calendar day, but buckets a
    /// Duration column, stored in whole minutes, to a day-sized chunk of minutes). Unmatched
    /// mode/family combinations (including every family's "EqualValues") fall through to the
    /// raw column — this must stay exactly `col` for EqualValues so existing saved reports'
    /// grouping behavior never changes.</summary>
    private static string BuildGroupByExpr(string col, string mode, string typeCode)
    {
        var family = GroupByModeCategoryHelper.GetFamily(typeCode);

        if (family is GroupByModeCategoryHelper.GroupByFamily.TextRich or GroupByModeCategoryHelper.GroupByFamily.User)
        {
            return mode switch
            {
                "FirstWord" => $"LEFT({col}, CASE WHEN CHARINDEX(' ', {col}) > 0 THEN CHARINDEX(' ', {col}) - 1 ELSE LEN({col}) END)",
                "FirstLetter" => $"LEFT({col}, 1)",
                _ => col,
            };
        }

        if (family == GroupByModeCategoryHelper.GroupByFamily.DateFamily)
        {
            return mode switch
            {
                "Day" => $"CAST({col} AS DATE)",
                "Week" => $"DATEADD(WEEK, DATEDIFF(WEEK, 0, {col}), 0)",
                "Month" => $"DATEADD(MONTH, DATEDIFF(MONTH, 0, {col}), 0)",
                "Quarter" => $"DATEADD(QUARTER, DATEDIFF(QUARTER, 0, {col}), 0)",
                "Year" => $"DATEADD(YEAR, DATEDIFF(YEAR, 0, {col}), 0)",
                "Decade" => $"DATEFROMPARTS((YEAR({col}) / 10) * 10, 1, 1)",
                _ => col,
            };
        }

        if (family == GroupByModeCategoryHelper.GroupByFamily.DurationFamily)
        {
            // Duration's physical value is stored in whole minutes (see
            // pb-duration-input.component.ts's parseDuration on the frontend), so "Minute" is
            // just the raw column — same as EqualValues.
            return mode switch
            {
                "Hour" => $"(({col} / 60) * 60)",
                "Day" => $"(({col} / 1440) * 1440)",
                "Week" => $"(({col} / 10080) * 10080)",
                _ => col,
            };
        }

        if (family == GroupByModeCategoryHelper.GroupByFamily.Numeric)
        {
            return mode switch
            {
                "Increment1" => $"(FLOOR({col} / 1) * 1)",
                "Increment10" => $"(FLOOR({col} / 10) * 10)",
                "Increment100" => $"(FLOOR({col} / 100) * 100)",
                "Increment1000" => $"(FLOOR({col} / 1000) * 1000)",
                "Increment10000" => $"(FLOOR({col} / 10000) * 10000)",
                _ => col,
            };
        }

        // TextSimple, Boolean, MultiUser, Time, Unclassified, NoGrouping — none of these
        // families have a mode beyond "EqualValues" (validators reject anything else for
        // them), so grouping by the raw column is always correct here.
        return col;
    }

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
        if (cond.FieldId == -1)
        {
            col = "PublicId";
        }
        else if (fieldLookup != null && fieldLookup.TryGetValue(cond.FieldId, out var f))
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
        
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var encContext = await GetEncryptionContextAsync(connection, table.AppId, null, ct);
        bool requiresClientSideDistinct = encContext.IsActive && !field.IsSystem && field.IsEncrypted;

        var sql = requiresClientSideDistinct 
            ? $"""
                SELECT CAST({selectExpr} AS NVARCHAR(MAX)) 
                FROM {PhysicalNaming.FullTableName(table.Id)}
                WHERE IsDeleted = 0 AND {col} IS NOT NULL AND CAST({col} AS NVARCHAR(MAX)) <> ''{whereExtra}
              """
            : $"""
                SELECT DISTINCT CAST({selectExpr} AS NVARCHAR(MAX)) 
                FROM {PhysicalNaming.FullTableName(table.Id)}
                WHERE IsDeleted = 0 AND {col} IS NOT NULL AND CAST({col} AS NVARCHAR(MAX)) <> ''{whereExtra}
              """;

        var rawValues = await connection.QueryAsync<string>(new CommandDefinition(sql, cancellationToken: ct));
        
        IEnumerable<string> processedValues = rawValues.Where(v => !string.IsNullOrWhiteSpace(v));

        if (requiresClientSideDistinct)
        {
            var decrypted = new List<string>();
            foreach (var v in processedValues)
                decrypted.Add(await encContext.DecryptValueAsync(v, ct));
            processedValues = decrypted.Distinct(StringComparer.OrdinalIgnoreCase);
        }
        
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
        // User/MultiUser name resolution intentionally does NOT happen here — CreatedBy/
        // ModifiedBy/User-typed columns store a plain BIGINT user id (core.[User].Id), not a
        // meta.AppUser.PublicId GUID, and meta.AppUser lives in the TENANT database this
        // repository is already connected to, while the actual user directory (core.[User])
        // is in the CONTROL database, reached only through IUserRepository. A previous version
        // tried to resolve names in-line here via `CAST(UserPublicId AS NVARCHAR(36))` against
        // meta.AppUser — that join could never match a plain integer id against a GUID column,
        // so it silently fell back to "id" as its own "name" for every value (the "2|2" bug).
        // GetDistinctFieldValuesQueryHandler resolves names afterward via IUserRepository,
        // the same control-DB lookup RunReportQueryHandler.ResolveUserNamesAsync already uses
        // for the main record grid.

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

    public async Task<bool> HasValueDuplicateAsync(AppTable table, AppField field, object value, long? excludeRecordId = null, CancellationToken ct = default)
    {
        var col = PhysicalNaming.ColumnName(field.Fid!.Value);
        var sql = $"""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM {PhysicalNaming.FullTableName(table.Id)}
                WHERE IsDeleted = 0 AND {col} = @value
                  AND (@excludeRecordId IS NULL OR Id <> @excludeRecordId)
            ) THEN 1 ELSE 0 END AS BIT)
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(sql, new { value, excludeRecordId }, cancellationToken: ct));
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

        public async Task<bool> HasAnyRecordsAsync(AppTable table, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM {PhysicalNaming.FullTableName(table.Id)}
                WHERE IsDeleted = 0
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
        return dict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value == DBNull.Value ? null : (object?)kvp.Value, StringComparer.OrdinalIgnoreCase);
    }

    public async Task<int> SanitizeTableEncryptedDataAsync(AppTable table, IReadOnlyList<AppField> fields, CancellationToken ct = default)
    {
        var encryptedFields = fields.Where(f => f.IsEncrypted && f.Fid.HasValue && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)).ToList();
        if (encryptedFields.Count == 0) return 0;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var enc = await GetEncryptionContextAsync(connection, table.AppId, null, ct);
        if (!enc.IsActive) return 0;

        int updatedCount = 0;
        int page = 1;
        const int pageSize = 500;

        var encryptedCols = encryptedFields.Select(f => PhysicalNaming.ColumnName(f.Fid!.Value)).ToList();
        var selectCols = string.Join(", ", encryptedCols.Prepend("Id").Prepend("PublicId"));

        while (true)
        {
            var selectSql = $"""
                SELECT {selectCols}
                FROM {PhysicalNaming.FullTableName(table.Id)}
                WHERE IsDeleted = 0
                ORDER BY Id
                OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
                """;

            var rows = (await connection.QueryAsync<dynamic>(new CommandDefinition(selectSql, new { offset = (page - 1) * pageSize, pageSize }, cancellationToken: ct))).ToList();
            if (rows.Count == 0) break;

            foreach (var row in rows)
            {
                var rowDict = (IDictionary<string, object?>)row;
                var recordId = (long)rowDict["Id"]!;
                var publicId = (Guid)rowDict["PublicId"]!;

                var fieldsToUpdate = new Dictionary<string, object?>();
                var plainSearchableValues = new Dictionary<long, object?>();

                foreach (var field in encryptedFields)
                {
                    var col = PhysicalNaming.ColumnName(field.Fid!.Value);
                    if (rowDict.TryGetValue(col, out var val) && val is string cipherStr && !string.IsNullOrEmpty(cipherStr))
                    {
                        var isEncrypted = false;
                        string? decryptedVal = null;
                        try
                        {
                            decryptedVal = await enc.DecryptValueAsync(cipherStr, ct);
                            await _encryptionService.DecryptDataAsync(cipherStr, enc.WrappedDek!, QueryContext.TenantId, table.AppId, ct);
                            isEncrypted = true;
                        }
                        catch
                        {
                            isEncrypted = false;
                        }

                        if (!isEncrypted)
                        {
                            var cipher = await enc.EncryptValueAsync(field, cipherStr, ct);
                            fieldsToUpdate[col] = cipher;
                            plainSearchableValues[field.Fid.Value] = cipherStr;
                        }
                        else
                        {
                            plainSearchableValues[field.Fid.Value] = decryptedVal;
                        }
                    }
                }

                if (fieldsToUpdate.Count > 0)
                {
                    var setClauses = fieldsToUpdate.Keys.Select(k => $"{k} = @{k}");
                    var updateSql = $"""
                        UPDATE {PhysicalNaming.FullTableName(table.Id)}
                        SET {string.Join(", ", setClauses)}, ModifiedOn = SYSUTCDATETIME()
                        WHERE Id = @recordId
                        """;

                    var updateParams = new DynamicParameters(fieldsToUpdate);
                    updateParams.Add("recordId", recordId);

                    await connection.ExecuteAsync(new CommandDefinition(updateSql, updateParams, cancellationToken: ct));
                    updatedCount++;

                    var allSearchableFields = fields.Where(f => f.IsSearchable || f.IsFilterable).ToList();
                    var searchPayload = new Dictionary<string, object?>();

                    var searchableCols = allSearchableFields
                        .Where(f => f.Fid.HasValue && !PhysicalNaming.IsComputedTypeCode(f.TypeCode))
                        .Select(f => PhysicalNaming.ColumnName(f.Fid!.Value))
                        .ToList();

                    if (searchableCols.Count > 0)
                    {
                        var searchSelectSql = $"""
                            SELECT {string.Join(", ", searchableCols)}
                            FROM {PhysicalNaming.FullTableName(table.Id)}
                            WHERE Id = @recordId
                            """;
                        var rawRecord = await connection.QueryFirstOrDefaultAsync<dynamic>(new CommandDefinition(searchSelectSql, new { recordId }, cancellationToken: ct));
                        if (rawRecord != null)
                        {
                            var recDict = (IDictionary<string, object?>)rawRecord;
                            foreach (var sf in allSearchableFields)
                            {
                                if (!sf.Fid.HasValue) continue;
                                var colName = PhysicalNaming.ColumnName(sf.Fid.Value);
                                if (recDict.TryGetValue(colName, out var v))
                                {
                                    if (sf.IsEncrypted)
                                    {
                                        if (plainSearchableValues.TryGetValue(sf.Fid.Value, out var pv))
                                        {
                                            searchPayload[sf.Fid.Value.ToString()] = pv;
                                        }
                                        else if (v is string cStr)
                                        {
                                            searchPayload[sf.Fid.Value.ToString()] = await enc.DecryptValueAsync(cStr, ct);
                                        }
                                    }
                                    else
                                    {
                                        searchPayload[sf.Fid.Value.ToString()] = v;
                                    }
                                }
                            }
                        }
                    }

                    var msg = new PowerBase.Application.Common.Models.SearchIndexMessage
                    {
                        Action = PowerBase.Application.Common.Models.IndexAction.Upsert,
                        TenantId = QueryContext.TenantId,
                        AppId = table.AppId,
                        TableId = table.Id,
                        RecordPublicId = publicId,
                        Payload = searchPayload.Count > 0 ? searchPayload : null
                    };

                    _ = _messagePublisher.PublishAsync(msg, default);
                }
            }

            page++;
        }

        return updatedCount;
    }
    public async Task<IReadOnlyList<PowerBase.Application.Common.Interfaces.SearchIndexDocument>> GetFieldBackfillBatchAsync(long tenantId, long appId, long tableId, long fieldId, bool isNullify, int page, int pageSize, CancellationToken ct = default)
    {
        var result = new List<PowerBase.Application.Common.Interfaces.SearchIndexDocument>();
        if (tenantId > 0)
        {
            QueryContext.SetTenantId(tenantId);
        }
        await using var connection = await ConnectionFactory.CreateAsync(ct);

        var tableSql = "SELECT Name FROM meta.AppTable WHERE Id = @tableId";
        var tableInfo = await connection.QueryFirstOrDefaultAsync<dynamic>(tableSql, new { tableId });
        if (tableInfo == null) return result;

        var fieldSql = "SELECT Id, AppTableId, Name, PhysicalColumnName, Fid, IsSystem, IsSearchable, IsFilterable, IsEncrypted FROM meta.AppField WHERE Fid = @fieldId AND AppTableId = @tableId";
        var field = await connection.QueryFirstOrDefaultAsync<AppField>(fieldSql, new { fieldId, tableId });
        if (field == null || !field.Fid.HasValue) return result;

        var colName = field.IsSystem ? field.PhysicalColumnName! : PhysicalNaming.ColumnName(field.Fid.Value);

        var selectSql = $"""
            SELECT t.PublicId, t.{colName}
            FROM {PhysicalNaming.FullTableName(tableId)} t
            WHERE t.IsDeleted = 0
            ORDER BY t.Id
            OFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY
            """;

        var offset = (page - 1) * pageSize;
        var rows = (await connection.QueryAsync<dynamic>(selectSql, new { offset, pageSize })).ToList();
        
        if (rows.Count == 0) return result;

        var enc = await GetEncryptionContextAsync(connection, appId, null, ct);

        foreach (var rawRow in rows)
        {
            var rowDict = (IDictionary<string, object?>)rawRow;
            var publicId = (Guid)rowDict["PublicId"]!;
            
            var documentValues = new Dictionary<long, object?>();

            if (isNullify)
            {
                documentValues[field.Fid.Value] = null;
            }
            else
            {
                if (rowDict.TryGetValue(colName, out var val))
                {
                    if (field.IsEncrypted && val is string cipherStr)
                    {
                        try
                        {
                            documentValues[field.Fid.Value] = await enc.DecryptValueAsync(cipherStr, ct);
                        }
                        catch
                        {
                            documentValues[field.Fid.Value] = val; // fallback
                        }
                    }
                    else
                    {
                        documentValues[field.Fid.Value] = val;
                    }
                }
            }

            result.Add(new PowerBase.Application.Common.Interfaces.SearchIndexDocument(tenantId, appId, tableId, publicId, documentValues));
        }

        return result;
    }
}
