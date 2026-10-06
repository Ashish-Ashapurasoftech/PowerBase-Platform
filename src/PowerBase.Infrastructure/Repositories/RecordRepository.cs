using System.Data;
using System.Text.Json;
using Dapper;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Validation;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Domain.FieldSettings;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

public class RecordRepository : TenantRepositoryBase, IRecordRepository
{
    private readonly IMessagePublisher _messagePublisher;
    private readonly IEncryptionService _encryptionService;
    private readonly IControlConnectionFactory _controlConnectionFactory;
    /// <summary>Resolves the projectors lazily (they depend on this repository, so they can't be constructor-injected).</summary>
    private readonly IServiceProvider? _services;

    public RecordRepository(
        ITenantConnectionFactory connectionFactory, 
        IQueryContext queryContext,
        IMessagePublisher messagePublisher,
        IEncryptionService encryptionService,
        IControlConnectionFactory controlConnectionFactory,
        IServiceProvider? services = null)
        : base(connectionFactory, queryContext)
    {
        _services = services;
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
        var recordSql = $"SELECT {fieldCols} FROM {PhysicalNaming.FullTableName((long)tableInfo.Id)} WHERE Id = (SELECT RecordId FROM RecordMetadata WHERE PublicId = @publicId AND TenantId = @tenantId)";
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
                if (!f.IsSearchable)
                {
                    result[(long)f.Fid.Value] = null;
                }
                else if (f.IsEncrypted && val is string cipherHex)
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

        var fieldLookup = BuildFieldLookup(fields);
        var filterWhere = BuildFilterTreeWhere(filterTree, parameters, fieldLookup) + BuildOwnerWhere(restrictToCreatedBy, parameters);
        var orderBy = sortFields?.Count > 0
            ? string.Join(", ", sortFields
                .Where(s => !fieldLookup.TryGetValue(s.FieldId, out var sf2) || !PhysicalNaming.IsComputedTypeCode(sf2.TypeCode))
                .Select(s =>
                {
                    var colName = fieldLookup.TryGetValue(s.FieldId, out var sf)
                        ? ResolveColumnName(sf, s.FieldId)
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
        var fieldLookup = BuildFieldLookup(fields);
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

    /// <inheritdoc />
    public async Task<bool> ExistsWithViewFilterAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        Guid publicId,
        FilterGroup? viewFilter,
        long? restrictToCreatedBy = null,
        CancellationToken ct = default)
    {
        var parameters = new DynamicParameters();
        parameters.Add("publicId", publicId);

        // Build the same WHERE fragments used by CountAsync/ListAsync so ViewFilter
        // conditions are always resolved identically (field Fid mapping, operators, etc.).
        // Both helpers return strings that start with " AND ..." (or empty string),
        // so they can be appended directly after "IsDeleted = 0".
        var fieldLookup = fields
            .Where(f => f.Fid.HasValue)
            .GroupBy(f => (long)f.Fid!.Value)
            .ToDictionary(g => g.Key, g => g.First());

        var filterWhere = BuildFilterTreeWhere(viewFilter, parameters, fieldLookup)
                        + BuildOwnerWhere(restrictToCreatedBy, parameters);

        var sql = $"""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1
                FROM {PhysicalNaming.FullTableName(table.Id)}
                WHERE PublicId = @publicId
                  AND IsDeleted = 0{filterWhere}
            ) THEN 1 ELSE 0 END AS BIT)
            """;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(sql, parameters, cancellationToken: ct));
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
        await using var connection = await ConnectionFactory.CreateAsync(ct);

        // An encrypted reference column holds ciphertext, so "= @parentRecordId" can never match: count the
        // children by decrypting the column instead (otherwise a parent with children looks deletable).
        var enc = await GetEncryptionContextAsync(connection, childTable.AppId, null, ct);
        var fieldEncrypted = await connection.ExecuteScalarAsync<bool?>(new CommandDefinition(
            "SELECT TOP 1 IsEncrypted FROM meta.AppField WHERE AppTableId = @tableId AND Fid = @fid",
            new { tableId = childTable.Id, fid = referenceFid }, cancellationToken: ct)) ?? false;
        if (enc.IsActive && (enc.IsAppEncrypted || fieldEncrypted))
        {
            var stored = await connection.QueryAsync<object>(new CommandDefinition(
                $"SELECT {col} FROM {PhysicalNaming.FullTableName(childTable.Id)} WHERE IsDeleted = 0 AND {col} IS NOT NULL",
                cancellationToken: ct));
            var count = 0;
            foreach (var raw in stored)
            {
                var text = await enc.DecryptValueAsync(Convert.ToString(raw, System.Globalization.CultureInfo.InvariantCulture) ?? "", ct);
                if (decimal.TryParse(text, System.Globalization.NumberStyles.Number, System.Globalization.CultureInfo.InvariantCulture, out var id) && id == parentRecordId)
                    count++;
            }
            return count;
        }

        var sql = $"SELECT COUNT(*) FROM {PhysicalNaming.FullTableName(childTable.Id)} WHERE IsDeleted = 0 AND {col} = @parentRecordId";
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, new { parentRecordId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ReferenceOption>> SearchForReferenceAsync(
        AppTable parentTable, IReadOnlyList<AppField> labelFields, string? search, int take,
        AppField? primaryLabelField = null, CancellationToken ct = default,
        IReadOnlyList<ReferenceFilterClause>? filters = null)
    {
        take = Math.Clamp(take, 1, 200);

        var parameters = new DynamicParameters();
        var where = "IsDeleted = 0";
        if (filters is { Count: > 0 })
            where += $" AND {BuildReferenceFilterSql(filters, parameters, await MatchEncryptedTreesAsync(parentTable, filters, ct))}";

        // Computed label fields (Formula/Lookup/Summary) have no SQL column: they are selected as NULL
        // placeholders and filled in by the caller after projection (see GetParentOptionsQueryHandler).
        // They can't be searched or ordered by in SQL, so those fall to the first physical label
        // field, or the row Id when there is none.
        var physicalLabelFields = labelFields.Where(f => !IsComputedLabel(f)).ToList();
        var searchColExpr = physicalLabelFields.Count > 0 ? LabelColumnExpr(physicalLabelFields[0]) : "CAST(Id AS NVARCHAR(400))";
        if (!string.IsNullOrWhiteSpace(search))
        {
            parameters.Add("search", $"%{search}%");
            if (physicalLabelFields.Count > 0)
            {
                var searchConditions = physicalLabelFields.Select(f => $"{LabelColumnExpr(f)} LIKE @search");
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
        // PublicId rides along from the same row purely so a picker-driven fetch (the Reference
        // dropdown's own "selection changed" moment) can hit GetById directly — it plays no part
        // in what gets submitted/stored.
        var selectCols = new List<string> { "CAST(Id AS NVARCHAR(400)) AS Id", "PublicId" };
        if (labelFields.Count > 0) selectCols.Add($"{SelectLabelExpr(labelFields[0])} AS Value1");
        if (labelFields.Count > 1) selectCols.Add($"{SelectLabelExpr(labelFields[1])} AS Value2");
        if (labelFields.Count > 2) selectCols.Add($"{SelectLabelExpr(labelFields[2])} AS Value3");
        
        if (labelFields.Count == 0) selectCols.Add($"{searchColExpr} AS Value1");

        var labelExpr = primaryLabelField is not null ? SelectLabelExpr(primaryLabelField) : searchColExpr;
        selectCols.Add($"{labelExpr} AS Label");

        var sql = $"""
            SELECT TOP (@take) {string.Join(", ", selectCols)}
            FROM {PhysicalNaming.FullTableName(parentTable.Id)} AS p
            WHERE {where}
            ORDER BY {searchColExpr}
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var enc = await GetEncryptionContextAsync(connection, parentTable.AppId, null, ct);
        var rows = await connection.QueryAsync<ReferenceOption>(new CommandDefinition(sql, parameters, cancellationToken: ct));
        var list = rows.AsList();

        // Decrypt label-field values in place when the parent app has field encryption active.
        // Id is the parent's internal BIGINT row Id (a system column) — it is never encrypted.
        if (enc.IsActive)
        {
            var f1 = labelFields.Count > 0 ? labelFields[0] : null;
            var f2 = labelFields.Count > 1 ? labelFields[1] : null;
            var f3 = labelFields.Count > 2 ? labelFields[2] : null;
            var fLabel = primaryLabelField ?? f1;

            foreach (var opt in list)
            {
                if (opt.Value1 is not null && (NeedsDecrypt(enc, f1) || opt.Value1.Length >= 40))
                    opt.Value1 = await enc.DecryptValueAsync(opt.Value1, ct);
                if (opt.Value2 is not null && (NeedsDecrypt(enc, f2) || opt.Value2.Length >= 40))
                    opt.Value2 = await enc.DecryptValueAsync(opt.Value2, ct);
                if (opt.Value3 is not null && (NeedsDecrypt(enc, f3) || opt.Value3.Length >= 40))
                    opt.Value3 = await enc.DecryptValueAsync(opt.Value3, ct);
                if (opt.Label is not null && (NeedsDecrypt(enc, fLabel) || opt.Label.Length >= 40))
                    opt.Label = await enc.DecryptValueAsync(opt.Label, ct);
            }
        }

        return list;
    }

    public async Task<bool> MatchesReferenceFilterAsync(
        AppTable parentTable, long parentRowId, IReadOnlyList<ReferenceFilterClause> filters, CancellationToken ct = default)
    {
        if (filters.Count == 0) return true;
        var parameters = new DynamicParameters();
        parameters.Add("parentRowId", parentRowId);
        var sql = $"""
            SELECT COUNT(1)
            FROM {PhysicalNaming.FullTableName(parentTable.Id)} AS p
            WHERE p.IsDeleted = 0 AND p.Id = @parentRowId AND {BuildReferenceFilterSql(filters, parameters, await MatchEncryptedTreesAsync(parentTable, filters, ct))}
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteScalarAsync<int>(new CommandDefinition(sql, parameters, cancellationToken: ct)) > 0;
    }

    /// <summary>A filter tree that reads an encrypted parent field can't be a WHERE clause (the column holds
    /// ciphertext). Such a tree is judged in memory over the decrypted parent rows instead; this returns,
    /// by clause index, the Ids of the parent rows each one matches — which <see cref="BuildReferenceFilterSql"/>
    /// then uses in place of the tree. Trees that read no encrypted field stay SQL.</summary>
    private async Task<IReadOnlyDictionary<int, IReadOnlyList<long>>> MatchEncryptedTreesAsync(
        AppTable parentTable, IReadOnlyList<ReferenceFilterClause> filters, CancellationToken ct)
    {
        var result = new Dictionary<int, IReadOnlyList<long>>();
        if (!filters.Any(f => f.Tree is not null)) return result;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var appEncrypted = (await GetEncryptionContextAsync(connection, parentTable.AppId, null, ct)).IsAppEncrypted;

        for (var i = 0; i < filters.Count; i++)
        {
            if (filters[i] is not { Tree: { } tree, TreeFields: { } treeFields }) continue;

            // The parent Lookups the tree reads: each is judged by its source field on another table, which
            // may be encrypted too — either way it has no column of its own to compare in SQL.
            var lookups = new List<(AppField Lookup, LookupSettings Settings, AppField Source, AppTable SourceTable, List<AppField> SourceFields)>();
            var sourceEncrypted = false;
            foreach (var lookup in treeFields.Where(f => f.Fid.HasValue && SummaryLookupSources.IsLookup(f) && ReadsField(tree, f.Fid.Value)))
            {
                if (SummaryLookupSources.Settings(lookup) is not { SourceTableId: long sourceTableId, SourceFid: int sourceFid, ReferenceFid: int } settings) continue;
                var sourceTable = await connection.QueryFirstOrDefaultAsync<AppTable>(new CommandDefinition(
                    "SELECT Id, AppId FROM meta.AppTable WHERE Id = @id", new { id = sourceTableId }, cancellationToken: ct));
                if (sourceTable is null) continue;
                var sourceFields = (await connection.QueryAsync<AppField>(new CommandDefinition(
                    "SELECT Id, AppTableId, Name, TypeCode, Settings, PhysicalColumnName, Fid, IsSystem, IsSearchable, IsFilterable, IsEncrypted FROM meta.AppField WHERE AppTableId = @id",
                    new { id = sourceTableId }, cancellationToken: ct))).ToList();
                if (sourceFields.FirstOrDefault(f => f.Fid == sourceFid) is not { } source) continue;
                var sourceAppEncrypted = (await GetEncryptionContextAsync(connection, sourceTable.AppId, null, ct)).IsAppEncrypted;
                sourceEncrypted |= EncryptedRowFilter.IsEncrypted(source, sourceAppEncrypted);
                lookups.Add((lookup, settings, source, sourceTable, sourceFields));
            }

            // A Formula/Summary has no column either: its value is computed per record, as a record read does.
            var readsComputed = treeFields.Any(f => f.Fid.HasValue && SummaryComputedTargets.ResultKind(f) is not null && ReadsField(tree, f.Fid.Value));
            if (!sourceEncrypted && !readsComputed && !EncryptedRowFilter.TouchesEncrypted(tree, treeFields, appEncrypted)) continue;

            IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = await ListAllRowsDecryptedAsync(parentTable, treeFields, ct);
            IReadOnlyList<IReadOnlyDictionary<long, object?>>? computed = null;
            if (readsComputed && _services is not null)
            {
                var relational = await _services.GetRequiredService<PowerBase.Application.Relationships.IRelationalProjector>().ProjectAsync(parentTable, treeFields, rows, ct);
                computed = _services.GetRequiredService<PowerBase.Application.Formulas.IFormulaProjector>().Project(treeFields, rows, relational, parentTable);
            }
            IReadOnlyList<AppField> fields = treeFields;
            foreach (var (lookup, settings, source, sourceTable, sourceFields) in lookups)
            {
                var sourceRows = await ListAllRowsDecryptedAsync(sourceTable, sourceFields, ct);
                (rows, fields) = EncryptedRowFilter.WithLookupValue(rows, fields, lookup, settings, source, sourceRows);
            }
            result[i] = EncryptedRowFilter.MatchingIds(rows, fields, tree, computed);
        }
        return result;
    }

    private static bool ReadsField(FilterGroup group, int fid) =>
        group.Nodes.Any(n => n.Condition?.FieldId == fid || (n.Group is { } sub && ReadsField(sub, fid)));

    /// <summary>Dependent-dropdown predicate over the parent table aliased <c>p</c>. Values are always
    /// bound as parameters; column names come from field metadata (integers), never user text.</summary>
    private static string BuildReferenceFilterSql(
        IReadOnlyList<ReferenceFilterClause> filters, DynamicParameters parameters,
        IReadOnlyDictionary<int, IReadOnlyList<long>>? memoryMatches = null)
    {
        static string Col(AppField f) => f.IsSystem && !string.IsNullOrEmpty(f.PhysicalColumnName)
            ? f.PhysicalColumnName!
            : PhysicalNaming.ColumnName(f.Fid!.Value);

        var parts = new List<string>();
        for (var i = 0; i < filters.Count; i++)
        {
            var f = filters[i];
            if (memoryMatches is not null && memoryMatches.TryGetValue(i, out var matchedIds))
            {
                if (matchedIds.Count == 0) { parts.Add("1 = 0"); continue; }
                parameters.Add($"encIds{i}", JsonSerializer.Serialize(matchedIds));
                parts.Add($"p.Id IN (SELECT CAST([value] AS BIGINT) FROM OPENJSON(@encIds{i}))");
                continue;
            }
            if (f.Tree is not null)
            {
                // Its own parameter range, clear of the fv{i} names the other clauses use.
                var treeIdx = 1000 * (i + 1);
                var fragment = BuildTreeFragment(f.Tree, parameters, ref treeIdx, BuildFieldLookup(f.TreeFields ?? []), lookupAlias: "p");
                if (!string.IsNullOrEmpty(fragment)) parts.Add($"({fragment})");
                continue;
            }
            if (string.IsNullOrWhiteSpace(f.Value)) { parts.Add("1 = 0"); continue; }
            var name = $"fv{i}";
            parameters.Add(name, f.Value);

            if (f.ParentField is not null)
                parts.Add($"CAST(p.{Col(f.ParentField)} AS NVARCHAR(400)) = @{name}");
            else if (f.JunctionTable is not null && f.JunctionParentField is not null && f.JunctionValueField is not null)
                parts.Add($"EXISTS (SELECT 1 FROM {PhysicalNaming.FullTableName(f.JunctionTable.Id)} AS j " +
                          $"WHERE j.IsDeleted = 0 AND j.{Col(f.JunctionParentField)} = p.Id " +
                          $"AND CAST(j.{Col(f.JunctionValueField)} AS NVARCHAR(400)) = @{name})");
        }
        return parts.Count == 0 ? "1 = 1" : string.Join(" AND ", parts);
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

    public async Task<IReadOnlyDictionary<long, IReadOnlyDictionary<string, object?>>> GetBulkUpsertRowsByIdsAsync(
        AppTable table, IReadOnlyList<AppField> fields, IReadOnlyCollection<long> ids, IDbTransaction transaction, CancellationToken ct = default)
    {
        var rows = await GetBulkUpsertRowsByColumnValuesAsync(table, fields, "Id", ids.Cast<object>().ToArray(), transaction, ct);
        return rows.Values.Distinct().ToDictionary(row => Convert.ToInt64(row["Id"]), row => row);
    }

    public async Task<IReadOnlyDictionary<object, IReadOnlyDictionary<string, object?>>> GetBulkUpsertRowsByColumnValuesAsync(
        AppTable table, IReadOnlyList<AppField> fields, string columnName, IReadOnlyCollection<object> values, IDbTransaction transaction, CancellationToken ct = default)
    {
        var result = new Dictionary<object, IReadOnlyDictionary<string, object?>>();
        if (values.Count == 0) return result;
        var mergeField = columnName == "Id" ? null : fields.FirstOrDefault(f => PhysicalNaming.GetPhysicalColumnName(f) == columnName);
        if (columnName != "Id" && mergeField == null)
            throw new ArgumentException("The merge column must belong to the target table.", nameof(columnName));

        var connection = transaction.Connection ?? throw new InvalidOperationException("Bulk upsert requires an active transaction.");
        var enc = await GetEncryptionContextAsync(connection, table.AppId, transaction, ct);
        var fieldCols = BuildFieldColumnList(fields);
        var escapedColumnName = columnName.Replace("]", string.Concat(']', ']'));

        if (mergeField != null && mergeField.IsEncrypted)
        {
            // The merge column stores ciphertext, so "[col] IN @chunk" against the caller's
            // plaintext values can never match — every bulk-upsert row would be misread as new
            // and inserted as a duplicate instead of being matched for update. Lock and decrypt
            // every candidate row instead, then match in memory (mirrors CopyRecordsExecutor's
            // encrypted merge-key index). This locks the whole table for the transaction's
            // duration rather than just the candidate rows — an unavoidable trade-off of an
            // exact-match lookup against ciphertext, not a partial fix.
            var wanted = new HashSet<string>(
                values.Select(v => v?.ToString()?.Trim()).Where(s => !string.IsNullOrEmpty(s))!,
                StringComparer.OrdinalIgnoreCase);
            var sql = $"""
                SELECT Id, PublicId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy{fieldCols}
                FROM {PhysicalNaming.FullTableName(table.Id)} WITH (UPDLOCK, HOLDLOCK)
                WHERE IsDeleted = 0
                """;
            var allRows = await connection.QueryAsync(new CommandDefinition(sql, transaction: transaction, cancellationToken: ct));
            foreach (var row in allRows)
            {
                IReadOnlyDictionary<string, object?> dict = ToDictionary(row);
                await enc.DecryptRowAsync((IDictionary<string, object?>)dict, fields, ct);
                if (dict.TryGetValue(columnName, out var value) && value is string sval && wanted.Contains(sval.Trim()))
                    result[sval] = dict;
            }
            return result;
        }

        foreach (var chunk in values.Distinct().Chunk(500))
        {
            var sql = $"""
                SELECT Id, PublicId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy{fieldCols}
                FROM {PhysicalNaming.FullTableName(table.Id)} WITH (UPDLOCK, HOLDLOCK)
                WHERE IsDeleted = 0 AND [{escapedColumnName}] IN @chunk
                """;
            var rows = await connection.QueryAsync(new CommandDefinition(sql, new { chunk }, transaction, cancellationToken: ct));
            foreach (var row in rows)
            {
                IReadOnlyDictionary<string, object?> dict = ToDictionary(row);
                await enc.DecryptRowAsync((IDictionary<string, object?>)dict, fields, ct);
                if (dict.TryGetValue(columnName, out var value) && value is not null && value != DBNull.Value)
                    result[value] = dict;
            }
        }
        return result;
    }

    public async Task<IReadOnlyDictionary<object, object?>> AggregateByReferenceAsync(
        AppTable childTable, int referenceFid, string function, int? targetFid,
        IReadOnlyCollection<object> parentKeyValues, FilterGroup? filterTree, string? targetSubField = null,
        IReadOnlyDictionary<long, AppField>? fieldLookup = null, ParentFieldScope? parentScope = null,
        CancellationToken ct = default)
    {
        var result = new Dictionary<object, object?>();
        if (parentKeyValues.Count == 0) return result;

        var refCol = PhysicalNaming.ColumnName(referenceFid);
        // Aggregated over the inner query's TargetValue column: a lookup target is a subquery on
        // the parent table, and SQL Server can't aggregate an expression containing a subquery.
        const string target = "TargetValue";
        var aggExpr = function switch
        {
            "Count" => "COUNT(*)",
            "Exists" => "CAST(CASE WHEN COUNT(*) > 0 THEN 1 ELSE 0 END AS BIT)",
            "Sum" when targetFid.HasValue => $"SUM(CAST({target} AS DECIMAL(18,4)))",
            "Avg" when targetFid.HasValue => $"AVG(CAST({target} AS DECIMAL(18,4)))",
            "Min" when targetFid.HasValue => $"MIN({target})",
            "Max" when targetFid.HasValue => $"MAX({target})",
            // Any column type: cast to text so NULLIF can also drop blank strings (a cleared text
            // field) alongside the NULLs COUNT already skips — NULLIF(decimalCol, '') would throw.
            "DistinctCount" when targetFid.HasValue => $"COUNT(DISTINCT NULLIF(CAST({target} AS NVARCHAR(MAX)), N''))",
            _ => "COUNT(*)",
        };
        var targetSelect = targetFid.HasValue ? $", {TargetColumnExpr(targetFid.Value, targetSubField, fieldLookup)} AS {target}" : "";

        var parameters = new DynamicParameters();
        parameters.Add("parentKeyValues", parentKeyValues);
        var filterWhere = BuildFilterTreeWhere(filterTree, parameters, fieldLookup, parentScope);

        // Aliased so a "parentField" condition's subquery can name this row's reference column
        // (c.f_X) without colliding with the parent table's own same-named columns.
        var sql = $"""
            SELECT ParentKey, {aggExpr} AS Value
            FROM (
                SELECT {refCol} AS ParentKey{targetSelect}
                FROM {PhysicalNaming.FullTableName(childTable.Id)} {ChildAlias}
                WHERE IsDeleted = 0 AND {refCol} IN @parentKeyValues{filterWhere}
            ) matched
            GROUP BY ParentKey
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        var enc = await GetEncryptionContextAsync(connection, childTable.AppId, null, ct);
        foreach (var row in rows)
        {
            var dict = (IDictionary<string, object>)row;
            if (dict.TryGetValue("ParentKey", out var pk) && pk is not null && pk != DBNull.Value)
            {
                var val = dict.TryGetValue("Value", out var v) && v != DBNull.Value ? v : null;
                if (val is string str && enc.IsActive && str.Length >= 40 && !str.Contains(' '))
                {
                    try { val = await enc.DecryptValueAsync(str, ct); } catch { }
                }
                result[pk] = val;
            }
        }
        return result;
    }

    public async Task<IReadOnlyList<(object ParentKey, object Value)>> ListValuesByReferenceAsync(
        AppTable childTable, int referenceFid, int targetFid, string? targetSubField,
        IReadOnlyCollection<object> parentKeyValues, FilterGroup? filterTree,
        int? sortFid, bool sortDescending, IReadOnlyDictionary<long, AppField>? fieldLookup = null,
        ParentFieldScope? parentScope = null, CancellationToken ct = default)
    {
        var result = new List<(object, object)>();
        if (parentKeyValues.Count == 0) return result;

        var refCol = PhysicalNaming.ColumnName(referenceFid);
        var targetExpr = TargetColumnExpr(targetFid, targetSubField, fieldLookup);
        var dir = sortDescending ? "DESC" : "ASC";
        var order = sortFid.HasValue ? $"{TargetColumnExpr(sortFid.Value, null, fieldLookup)} {dir}, Id {dir}" : $"Id {dir}";

        var parameters = new DynamicParameters();
        parameters.Add("parentKeyValues", parentKeyValues);
        var filterWhere = BuildFilterTreeWhere(filterTree, parameters, fieldLookup, parentScope);

        var sql = $"""
            SELECT {refCol} AS ParentKey, {targetExpr} AS Value
            FROM {PhysicalNaming.FullTableName(childTable.Id)} {ChildAlias}
            WHERE IsDeleted = 0 AND {refCol} IN @parentKeyValues AND {targetExpr} IS NOT NULL{filterWhere}
            ORDER BY {refCol}, {order}
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        foreach (var row in rows)
        {
            var dict = (IDictionary<string, object>)row;
            if (dict.TryGetValue("ParentKey", out var pk) && pk is not null && pk != DBNull.Value
                && dict.TryGetValue("Value", out var v) && v is not null && v != DBNull.Value)
                result.Add((pk, v));
        }
        return result;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListRowsByReferenceAsync(
        AppTable childTable, IReadOnlyList<AppField> fields, int referenceFid,
        IReadOnlyCollection<object> parentKeyValues, FilterGroup? filterTree,
        IReadOnlyDictionary<long, AppField>? fieldLookup = null, ParentFieldScope? parentScope = null,
        CancellationToken ct = default)
    {
        if (parentKeyValues.Count == 0) return [];

        var refCol = PhysicalNaming.ColumnName(referenceFid);
        var parameters = new DynamicParameters();
        parameters.Add("parentKeyValues", parentKeyValues);
        var filterWhere = BuildFilterTreeWhere(filterTree, parameters, fieldLookup, parentScope);

        var sql = $"""
            SELECT Id, PublicId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy{BuildFieldColumnList(fields)}
            FROM {PhysicalNaming.FullTableName(childTable.Id)} {ChildAlias}
            WHERE IsDeleted = 0 AND {refCol} IN @parentKeyValues{filterWhere}
            ORDER BY Id
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        var mutableRows = rows.Select(r => (IDictionary<string, object?>)ToDictionary(r)).ToList();

        var enc = await GetEncryptionContextAsync(connection, childTable.AppId, null, ct);
        await enc.DecryptRowsAsync(mutableRows, fields, ct);
        return mutableRows.Cast<IReadOnlyDictionary<string, object?>>().ToList();
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ListAllRowsDecryptedAsync(
        AppTable table, IReadOnlyList<AppField> fields, CancellationToken ct = default)
    {
        var sql = $"""
            SELECT Id, PublicId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy{BuildFieldColumnList(fields)}
            FROM {PhysicalNaming.FullTableName(table.Id)}
            WHERE IsDeleted = 0
            ORDER BY Id
            """;
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync(new CommandDefinition(sql, cancellationToken: ct));
        var mutableRows = rows.Select(r => (IDictionary<string, object?>)ToDictionary(r)).ToList();

        var enc = await GetEncryptionContextAsync(connection, table.AppId, null, ct);
        await enc.DecryptRowsAsync(mutableRows, fields, ct);
        return mutableRows.Cast<IReadOnlyDictionary<string, object?>>().ToList();
    }

    /// <summary>The SQL expression for a summary's target (or Combined Text sort): its f_{fid}
    /// column, a system field's own column (Id, CreatedOn, …), a Lookup's parent column (see
    /// <see cref="LookupColumnExpr"/>), or — for a composite Address field's sub-key — the
    /// JSON_VALUE-extracted part (same pattern as Address report filters).</summary>
    private static string TargetColumnExpr(int targetFid, string? targetSubField, IReadOnlyDictionary<long, AppField>? fieldLookup)
    {
        if (fieldLookup is not null && fieldLookup.TryGetValue(targetFid, out var field))
        {
            if (SummaryLookupSources.IsLookup(field)) return LookupColumnExpr(field) ?? "NULL";
            if (field.IsSystem) return ResolveColumnName(field, targetFid);
        }
        return string.IsNullOrWhiteSpace(targetSubField)
            ? PhysicalNaming.ColumnName(targetFid)
            : $"JSON_VALUE({PhysicalNaming.ColumnName(targetFid)}, '$.{SafeJsonKey(targetSubField)}')";
    }

    /// <summary>A summary query's value for a child Lookup field: the parent column it pulls down,
    /// read with a correlated subquery through the child's reference column (the child is aliased
    /// <see cref="ChildAlias"/>) — the same value the record read projects, a deleted parent giving
    /// NULL. Null when the lookup's settings are incomplete.</summary>
    private static string? LookupColumnExpr(AppField lookup, string rowAlias = ChildAlias)
    {
        var s = SummaryLookupSources.Settings(lookup);
        if (s is not { SourceTableId: long sourceTableId, ReferenceFid: int refFid, SourceFid: int sourceFid }) return null;
        var sourceCol = sourceFid switch
        {
            1 => "CreatedOn", 2 => "ModifiedOn", 3 => "Id", 4 => "CreatedBy", 5 => "ModifiedBy",
            _ => PhysicalNaming.ColumnName(sourceFid),
        };
        var value = string.IsNullOrWhiteSpace(s.SourceSubField)
            ? $"lk.{sourceCol}"
            : $"JSON_VALUE(lk.{sourceCol}, '$.{SafeJsonKey(s.SourceSubField)}')";
        return $"(SELECT {value} FROM {PhysicalNaming.FullTableName(sourceTableId)} lk "
             + $"WHERE lk.Id = {rowAlias}.{PhysicalNaming.ColumnName(refFid)} AND lk.IsDeleted = 0)";
    }

    private static string SafeJsonKey(string key) => System.Text.RegularExpressions.Regex.Replace(key, "[^a-zA-Z0-9_]", "");

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
        var enc = await GetEncryptionContextAsync(connection, table.AppId, null, ct);
        foreach (var row in rows)
        {
            var dict = (IDictionary<string, object>)row;
            if (dict.TryGetValue("Id", out var idVal) && idVal is not null)
            {
                var val = dict.TryGetValue("KeyColumnValue", out var v) && v != DBNull.Value ? v : null;
                if (val is string str && enc.IsActive)
                {
                    val = await enc.DecryptValueAsync(str, ct);
                }
                result[Convert.ToInt64(idVal)] = val;
            }
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

        var enc = await GetEncryptionContextAsync(connection, table.AppId, null, ct);
        if (enc.IsActive && result.Count < values.Count)
        {
            var valueSet = new HashSet<string>(values.Select(v => v?.ToString()?.Trim()).Where(s => !string.IsNullOrEmpty(s))!, StringComparer.OrdinalIgnoreCase);
            var scanSql = $"""
                SELECT Id, {columnName} AS KeyColumnValue
                FROM {PhysicalNaming.FullTableName(table.Id)}
                WHERE IsDeleted = 0 AND {columnName} IS NOT NULL
                """;
            var scanRows = await connection.QueryAsync(new CommandDefinition(scanSql, cancellationToken: ct));
            foreach (var r in scanRows)
            {
                var d = (IDictionary<string, object>)r;
                if (d.TryGetValue("KeyColumnValue", out var cv) && cv is string cipherStr && cipherStr.Length >= 40 && !cipherStr.Contains(' ')
                    && d.TryGetValue("Id", out var idV) && idV is not null)
                {
                    try
                    {
                        var decrypted = await enc.DecryptValueAsync(cipherStr, ct);
                        if (!string.IsNullOrEmpty(decrypted) && valueSet.Contains(decrypted.Trim()))
                        {
                            result[decrypted] = Convert.ToInt64(idV);
                            result[decrypted.Trim()] = Convert.ToInt64(idV);
                        }
                    }
                    catch { }
                }
            }
        }

        return result;
    }

    private static bool IsComputedLabel(AppField field)
        => field.Fid.HasValue && PhysicalNaming.IsComputedTypeCode(field.TypeCode);

    /// <summary>SELECT-list expression for a picker label: the field's column, or a NULL
    /// placeholder for a computed field the caller fills in after projection.</summary>
    private static string SelectLabelExpr(AppField field)
        => IsComputedLabel(field) ? "CAST(NULL AS NVARCHAR(400))" : LabelColumnExpr(field);

    private static string LabelColumnExpr(AppField? labelField)
    {
        if (labelField is null) return "CAST(Id AS NVARCHAR(400))";

        // Computed fields (Formula/Lookup/Summary/ReportLink) have no physical SQL column.
        // If one somehow reaches here, fall back to the record Id rather than generating an
        // invalid column reference that would crash the query with "Invalid column name 'f_N'".
        if (!labelField.Fid.HasValue || PhysicalNaming.IsComputedTypeCode(labelField.TypeCode))
            return "CAST(Id AS NVARCHAR(400))";

        var col = labelField.IsSystem && !string.IsNullOrEmpty(labelField.PhysicalColumnName)
            ? labelField.PhysicalColumnName!
            : PhysicalNaming.ColumnName(labelField.Fid.Value);
        return $"CAST({col} AS NVARCHAR(400))";
    }

    /// <summary>True when <paramref name="field"/>'s physical column was encrypted at write time
    /// and therefore must be decrypted before surfacing its value to the caller. Mirrors the
    /// <c>FieldsToEncrypt</c> predicate inside <see cref="Services.FieldEncryptionContext"/>.</summary>
    private static bool NeedsDecrypt(Services.FieldEncryptionContext enc, AppField? field)
        => field is not null && field.Fid.HasValue && !field.IsSystem
           && !PhysicalNaming.IsEncryptionExemptTypeCode(field.TypeCode)
           && (enc.IsAppEncrypted || field.IsEncrypted);

    public async Task<IReadOnlyDictionary<string, object?>> GetByPublicIdAsync(
        AppTable table, IReadOnlyList<AppField> fields, Guid publicId, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var fieldCols = BuildFieldColumnList(fields);
        var sql = $"""
            SELECT Id, PublicId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy{fieldCols}
            FROM {PhysicalNaming.FullTableName(table.Id)}
            WHERE PublicId = @publicId AND IsDeleted = 0
            """;

        // Same self-deadlock risk as HasValueDuplicateAsync: a pipeline action step running mid-
        // transaction (e.g. Update Record / File Upload steps in PipelineEngine) calls this as
        // ApplyAsync's "load current row" fallback. A second connection here would block on locks
        // the caller's own still-open transaction already holds on this table.
        if (transaction is not null)
        {
            var txRow = await transaction.Connection!.QuerySingleOrDefaultAsync(
                new CommandDefinition(sql, new { publicId }, transaction, cancellationToken: ct));
            if (txRow is null) throw new NotFoundException("Record", publicId);

            var txDict = ToDictionary(txRow);
            var txEnc = await GetEncryptionContextAsync(transaction.Connection!, table.AppId, transaction, ct);
            await txEnc.DecryptRowAsync((System.Collections.Generic.IDictionary<string, object?>)txDict, fields, ct);
            return txDict;
        }

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
                var raw = values.TryGetValue((long)f.Fid.Value, out var rv2) ? rv2 : null;
                parameters.Add(col, IsBlankForNonTextField(f, raw)
                    ? null
                    : (encryptedValues.TryGetValue((long)f.Fid.Value, out var ev) ? ev : raw));
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
            .ToDictionary(f => f.Fid!.Value.ToString(), f => f.IsSearchable ? values[(long)f.Fid.Value] : null);

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
                parameters.Add(col, IsBlankForNonTextField(f, values[(long)f.Fid.Value]) ? null : valToBind);
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
            .ToDictionary(f => f.Fid!.Value.ToString(), f => f.IsSearchable ? values[(long)f.Fid.Value] : null);

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
        IReadOnlyDictionary<long, object?> values, CancellationToken ct = default, Action<PowerBase.Application.Common.Models.SearchIndexMessage>? onIndexMessageCreated = null, IDbTransaction? transaction = null)
    {
        var relevantFields = fields.Where(f => f.Fid.HasValue && values.ContainsKey((long)f.Fid.Value) && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)).ToList();
        if (relevantFields.Count == 0 || recordIds.Count == 0) return 0;

        Services.FieldEncryptionContext enc;
        IReadOnlyDictionary<long, object?> encryptedValues;

        await using var ownedConnection = transaction == null ? await ConnectionFactory.CreateAsync(ct) : null;
        var connection = transaction?.Connection ?? ownedConnection
            ?? throw new InvalidOperationException("The mass update transaction has no active connection.");
        enc = await GetEncryptionContextAsync(connection, table.AppId, transaction, ct);
        if (!enc.IsActive && (enc.IsAppEncrypted || relevantFields.Any(f => f.IsEncrypted)))
            await enc.EnsureDekAsync(connection, transaction, ct);
        encryptedValues = await enc.EncryptValuesAsync(fields, values, ct);

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
                setClauses.Add($"{col} = @{col}");
                parameters.Add(col, IsBlankForNonTextField(f, values[(long)f.Fid.Value]) ? null : valToBind);
            }
        }

        // Enlist the mutation in the same transaction as its pipeline outbox entries.
        var sql = $"""
            UPDATE {PhysicalNaming.FullTableName(table.Id)}
            SET {string.Join(", ", setClauses)}, ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
            WHERE Id IN @ids AND IsDeleted = 0
            """;

        var affected = await ExecuteTranslatingUniqueViolationsAsync(() =>
            connection.ExecuteAsync(new CommandDefinition(sql, parameters, transaction, cancellationToken: ct)));

        // GAP #5: Re-index in Azure AI Search after Mass Update
        if (affected > 0 && fields.Any(f => f.IsSearchable || f.IsFilterable))
        {
            var publicIdsSql = $"SELECT PublicId FROM {PhysicalNaming.FullTableName(table.Id)} WHERE Id IN @ids";
            var publicIds = await connection.QueryAsync<Guid>(new CommandDefinition(publicIdsSql, new { ids = recordIds }, transaction, cancellationToken: ct));

            var searchableValues = fields
                .Where(f => (f.IsSearchable || f.IsFilterable) && f.Fid.HasValue && values.ContainsKey((long)f.Fid.Value))
                .ToDictionary(f => f.Fid!.Value.ToString(), f => f.IsSearchable ? values[(long)f.Fid.Value] : null);

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

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var enc = await GetEncryptionContextAsync(connection, table.AppId, null, ct);
        var valueToSet = defaultValue;
        if (enc.IsActive && NeedsDecrypt(enc, field) && !string.IsNullOrEmpty(defaultValue))
        {
            valueToSet = await enc.EncryptValueAsync(field, defaultValue, ct) ?? defaultValue;
        }

        var sql = $"""
            UPDATE {PhysicalNaming.FullTableName(table.Id)}
            SET {col} = @valueToSet
            WHERE IsDeleted = 0 AND ({col} IS NULL OR {col} = '')
            """;

        return await connection.ExecuteAsync(
            new CommandDefinition(sql, new { valueToSet }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SummarizeAsync(
        AppTable table, IReadOnlyList<(AppField Field, string Mode)> groupByFields,
        IReadOnlyList<SummaryAggregation> aggregations,
        IReadOnlyList<AppField> allFields,
        FilterGroup? filterTree = null,
        long? restrictToCreatedBy = null,
        AppField? seriesField = null,
        string seriesMode = "EqualValues",
        IReadOnlyList<SummarizeSortSpec>? sort = null,
        CancellationToken ct = default)
    {
        if (groupByFields.Count == 0) return [];

        static string ColumnOf(AppField f) => f.IsSystem && !string.IsNullOrEmpty(f.PhysicalColumnName)
            ? f.PhysicalColumnName!
            : PhysicalNaming.ColumnName(f.Fid!.Value);

        // Ordered "Rows" group levels (Summary's chained "Group by X, then by Y, ..." — Chart/
        // legacy single-level Summary always pass a 1-element list here). Each level gets its own
        // GroupValue{i} SELECT/GROUP BY/ORDER BY slot, all evaluated together with the optional
        // crosstab/series dimension in one query.
        var groupCols = groupByFields.Select(g => ColumnOf(g.Field)).ToList();
        var groupExprs = groupByFields.Select((g, i) => BuildGroupByExpr(groupCols[i], g.Mode, g.Field.TypeCode)).ToList();
        var fieldMap = allFields.GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());

        string? seriesExpr = null;
        string? seriesCol = null;
        if (seriesField is not null)
        {
            seriesCol = ColumnOf(seriesField);
            seriesExpr = BuildGroupByExpr(seriesCol, seriesMode, seriesField.TypeCode);
        }

        var parameters = new DynamicParameters();
        var ownerWhere = BuildOwnerWhere(restrictToCreatedBy, parameters);
        // fieldMap guards against computed/Formula-type conditions reaching SQL as a reference
        // to a nonexistent f_{fid} column — see BuildConditionClause's IsComputedTypeCode check.
        var filterWhere = BuildFilterTreeWhere(filterTree, parameters, fieldMap);

        // Median needs a table alias to correlate a per-group scalar subquery back to this
        // outer query's row (PERCENTILE_CONT is a T-SQL window function — it always requires
        // OVER, and can't sit directly in a GROUP BY select list alongside SUM/COUNT/etc.).
        // Only reached (aliased) when at least one aggregation actually uses Median.
        const string outerAlias = "rpt_o";
        const string medianAlias = "rpt_m";
        var tableName = PhysicalNaming.FullTableName(table.Id);

        var aggClauses = new List<string> { "COUNT(*) AS [Count]" };
        // Parallel to `aggregations`, not `aggClauses` (which is one longer, for the locked Count
        // column, and can be shorter overall when an aggregation references an unknown field) —
        // lets a SummarizeSortSpec.Aggregation.Index (assigned by RunSummaryAsync against this
        // exact `aggregations` list) find its SQL alias for ORDER BY below; null marks a skipped
        // (unknown-field) aggregation, which a sort request against it simply falls through on.
        var aggAliasesInOrder = new List<string?>();
        foreach (var agg in aggregations)
        {
            if (!fieldMap.TryGetValue(agg.FieldId, out var aggField)) { aggAliasesInOrder.Add(null); continue; }
            // Mirror groupCol/seriesCol above — a system field (e.g. Record ID#) stores its
            // value under PhysicalColumnName, not the generic f_{fid} slot; aggregating it via
            // ColumnName() alone referenced a column that never existed (SQL error 207).
            var col = aggField.IsSystem && !string.IsNullOrEmpty(aggField.PhysicalColumnName)
                ? aggField.PhysicalColumnName!
                : PhysicalNaming.ColumnName((int)agg.FieldId);
            var alias = $"[{agg.Function}_{aggField.Name.Replace(" ", "_")}]";
            // Number/Currency/Percent/Rating's "Treat blank values as 0 in calculations" Behavior
            // Setting (defaults to true when unset — see NumericSettings.Validation) — SQL Server's
            // SUM/AVG silently skip NULL rows by default, which is the opposite of "checked": that
            // setting means a blank should count as 0 in both the sum and the average's denominator,
            // not be excluded. ISNULL(...,0) makes that explicit; unchecked leaves the plain column
            // so SUM/AVG's native NULL-skipping applies (blank genuinely excluded), matching the
            // Table report's client-side footer (table-report-view.component.ts's
            // formatAggregateForField, same setting, same semantics).
            var sumAvgExpr = TreatBlankAsZero(aggField)
                ? $"ISNULL(CAST({col} AS DECIMAL(18,4)), 0)"
                : $"CAST({col} AS DECIMAL(18,4))";
            var clause = agg.Function switch
            {
                "Sum" => $"SUM({sumAvgExpr}) AS {alias}",
                "Avg" => $"AVG({sumAvgExpr}) AS {alias}",
                "Min" => $"MIN({col}) AS {alias}",
                "Max" => $"MAX({col}) AS {alias}",
                "DistinctCount" => $"COUNT(DISTINCT {col}) AS {alias}",
                "StdDev" => $"STDEV(CAST({col} AS DECIMAL(18,4))) AS {alias}",
                "Median" => BuildMedianClause(tableName, outerAlias, medianAlias, col, alias, ownerWhere, filterWhere,
                    groupCols.Select((c, i) => (c, groupByFields[i].Mode, groupByFields[i].Field.TypeCode)).ToList(),
                    seriesCol, seriesMode, seriesField?.TypeCode),
                _ => null,
            };
            if (clause is not null) aggClauses.Add(clause);
            aggAliasesInOrder.Add(clause is not null ? alias : null);
        }

        var groupSelectParts = groupExprs.Select((e, i) => $"{e} AS GroupValue{i}").ToList();
        var selectParts = new List<string>(groupSelectParts);
        if (seriesExpr is not null) selectParts.Add($"{seriesExpr} AS SeriesValue");
        selectParts.AddRange(aggClauses);
        var selectList = string.Join(", ", selectParts);

        var groupByParts = new List<string>(groupExprs);
        if (seriesExpr is not null) groupByParts.Add(seriesExpr);
        var groupByList = string.Join(", ", groupByParts);

        // Default (unchanged pre-existing behavior): ascending by every group level. A caller-
        // supplied `sort` (Summary reports only — see RunSummaryAsync) overrides this by ORDER-
        // BY-ing SQL aliases already in the SELECT list instead; any entry whose index doesn't
        // resolve (out of range, or an Aggregation index pointing at a skipped/unknown field)
        // is simply dropped rather than failing the whole query.
        var orderByClause = groupByList;
        if (sort is { Count: > 0 })
        {
            var orderParts = new List<string>();
            foreach (var s in sort)
            {
                string? expr = s.Target switch
                {
                    SummarizeSortTarget.GroupLevel when s.Index >= 0 && s.Index < groupExprs.Count => $"GroupValue{s.Index}",
                    SummarizeSortTarget.Count => "[Count]",
                    SummarizeSortTarget.Aggregation when s.Index >= 0 && s.Index < aggAliasesInOrder.Count => aggAliasesInOrder[s.Index],
                    _ => null,
                };
                if (expr is not null) orderParts.Add($"{expr} {(s.Desc ? "DESC" : "ASC")}");
            }
            if (orderParts.Count > 0) orderByClause = string.Join(", ", orderParts);
        }

        var sql = $"""
            SELECT {selectList}
            FROM {tableName} AS {outerAlias}
            WHERE {outerAlias}.IsDeleted = 0{ownerWhere}{filterWhere}
            GROUP BY {groupByList}
            ORDER BY {orderByClause}
            """;

        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var rows = await connection.QueryAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));
        var rawRows = rows.Select(ToDictionary).ToList();
        var enc = await GetEncryptionContextAsync(connection, table.AppId, null, ct);
        if (!enc.IsActive) return rawRows;

        var decryptedRows = new List<IReadOnlyDictionary<string, object?>>(rawRows.Count);
        foreach (var row in rawRows)
        {
            var dict = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);
            foreach (var (k, v) in dict.ToList())
            {
                if (v is string s && s.Length >= 40 && !s.Contains(' '))
                {
                    try
                    {
                        var decrypted = await enc.DecryptValueAsync(s, ct);
                        if (!string.IsNullOrEmpty(decrypted))
                        {
                            dict[k] = decrypted;
                        }
                    }
                    catch
                    {
                        // Not ciphertext or decrypt failed, leave as-is
                    }
                }
            }
            decryptedRows.Add(dict);
        }

        bool hasEncryptedGroup = groupByFields.Any(g => g.Field.IsEncrypted) || (seriesField?.IsEncrypted ?? false);
        if (hasEncryptedGroup && decryptedRows.Count > 1)
        {
            decryptedRows = ConsolidateSummarizedRows(decryptedRows, groupByFields.Count, seriesField is not null, aggregations, fieldMap);
        }

        return decryptedRows;
    }

    private static List<IReadOnlyDictionary<string, object?>> ConsolidateSummarizedRows(
        List<IReadOnlyDictionary<string, object?>> rows,
        int groupLevelCount,
        bool hasSeries,
        IReadOnlyList<SummaryAggregation> aggregations,
        IReadOnlyDictionary<long, AppField> fieldMap)
    {
        var groups = new Dictionary<string, (Dictionary<string, object?> Row, long TotalCount, Dictionary<string, (decimal WeightedSum, long Count)> AvgTracker)>();

        foreach (var row in rows)
        {
            var keyParts = new List<string?>();
            for (var i = 0; i < groupLevelCount; i++)
            {
                keyParts.Add(row.TryGetValue($"GroupValue{i}", out var gv) ? gv?.ToString() : "");
            }
            if (hasSeries)
            {
                keyParts.Add(row.TryGetValue("SeriesValue", out var sv) ? sv?.ToString() : "");
            }
            var groupKey = string.Join("\u001f", keyParts);

            long rowCount = row.TryGetValue("Count", out var cVal) && cVal is not null ? Convert.ToInt64(cVal) : 0;

            if (!groups.TryGetValue(groupKey, out var entry))
            {
                var copy = new Dictionary<string, object?>(row, StringComparer.OrdinalIgnoreCase);
                var avgTracker = new Dictionary<string, (decimal WeightedSum, long Count)>(StringComparer.OrdinalIgnoreCase);

                foreach (var agg in aggregations)
                {
                    if (!fieldMap.TryGetValue(agg.FieldId, out var aggField)) continue;
                    var alias = $"[{agg.Function}_{aggField.Name.Replace(" ", "_")}]";
                    if (agg.Function == "Avg" && row.TryGetValue(alias, out var avgVal) && avgVal is not null)
                    {
                        if (decimal.TryParse(avgVal.ToString(), out var d))
                            avgTracker[alias] = (d * rowCount, rowCount);
                    }
                }

                groups[groupKey] = (copy, rowCount, avgTracker);
            }
            else
            {
                entry.TotalCount += rowCount;
                entry.Row["Count"] = entry.TotalCount;

                foreach (var agg in aggregations)
                {
                    if (!fieldMap.TryGetValue(agg.FieldId, out var aggField)) continue;
                    var alias = $"[{agg.Function}_{aggField.Name.Replace(" ", "_")}]";
                    if (!row.TryGetValue(alias, out var curVal) || curVal is null) continue;

                    if (!entry.Row.TryGetValue(alias, out var existingVal) || existingVal is null)
                    {
                        entry.Row[alias] = curVal;
                        continue;
                    }

                    switch (agg.Function)
                    {
                        case "Sum":
                        case "DistinctCount":
                            if (decimal.TryParse(curVal.ToString(), out var curDec) && decimal.TryParse(existingVal.ToString(), out var exDec))
                                entry.Row[alias] = exDec + curDec;
                            break;
                        case "Min":
                            if (curVal is IComparable compCur && existingVal is IComparable compExist)
                                entry.Row[alias] = compCur.CompareTo(compExist) < 0 ? curVal : existingVal;
                            break;
                        case "Max":
                            if (curVal is IComparable compCur2 && existingVal is IComparable compExist2)
                                entry.Row[alias] = compCur2.CompareTo(compExist2) > 0 ? curVal : existingVal;
                            break;
                        case "Avg":
                            if (decimal.TryParse(curVal.ToString(), out var avgDec))
                            {
                                var (prevSum, prevCount) = entry.AvgTracker.TryGetValue(alias, out var at) ? at : (0m, 0L);
                                var newWeightedSum = prevSum + (avgDec * rowCount);
                                var newCount = prevCount + rowCount;
                                entry.AvgTracker[alias] = (newWeightedSum, newCount);
                                entry.Row[alias] = newCount > 0 ? Math.Round(newWeightedSum / newCount, 4) : 0m;
                            }
                            break;
                    }
                }
            }
        }

        return groups.Values.Select(g => (IReadOnlyDictionary<string, object?>)g.Row).ToList();
    }

    /// <summary>Number/Currency/Percent/Rating's "Treat blank values as 0 in calculations" Behavior
    /// Setting — defaults to true (matching <see cref="NumericSettings.TreatBlankAsZero"/>'s own
    /// doc comment) when the field has no Settings JSON, no Validation block, or a malformed one.</summary>
    private static bool TreatBlankAsZero(AppField field)
    {
        if (string.IsNullOrWhiteSpace(field.Settings)) return true;
        try
        {
            var settings = JsonSerializer.Deserialize<NumericSettings>(
                field.Settings, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            return settings?.TreatBlankAsZero ?? true;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>
    /// Median for a summarized aggregation column. SQL Server's PERCENTILE_CONT is an ordered-
    /// set window function — it always requires OVER, and (unlike SUM/AVG/COUNT) can't sit
    /// directly in the same GROUP BY select list as the rest of <see cref="SummarizeAsync"/>'s
    /// aggregates. Instead this builds a correlated scalar subquery: it re-scans the same table
    /// under a second alias, re-applies the exact same owner/filter restrictions plus a
    /// correlation predicate that recomputes the group (and series, if any) bucket expression
    /// and matches it back to the outer query's row, then takes PERCENTILE_CONT(0.5) OVER() —
    /// which returns the same median value for every row in that bucket — via TOP(1).
    /// NULL-vs-NULL is matched explicitly since `NULL = NULL` is unknown, not true, in SQL.
    /// </summary>
    private static string BuildMedianClause(
        string tableName, string outerAlias, string medianAlias,
        string col, string alias, string ownerWhere, string filterWhere,
        IReadOnlyList<(string Col, string Mode, string TypeCode)> groupLevels,
        string? seriesCol, string? seriesMode, string? seriesTypeCode)
    {
        var correlationParts = new List<string>();
        foreach (var (groupCol, groupByMode, groupTypeCode) in groupLevels)
        {
            var groupOuter = BuildGroupByExpr($"{outerAlias}.{groupCol}", groupByMode, groupTypeCode);
            var groupInner = BuildGroupByExpr($"{medianAlias}.{groupCol}", groupByMode, groupTypeCode);
            correlationParts.Add($"(({groupInner} = {groupOuter}) OR ({groupInner} IS NULL AND {groupOuter} IS NULL))");
        }

        if (seriesCol is not null)
        {
            var seriesOuter = BuildGroupByExpr($"{outerAlias}.{seriesCol}", seriesMode ?? "EqualValues", seriesTypeCode ?? "Text");
            var seriesInner = BuildGroupByExpr($"{medianAlias}.{seriesCol}", seriesMode ?? "EqualValues", seriesTypeCode ?? "Text");
            correlationParts.Add($"(({seriesInner} = {seriesOuter}) OR ({seriesInner} IS NULL AND {seriesOuter} IS NULL))");
        }

        var correlation = string.Join(" AND ", correlationParts);

        return $"""
            (SELECT TOP (1) PERCENTILE_CONT(0.5) WITHIN GROUP (ORDER BY CAST({medianAlias}.{col} AS DECIMAL(18,4))) OVER ()
             FROM {tableName} AS {medianAlias}
             WHERE {medianAlias}.IsDeleted = 0{ownerWhere}{filterWhere}
               AND {correlation}) AS {alias}
            """;
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

    /// <summary>The alias the summary queries (AggregateByReferenceAsync / ListValuesByReferenceAsync)
    /// give the child table, which a "parentField" condition's correlated subquery refers back to.</summary>
    private const string ChildAlias = "c";

    /// <param name="parentScope">Only for a query whose FROM aliases the child table as
    /// <see cref="ChildAlias"/> — "parentField" conditions are no-ops without it.</param>
    private static string BuildFilterTreeWhere(FilterGroup? group, DynamicParameters parameters,
        IReadOnlyDictionary<long, AppField>? fieldLookup = null, ParentFieldScope? parentScope = null)
    {
        if (group is null || group.Nodes.Count == 0) return string.Empty;
        var paramIdx = 0;
        var fragment = BuildTreeFragment(group, parameters, ref paramIdx, fieldLookup, parentScope);
        return string.IsNullOrEmpty(fragment) ? string.Empty : $" AND ({fragment})";
    }

    /// <param name="lookupAlias">For a query that isn't a summary (no <paramref name="parentScope"/>) but filters a
    /// table's own Lookup fields — the reference dropdown filters the parent table aliased <c>p</c>: the alias
    /// of the row that holds the lookup's reference column.</param>
    private static string BuildTreeFragment(FilterGroup group, DynamicParameters parameters, ref int paramIdx,
        IReadOnlyDictionary<long, AppField>? fieldLookup = null, ParentFieldScope? parentScope = null, string? lookupAlias = null)
    {
        var parts = new List<string>();
        foreach (var node in group.Nodes)
        {
            if (node.Condition is { } cond)
            {
                var clause = BuildConditionClause(cond, parameters, ref paramIdx, fieldLookup, parentScope, lookupAlias);
                if (clause is not null) parts.Add(clause);
            }
            else if (node.Group is { } sub && sub.Nodes.Count > 0)
            {
                var subSql = BuildTreeFragment(sub, parameters, ref paramIdx, fieldLookup, parentScope, lookupAlias);
                if (!string.IsNullOrEmpty(subSql)) parts.Add($"({subSql})");
            }
        }
        if (parts.Count == 0) return string.Empty;
        var joiner = group.Logic?.ToLowerInvariant() == "or" ? " OR " : " AND ";
        return string.Join(joiner, parts);
    }

    /// <summary>
    /// True when `rawValue` is a blank string being written to a field whose physical column
    /// genuinely doesn't store text (numeric/date/bit/bigint) — SQL Server can't implicitly
    /// convert an empty string to those types and throws ("Error converting data type nvarchar to
    /// ..."), so this must resolve to a real NULL write instead of being handed to Dapper's
    /// parameter binder as-is (which infers an NVARCHAR parameter from the C# string). Checked
    /// against the RAW (pre-encryption) value — encrypting a blank string for e.g. a Number field
    /// would otherwise produce non-empty ciphertext for what should just be a NULL column. Mirrors
    /// SplitRangeValue's own "normalise empty strings to null" handling below, just for the plain
    /// (non-range) field case that was missing it.
    /// </summary>
    private static bool IsBlankForNonTextField(AppField field, object? rawValue) =>
        rawValue is string s && string.IsNullOrWhiteSpace(s) && !PhysicalNaming.IsTextStoringTypeCode(field.TypeCode);

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

    private static Dictionary<long, AppField> BuildFieldLookup(IEnumerable<AppField> fields)
    {
        var lookup = new Dictionary<long, AppField>();
        foreach (var f in fields)
        {
            lookup[f.Id] = f;
        }
        foreach (var f in fields)
        {
            if (f.Fid.HasValue) lookup[(long)f.Fid.Value] = f;
        }
        return lookup;
    }

    private static string ResolveColumnName(AppField f, long fallbackFieldId)
    {
        if (!string.IsNullOrWhiteSpace(f.PhysicalColumnName))
            return f.PhysicalColumnName;

        if (f.IsSystem || (f.Fid.HasValue && f.Fid.Value is >= 1 and <= 5) || fallbackFieldId is >= 1 and <= 5)
        {
            var fid = f.Fid ?? fallbackFieldId;
            return fid switch
            {
                1 => "CreatedOn",
                2 => "ModifiedOn",
                3 => "Id",
                4 => "CreatedBy",
                5 => "ModifiedBy",
                _ => f.Fid.HasValue ? PhysicalNaming.ColumnName(f.Fid.Value) : PhysicalNaming.ColumnName((int)fallbackFieldId)
            };
        }

        if (f.Fid.HasValue)
            return PhysicalNaming.ColumnName(f.Fid.Value);

        return PhysicalNaming.ColumnName((int)fallbackFieldId);
    }

    /// <summary>Translates a user-facing wildcard pattern (* = any run of characters, ? = any
    /// single character — the Quickbase-style convention this report filter's "wildcard match"
    /// operator uses) into a SQL Server LIKE pattern. Literal '%'/'_'/'[' the user typed are
    /// bracket-escaped first (native T-SQL, no ESCAPE clause needed) so they're never misread as
    /// SQL wildcards, only THEN are '*'/'?' substituted for '%'/'_'.</summary>
    private static string TranslateWildcardPattern(string raw)
    {
        var escaped = raw
            .Replace("[", "[[]")
            .Replace("%", "[%]")
            .Replace("_", "[_]");
        return escaped.Replace("*", "%").Replace("?", "_");
    }

    /// <summary>A numeric column as text the way it reads on screen — "42500", "5.5", not the
    /// DECIMAL(18,4) storage form "42500.0000" — so text-style operators (contains, starts with,
    /// wildcard …) match what the user sees: "*5" finds 5 and 15, "contains 0" doesn't find every
    /// number. Trailing zeros after the decimal point go, then a trailing point; integer columns
    /// (Record ID#, user ids) have no point and pass through unchanged.</summary>
    private static string NumberAsShownExpr(string colExpr)
    {
        var text = $"CAST({colExpr} AS NVARCHAR(50))";
        var noTrailingZeros = $"REPLACE(RTRIM(REPLACE({text}, '0', ' ')), ' ', '0')";
        return $"(CASE WHEN CHARINDEX('.', {text}) = 0 THEN {text} "
             + $"WHEN RIGHT({noTrailingZeros}, 1) = '.' THEN LEFT({noTrailingZeros}, LEN({noTrailingZeros}) - 1) "
             + $"ELSE {noTrailingZeros} END)";
    }

    /// <summary>True for a Date &amp; Time column (a DateTime field, or the system Date Created /
    /// Date Modified), whose values carry a time of day.</summary>
    private static bool IsDateTimeColumn(string col, AppField? field) =>
        col is "CreatedOn" or "ModifiedOn"
        || (field is not null && field.TypeCode.Equals("DateTime", StringComparison.OrdinalIgnoreCase));

    private static string? BuildConditionClause(FilterCondition cond, DynamicParameters p, ref int i,
        IReadOnlyDictionary<long, AppField>? fieldLookup = null, ParentFieldScope? parentScope = null, string? lookupAlias = null)
    {
        // "the value in the field" / "the value in the parent's field" conditions legitimately
        // carry no Value at all (ValueFieldId is the comparison target instead) — don't let the
        // empty-value skip below drop them. "ask the user" conditions DO still fall through this
        // skip when left unresolved (no Value AND no ValueFieldId) — that's the intended no-op
        // behavior documented at the call site in RunReportQueryHandler.
        var isParentFieldComparison = ParentFieldScope.IsParentFieldMode(cond.ValueMode) && cond.ValueFieldId.HasValue;
        var isFieldToFieldComparison = isParentFieldComparison
            || (string.Equals(cond.ValueMode, "field", StringComparison.OrdinalIgnoreCase) && cond.ValueFieldId.HasValue);

        // Skip empty filter values for operators that require a value
        if (cond.Operator is not ("isEmpty" or "isNotEmpty") && string.IsNullOrEmpty(cond.Value) && !isFieldToFieldComparison)
            return null;

        // In a summary query (parentScope set) a Lookup is filtered on the parent column it pulls
        // down, typed as that field — see LookupColumnExpr.
        AppField? lookupAsSource = null;
        string? lookupCol = null;
        var rowAlias = lookupAlias ?? ChildAlias;
        if ((parentScope is not null || lookupAlias is not null) && fieldLookup != null && fieldLookup.TryGetValue(cond.FieldId, out var lookupField)
            && SummaryLookupSources.IsLookup(lookupField) && LookupColumnExpr(lookupField, rowAlias) is { } lookupExpr)
        {
            lookupCol = lookupExpr;
            lookupAsSource = new AppField
            {
                Fid = lookupField.Fid,
                TypeCode = SummaryLookupSources.Settings(lookupField)?.SourceTypeCode ?? "Text",
            };
        }

        // Skip formula/computed fields — they have no physical column; filtered in-memory instead.
        if (lookupCol is null && fieldLookup != null && fieldLookup.TryGetValue(cond.FieldId, out var checkField)
            && PhysicalNaming.IsComputedTypeCode(checkField.TypeCode))
            return null;

        // Use physical column name or resolved Fid/Id column name
        AppField? resolvedField = null;
        string col;
        if (lookupCol is not null)
        {
            resolvedField = lookupAsSource;
            col = lookupCol;
        }
        else if (cond.FieldId == -1)
        {
            col = "PublicId";
        }
        else if (fieldLookup != null && fieldLookup.TryGetValue(cond.FieldId, out var f))
        {
            resolvedField = f;
            col = ResolveColumnName(f, cond.FieldId);
        }
        else
        {
            col = cond.FieldId switch
            {
                1 => "CreatedOn",
                2 => "ModifiedOn",
                3 => "Id",
                4 => "CreatedBy",
                5 => "ModifiedBy",
                _ => PhysicalNaming.ColumnName((int)cond.FieldId)
            };
        }

        // Range field: SubField "start" targets f_{fid}, "end" targets f_{fid}_e (never a lookup —
        // summaries refuse range lookups in criteria)
        if (lookupCol is null && resolvedField != null && PhysicalNaming.IsRangeTypeCode(resolvedField.TypeCode) && !string.IsNullOrWhiteSpace(cond.SubField))
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

        // "the value in the field" — compare this field's column to another field's column on
        // the same row instead of a literal. Only eq/ne/gt/gte/lt/lte/date_eq are meaningful here
        // (enforced at save time by CommonReportValidationHelpers.FieldComparableOperators; an
        // operator outside that set just falls through to "not supported" below, same handling
        // as an unrecognized operator elsewhere in this method). No SubField-on-target support
        // (Address-subfield-vs-Address-subfield is out of scope) — the target always resolves to
        // its own plain column. Native SQL NULL semantics apply with no special-casing: a NULL on
        // either side makes the comparison false, same as every other operator in this method.
        //
        // "the value in the parent's field" is the same comparison, with the target read from the
        // parent record this child references: a correlated subquery on the parent's primary key,
        // so one query still covers every parent on the page. A blank parent value matches no
        // children. Without a parentScope (i.e. outside a summary query) it's a no-op.
        if (isFieldToFieldComparison)
        {
            i--; // no @pname needed for a column-vs-column comparison — release the reserved slot
            string colExpr2;
            if (isParentFieldComparison)
            {
                if (parentScope is null
                    || !parentScope.ParentFieldsByFid.TryGetValue(cond.ValueFieldId!.Value, out var parentField)
                    || PhysicalNaming.IsComputedTypeCode(parentField.TypeCode))
                    return null;
                var parentCol = ResolveColumnName(parentField, cond.ValueFieldId.Value);
                colExpr2 = $"(SELECT p.{parentCol} FROM {PhysicalNaming.FullTableName(parentScope.ParentTableId)} p "
                         + $"WHERE p.Id = {ChildAlias}.{PhysicalNaming.ColumnName(parentScope.ReferenceFid)})";
            }
            else
            {
                if (fieldLookup == null || !fieldLookup.TryGetValue(cond.ValueFieldId!.Value, out var targetField))
                    return null;
                if ((parentScope is not null || lookupAlias is not null) && SummaryLookupSources.IsLookup(targetField) && LookupColumnExpr(targetField, rowAlias) is { } otherLookup)
                    colExpr2 = otherLookup;
                else if (PhysicalNaming.IsComputedTypeCode(targetField.TypeCode))
                    return null;
                else
                    colExpr2 = ResolveColumnName(targetField, cond.ValueFieldId.Value);
            }
            if (cond.Operator == "date_eq")
                return $"CAST({colExpr} AS DATE) = CAST({colExpr2} AS DATE)";
            var sqlOp = cond.Operator switch
            {
                "eq" => "=", "ne" => "<>", "gt" => ">", "gte" => ">=", "lt" => "<", "lte" => "<=",
                _ => null,
            };
            return sqlOp is null ? null : $"{colExpr} {sqlOp} {colExpr2}";
        }

        var isNumericCol = col.Equals("Id", StringComparison.OrdinalIgnoreCase) ||
                           col.Equals("CreatedBy", StringComparison.OrdinalIgnoreCase) ||
                           col.Equals("ModifiedBy", StringComparison.OrdinalIgnoreCase) ||
                           (resolvedField != null && (
                               resolvedField.TypeCode.Equals("Number", StringComparison.OrdinalIgnoreCase) ||
                               resolvedField.TypeCode.Equals("Numeric", StringComparison.OrdinalIgnoreCase) ||
                               resolvedField.TypeCode.Equals("Currency", StringComparison.OrdinalIgnoreCase) ||
                               resolvedField.TypeCode.Equals("Percent", StringComparison.OrdinalIgnoreCase) ||
                               resolvedField.TypeCode.Equals("Rating", StringComparison.OrdinalIgnoreCase) ||
                               resolvedField.TypeCode.Equals("Duration", StringComparison.OrdinalIgnoreCase) ||
                               resolvedField.TypeCode.Equals("RecordId", StringComparison.OrdinalIgnoreCase) ||
                               resolvedField.TypeCode.Equals("Integer", StringComparison.OrdinalIgnoreCase) ||
                               resolvedField.TypeCode.Equals("Float", StringComparison.OrdinalIgnoreCase) ||
                               resolvedField.TypeCode.Equals("NumericRange", StringComparison.OrdinalIgnoreCase) ||
                               (resolvedField.IsSystem && resolvedField.PhysicalColumnName is "Id" or "CreatedBy" or "ModifiedBy")
                            ));

        var isBooleanCol = resolvedField != null && (
            resolvedField.TypeCode.Equals("Boolean", StringComparison.OrdinalIgnoreCase) ||
            resolvedField.TypeCode.Equals("Checkbox", StringComparison.OrdinalIgnoreCase));

        var isDateCol = resolvedField != null && (
            resolvedField.TypeCode.Equals("Date", StringComparison.OrdinalIgnoreCase) ||
            resolvedField.TypeCode.Equals("DateTime", StringComparison.OrdinalIgnoreCase) ||
            resolvedField.TypeCode.Equals("Date_Time", StringComparison.OrdinalIgnoreCase) ||
            resolvedField.TypeCode.Equals("Timestamp", StringComparison.OrdinalIgnoreCase));

        Func<string?, object?> formatVal = rawVal =>
        {
            if (rawVal == null) return null;
            if (isNumericCol)
            {
                if (long.TryParse(rawVal, out var l)) return l;
                if (decimal.TryParse(rawVal, out var d)) return d;
            }
            if (isBooleanCol)
            {
                if (bool.TryParse(rawVal, out var b)) return b;
                if (rawVal == "1") return true;
                if (rawVal == "0") return false;
            }
            if (isDateCol && DateTime.TryParse(rawVal, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out var date))
                return date;
            return rawVal;
        };

        var isStringCol = resolvedField != null && (
            resolvedField.TypeCode.Equals("Text", StringComparison.OrdinalIgnoreCase) ||
            resolvedField.TypeCode.Equals("MultiLineText", StringComparison.OrdinalIgnoreCase) ||
            resolvedField.TypeCode.Equals("Email", StringComparison.OrdinalIgnoreCase) ||
            resolvedField.TypeCode.Equals("Phone", StringComparison.OrdinalIgnoreCase) ||
            resolvedField.TypeCode.Equals("SingleSelect", StringComparison.OrdinalIgnoreCase) ||
            resolvedField.TypeCode.Equals("MultiSelect", StringComparison.OrdinalIgnoreCase) ||
            resolvedField.TypeCode.Equals("Address", StringComparison.OrdinalIgnoreCase)
        );

        var stringColExpr = isStringCol ? colExpr
            : isNumericCol ? NumberAsShownExpr(colExpr)
            : $"CAST({colExpr} AS NVARCHAR(MAX))";

        // A Date & Time column against a plain date ("is after 09-28-2026", "today", "during the
        // current day" — which arrives here as gte/lte of dates) compares whole days: the date
        // means that day, not its midnight. Otherwise "is on or before today" would drop today's
        // records and "is after today" keep them. Kept sargable: ranges on the column itself.
        if (IsDateTimeColumn(col, resolvedField) && string.IsNullOrWhiteSpace(cond.SubField)
            && cond.Operator is "eq" or "ne" or "gt" or "gte" or "lt" or "lte"
            && DateTime.TryParseExact(cond.Value, "yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var day))
        {
            var nextDay = $"{pname}next";
            p.Add(pname, day);
            p.Add(nextDay, day.AddDays(1));
            return cond.Operator switch
            {
                "eq" => $"({colExpr} >= @{pname} AND {colExpr} < @{nextDay})",
                "ne" => $"({colExpr} < @{pname} OR {colExpr} >= @{nextDay})",
                "gt" => $"{colExpr} >= @{nextDay}",
                "gte" => $"{colExpr} >= @{pname}",
                "lt" => $"{colExpr} < @{pname}",
                _ => $"{colExpr} < @{nextDay}",   // lte
            };
        }

        switch (cond.Operator)
        {
            case "eq":
            {
                var val = formatVal(cond.Value);
                p.Add(pname, val);
                var targetExpr = (isNumericCol && val is string) ? stringColExpr : colExpr;
                return $"{targetExpr} = @{pname}";
            }
            case "ne":
            {
                var val = formatVal(cond.Value);
                p.Add(pname, val);
                var targetExpr = (isNumericCol && val is string) ? stringColExpr : colExpr;
                return $"{targetExpr} <> @{pname}";
            }
            case "gt":
            {
                var val = formatVal(cond.Value);
                p.Add(pname, val);
                var targetExpr = (isNumericCol && val is string) ? stringColExpr : colExpr;
                return $"{targetExpr} > @{pname}";
            }
            case "gte":
            {
                var val = formatVal(cond.Value);
                p.Add(pname, val);
                var targetExpr = (isNumericCol && val is string) ? stringColExpr : colExpr;
                return $"{targetExpr} >= @{pname}";
            }
            case "lt":
            {
                var val = formatVal(cond.Value);
                p.Add(pname, val);
                var targetExpr = (isNumericCol && val is string) ? stringColExpr : colExpr;
                return $"{targetExpr} < @{pname}";
            }
            case "lte":
            {
                var val = formatVal(cond.Value);
                p.Add(pname, val);
                var targetExpr = (isNumericCol && val is string) ? stringColExpr : colExpr;
                return $"{targetExpr} <= @{pname}";
            }
            //case "date_eq": p.Add(pname, formatVal(cond.Value)); return $"CAST({colExpr} AS DATE) = CAST(@{pname} AS DATE)";
            //case "date_ne": p.Add(pname, formatVal(cond.Value)); return $"CAST({colExpr} AS DATE) <> CAST(@{pname} AS DATE)";

            // date_* operators compare a calendar day, not an instant. The column stores a UTC
            // instant (Date Created/Modified are timestamps, not dates), and the picker value is
            // the exact UTC instant of the picked day's local midnight — so CAST(col AS DATE) =
            // CAST(@val AS DATE) truncates the column to its own UTC calendar day, which is not
            // necessarily the local day the user picked (two records "the same local day" can sit
            // on different UTC calendar days depending on time-of-day). A day-window comparison
            // anchored on that local-midnight instant matches the day the user actually picked,
            // regardless of which UTC day each row's timestamp happens to fall on.
            case "date_eq":  p.Add(pname, formatVal(cond.Value)); return $"({colExpr} >= @{pname} AND {colExpr} < DATEADD(day, 1, @{pname}))";
            case "date_ne":  p.Add(pname, formatVal(cond.Value)); return $"NOT ({colExpr} >= @{pname} AND {colExpr} < DATEADD(day, 1, @{pname}))";
            case "date_gt":  p.Add(pname, formatVal(cond.Value)); return $"{colExpr} >= DATEADD(day, 1, @{pname})";
            case "date_gte": p.Add(pname, formatVal(cond.Value)); return $"{colExpr} >= @{pname}";
            case "date_lt":  p.Add(pname, formatVal(cond.Value)); return $"{colExpr} < @{pname}";
            case "date_lte": p.Add(pname, formatVal(cond.Value)); return $"{colExpr} < DATEADD(day, 1, @{pname})";
            case "contains":       p.Add(pname, $"%{cond.Value?.ToLower()}%"); return $"LOWER({stringColExpr}) LIKE @{pname}";
            case "notContains":    p.Add(pname, $"%{cond.Value?.ToLower()}%"); return $"LOWER({stringColExpr}) NOT LIKE @{pname}";
            case "startsWith":     p.Add(pname, $"{cond.Value?.ToLower()}%");  return $"LOWER({stringColExpr}) LIKE @{pname}";
            case "notStartsWith":  p.Add(pname, $"{cond.Value?.ToLower()}%");  return $"LOWER({stringColExpr}) NOT LIKE @{pname}";
            case "endsWith":       p.Add(pname, $"%{cond.Value?.ToLower()}");  return $"LOWER({stringColExpr}) LIKE @{pname}";
            case "wildcard":
            {
                p.Add(pname, TranslateWildcardPattern(cond.Value ?? "").ToLower());
                return $"LOWER({stringColExpr}) LIKE @{pname}";
            }
            case "notWildcard":
            {
                p.Add(pname, TranslateWildcardPattern(cond.Value ?? "").ToLower());
                return $"LOWER({stringColExpr}) NOT LIKE @{pname}";
            }
            case "includes":
            case "notIncludes":
            {
                // MultiSelect/MultiUser columns store either a JSON array ("["Red","Blue"]") or a
                // comma list ("Red,Blue") — normalize both into one comma-delimited, comma-wrapped
                // form so a boundary-anchored LIKE can't false-positive-match a substring of a
                // different element (e.g. "includes Red" must not match a stored "Bred").
                var normalizedExpr = $"(',' + REPLACE(REPLACE(REPLACE({stringColExpr}, '[', ','), ']', ','), '\"', ',') + ',')";
                p.Add(pname, $"%,{cond.Value?.ToLower()},%");
                var op = cond.Operator == "includes" ? "LIKE" : "NOT LIKE";
                return $"LOWER({normalizedExpr}) {op} @{pname}";
            }
            // '' only means blank in a column that stores text. Anywhere else SQL Server converts
            // it instead: a DECIMAL column throws ("Error converting data type varchar to
            // numeric"), an INT column matches 0 and a DATE column 1900-01-01 — so those check
            // NULL alone. The system columns (Fid 1-5) never store text; any other column whose
            // field isn't known here keeps both checks.
            case "isEmpty":
            case "isNotEmpty":
            {
                i--;
                var storesText = resolvedField is not null
                    ? PhysicalNaming.IsTextStoringTypeCode(resolvedField.TypeCode)
                    : cond.FieldId > 5;
                var isEmpty = cond.Operator == "isEmpty";
                if (!storesText) return isEmpty ? $"{colExpr} IS NULL" : $"{colExpr} IS NOT NULL";
                return isEmpty ? $"({colExpr} IS NULL OR {colExpr} = '')" : $"({colExpr} IS NOT NULL AND {colExpr} <> '')";
            }
            case "in":
            case "notIn":
            {
                var values = ParseValueList(cond.Value);
                if (values.Count == 0) { i--; return null; }
                var names = new List<string>(values.Count);
                var hasNonNumeric = false;
                var formattedValues = new List<object?>(values.Count);
                foreach (var v in values)
                {
                    var fv = formatVal(v);
                    if (fv is string) hasNonNumeric = true;
                    formattedValues.Add(fv);
                }

                for (var idx = 0; idx < formattedValues.Count; idx++)
                {
                    var pn = $"fv{i++}";
                    p.Add(pn, formattedValues[idx]);
                    names.Add($"@{pn}");
                }
                var op = cond.Operator == "in" ? "IN" : "NOT IN";
                var targetExpr = (isNumericCol && hasNonNumeric) ? stringColExpr : colExpr;
                return $"{targetExpr} {op} ({string.Join(",", names)})";
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
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                var list = new List<string>();
                foreach (var el in doc.RootElement.EnumerateArray())
                {
                    var str = el.ValueKind == JsonValueKind.String ? el.GetString() : el.GetRawText();
                    if (!string.IsNullOrWhiteSpace(str)) list.Add(str.Trim('"'));
                }
                return list;
            }
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
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        var enc = await GetEncryptionContextAsync(connection, table.AppId, null, ct);

        if (enc.IsActive && NeedsDecrypt(enc, field))
        {
            var sqlEnc = $"""
                SELECT {col} FROM {PhysicalNaming.FullTableName(table.Id)}
                WHERE IsDeleted = 0 AND {col} IS NOT NULL
                """;
            var rawCiphers = await connection.QueryAsync<string>(new CommandDefinition(sqlEnc, cancellationToken: ct));
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cipher in rawCiphers)
            {
                if (string.IsNullOrEmpty(cipher)) continue;
                try
                {
                    var decrypted = await enc.DecryptValueAsync(cipher, ct);
                    var key = decrypted?.Trim() ?? "";
                    if (!string.IsNullOrEmpty(key) && !seen.Add(key))
                        return true;
                }
                catch { }
            }
            return false;
        }

        var sql = $"""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT {col} FROM {PhysicalNaming.FullTableName(table.Id)}
                WHERE IsDeleted = 0 AND {col} IS NOT NULL
                GROUP BY {col} HAVING COUNT(*) > 1
            ) THEN 1 ELSE 0 END AS BIT)
            """;
        return await connection.ExecuteScalarAsync<bool>(new CommandDefinition(sql, cancellationToken: ct));
    }

    public async Task<bool> HasValueDuplicateAsync(AppTable table, AppField field, object value, long? excludeRecordId = null, IDbTransaction? transaction = null, CancellationToken ct = default)
    {
        var col = PhysicalNaming.ColumnName(field.Fid!.Value);
        await using var ownedConnection = transaction is null ? await ConnectionFactory.CreateAsync(ct) : null;
        var connection = transaction?.Connection ?? ownedConnection!;
        var enc = await GetEncryptionContextAsync(connection, table.AppId, transaction, ct);

        if (enc.IsActive && NeedsDecrypt(enc, field))
        {
            var targetStr = value?.ToString()?.Trim();
            if (string.IsNullOrEmpty(targetStr)) return false;

            var sqlEnc = $"""
                SELECT {col} FROM {PhysicalNaming.FullTableName(table.Id)}
                WHERE IsDeleted = 0 AND {col} IS NOT NULL
                  AND (@excludeRecordId IS NULL OR Id <> @excludeRecordId)
                """;
            var rawCiphers = await connection.QueryAsync<string>(
                new CommandDefinition(sqlEnc, new { excludeRecordId }, transaction, cancellationToken: ct));

            foreach (var cipher in rawCiphers)
            {
                if (string.IsNullOrEmpty(cipher)) continue;
                try
                {
                    var decrypted = await enc.DecryptValueAsync(cipher, ct);
                    if (string.Equals(decrypted?.Trim(), targetStr, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
                catch { }
            }
            return false;
        }

        var sql = $"""
            SELECT CAST(CASE WHEN EXISTS (
                SELECT 1 FROM {PhysicalNaming.FullTableName(table.Id)}
                WHERE IsDeleted = 0 AND {col} = @value
                  AND (@excludeRecordId IS NULL OR Id <> @excludeRecordId)
            ) THEN 1 ELSE 0 END AS BIT)
            """;

        // When called mid-write (e.g. bulk upsert's per-row constraint check), the caller's own
        // transaction may already hold locks on this table from earlier rows in the same commit.
        // Opening a second connection here — instead of reusing transaction.Connection — makes
        // that second connection block on those locks under READ COMMITTED, while the first
        // connection sits waiting for THIS call to return: a self-deadlock that only resolves via
        // command timeout ("Execution Timeout Expired"). Same fix as UpdateAsync/CreateAsync.
        if (transaction is not null)
        {
            return await transaction.Connection!.ExecuteScalarAsync<bool>(
                new CommandDefinition(sql, new { value, excludeRecordId }, transaction, cancellationToken: ct));
        }

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
                                    if (!sf.IsSearchable)
                                    {
                                        searchPayload[sf.Fid.Value.ToString()] = null;
                                    }
                                    else if (sf.IsEncrypted)
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

            if (isNullify || !field.IsSearchable)
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
