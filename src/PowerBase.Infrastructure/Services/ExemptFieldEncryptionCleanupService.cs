using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// Service that scans and migrates fields whose TypeCode is encryption-exempt
/// (Number, Date, Currency, Boolean, User, etc.) but were previously created with
/// IsEncrypted = true.
/// </summary>
public class ExemptFieldEncryptionCleanupService : IExemptFieldEncryptionCleanupService
{
    private const int BatchSize = 5000;

    private readonly IQueryContext _queryContext;
    private readonly IAppRepository _appRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly IAppFieldRepository _fieldRepo;
    private readonly ITenantConnectionFactory _connectionFactory;
    private readonly IControlConnectionFactory _controlFactory;
    private readonly IEncryptionService _encryptionService;
    private readonly ILogger<ExemptFieldEncryptionCleanupService> _logger;

    private static readonly IReadOnlyDictionary<string, string> NativeSqlType =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["Number"]       = "DECIMAL(18,4)",
            ["Currency"]     = "DECIMAL(18,4)",
            ["Percent"]      = "DECIMAL(18,4)",
            ["NumericRange"] = "DECIMAL(18,4)",
            ["Rating"]       = "INT",
            ["Duration"]     = "INT",
            ["Date"]         = "DATE",
            ["DateTime"]     = "DATETIME2(3)",
            ["Time"]         = "TIME",
            ["DateRange"]    = "DATE",
            ["Boolean"]      = "BIT",
            ["User"]         = "NVARCHAR(500)",
            ["MultiUser"]    = "NVARCHAR(MAX)",
        };

    public ExemptFieldEncryptionCleanupService(
        IQueryContext queryContext,
        IAppRepository appRepo,
        IAppTableRepository tableRepo,
        IAppFieldRepository fieldRepo,
        ITenantConnectionFactory connectionFactory,
        IControlConnectionFactory controlFactory,
        IEncryptionService encryptionService,
        ILogger<ExemptFieldEncryptionCleanupService> logger)
    {
        _queryContext = queryContext;
        _appRepo = appRepo;
        _tableRepo = tableRepo;
        _fieldRepo = fieldRepo;
        _connectionFactory = connectionFactory;
        _controlFactory = controlFactory;
        _encryptionService = encryptionService;
        _logger = logger;
    }

    private static string NormalizePlaintextForSql(string plain, string typeCode)
    {
        if (string.IsNullOrWhiteSpace(plain)) return plain;

        switch (typeCode.ToLowerInvariant())
        {
            case "date":
            case "daterange":
                if (DateTimeOffset.TryParse(plain, out var d))
                    return d.ToString("yyyy-MM-dd");
                break;

            case "datetime":
                if (DateTimeOffset.TryParse(plain, out var dt))
                    return dt.ToString("yyyy-MM-dd HH:mm:ss.fff");
                break;

            case "time":
                if (TimeSpan.TryParse(plain, out var ts))
                    return ts.ToString(@"hh\:mm\:ss");
                if (DateTimeOffset.TryParse(plain, out var t))
                    return t.ToString("HH:mm:ss");
                break;

            case "boolean":
                if (bool.TryParse(plain, out var b))
                    return b ? "1" : "0";
                if (plain == "1" || plain == "0")
                    return plain;
                break;

            case "number":
            case "currency":
            case "percent":
            case "numericrange":
            case "rating":
            case "duration":
                if (decimal.TryParse(plain, System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var dec))
                    return dec.ToString(System.Globalization.CultureInfo.InvariantCulture);
                break;
        }

        return plain;
    }

    /// <summary>
    /// An encrypted app keeps a whole Date / Numeric Range as one JSON value
    /// ({"start":..,"end":..}) in the start column. Splits it into its two ends;
    /// false when the value is not such JSON.
    /// </summary>
    private static bool TrySplitRangeJson(string plain, out string? start, out string? end)
    {
        start = end = null;
        if (!plain.TrimStart().StartsWith('{')) return false;
        try
        {
            using var doc = JsonDocument.Parse(plain);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            start = RangeEndText(doc.RootElement, "start");
            end   = RangeEndText(doc.RootElement, "end");
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string? RangeEndText(JsonElement range, string name)
    {
        if (!range.TryGetProperty(name, out var value)) return null;
        var text = value.ValueKind switch
        {
            JsonValueKind.String => value.GetString(),
            JsonValueKind.Null or JsonValueKind.Undefined => null,
            _ => value.GetRawText(),
        };
        return string.IsNullOrWhiteSpace(text) ? null : text;
    }

    public async Task<List<CleanupExemptFieldEncryptionResult>> CleanupAllTenantsAsync(CancellationToken ct = default)
    {
        var results = new List<CleanupExemptFieldEncryptionResult>();

        List<(long Id, string Name)> tenants;
        await using (var controlConn = _controlFactory.Create())
        {
            await controlConn.OpenAsync(ct);
            var rows = await controlConn.QueryAsync<(long Id, string Name)>(
                "SELECT Id, Name FROM meta.Tenant WHERE IsDeleted = 0 AND Status = 'Active' ORDER BY Id",
                commandTimeout: 60);
            tenants = rows.ToList();
        }

        _logger.LogInformation("Found {Count} active tenant(s) to process for exempt field encryption cleanup.", tenants.Count);

        foreach (var tenant in tenants)
        {
            try
            {
                var result = await CleanupTenantAsync(tenant.Id, ct);
                result.TenantName = tenant.Name;
                results.Add(result);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed exempt field encryption cleanup for Tenant {TenantName} (Id={TenantId})", tenant.Name, tenant.Id);
                results.Add(new CleanupExemptFieldEncryptionResult
                {
                    TenantId = tenant.Id,
                    TenantName = tenant.Name,
                    Error = ex.Message
                });
            }
        }

        return results;
    }

    public async Task<CleanupExemptFieldEncryptionResult> CleanupTenantAsync(long tenantId, CancellationToken ct = default)
    {
        _queryContext.SetTenantId(tenantId);

        var result = new CleanupExemptFieldEncryptionResult
        {
            TenantId = tenantId
        };

        _logger.LogInformation("=== [CleanupExemptFieldEncryption] START — TenantId={TenantId} ===", tenantId);

        int page = 1;
        while (true)
        {
            var apps = await _appRepo.ListAsync(page, 100, ct);
            if (apps.Count == 0) break;

            foreach (var app in apps)
            {
                result.AppsScanned++;
                var tables = await _tableRepo.ListByAppAsync(app.Id, ct);

                foreach (var table in tables)
                {
                    result.TablesScanned++;
                    try
                    {
                        var fields = await _fieldRepo.ListByTableAsync(table.Id, ct);

                        var affectedFields = fields
                            .Where(f => f.IsEncrypted
                                && f.Fid.HasValue
                                && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)
                                && PhysicalNaming.IsEncryptionExemptTypeCode(f.TypeCode)
                                && NativeSqlType.ContainsKey(f.TypeCode))
                            .ToList();

                        if (affectedFields.Count == 0) continue;

                        _logger.LogInformation(
                            "  Table '{TableName}' (Id={TableId}) in App '{AppName}' — " +
                            "{Count} exempt-but-encrypted field(s): [{Fields}]",
                            table.Name, table.Id, app.Name, affectedFields.Count,
                            string.Join(", ", affectedFields.Select(f => $"{f.Name}({f.TypeCode})")));

                        await using var conn = await _connectionFactory.CreateAsync(ct);
                        await conn.OpenAsync(ct);

                        var encCtx = await FieldEncryptionContext.ResolveAsync(
                            conn, app.Id, tenantId, _encryptionService, null, ct);

                        foreach (var field in affectedFields)
                        {
                            var fieldResult = await ProcessFieldAsync(
                                conn, table, field, encCtx, tenantId, app.Id, ct);

                            result.FieldsFixed += fieldResult.Fixed ? 1 : 0;
                            result.FieldsFailed += fieldResult.Fixed ? 0 : 1;
                            result.RowsDecrypted += fieldResult.RowsDecrypted;

                            if (fieldResult.Fixed)
                            {
                                _logger.LogInformation(
                                    "    ✓ Field '{FieldName}' (fid={Fid}, type={TypeCode}): " +
                                    "{Rows} rows decrypted, column → {NativeType}, IsEncrypted cleared.",
                                    field.Name, field.Fid, field.TypeCode,
                                    fieldResult.RowsDecrypted, NativeSqlType[field.TypeCode]);
                            }
                            else
                            {
                                _logger.LogWarning(
                                    "    ✗ Field '{FieldName}' (fid={Fid}, type={TypeCode}): FAILED — {Error}",
                                    field.Name, field.Fid, field.TypeCode, fieldResult.Error);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        result.TablesFailed++;
                        _logger.LogError(ex,
                            "  ERROR processing table '{TableName}' (Id={TableId}).",
                            table.Name, table.Id);
                    }
                }
            }
            page++;
        }

        _logger.LogInformation(
            "=== [CleanupExemptFieldEncryption] DONE — TenantId={TenantId} | " +
            "Apps={Apps}, Tables={Tables} ({TablesFailed} failed), " +
            "Fields fixed={FieldsFixed} ({FieldsFailed} failed), " +
            "Rows decrypted={RowsDecrypted} ===",
            tenantId,
            result.AppsScanned, result.TablesScanned, result.TablesFailed,
            result.FieldsFixed, result.FieldsFailed, result.RowsDecrypted);

        return result;
    }

    private async Task<FieldProcessResult> ProcessFieldAsync(
        SqlConnection conn,
        AppTable table,
        AppField field,
        FieldEncryptionContext encCtx,
        long tenantId, long appId,
        CancellationToken ct)
    {
        var physicalTable = PhysicalNaming.FullTableName(table.Id);
        var tableName     = PhysicalNaming.TableName(table.Id);
        var physicalCol   = PhysicalNaming.ColumnName(field.Fid!.Value);
        var newCol        = $"{physicalCol}_new";
        var nativeType    = NativeSqlType[field.TypeCode];
        var isRange       = PhysicalNaming.IsRangeTypeCode(field.TypeCode);
        var endCol        = PhysicalNaming.EndColumnName(field.Fid!.Value);
        int rowsDecrypted = 0;

        try
        {
            // A range's end column: a JSON range read from the start column writes its end there.
            // A VARCHAR end column is converted afterwards (CleanupRangeEndColumnAsync), so it takes text.
            string? endColType = isRange
                ? await conn.ExecuteScalarAsync<string?>($"""
                    SELECT ty.name FROM sys.columns c
                    JOIN sys.types ty ON ty.user_type_id = c.user_type_id
                    WHERE c.object_id = OBJECT_ID('{physicalTable}') AND c.name = '{endCol}'
                    """)
                : null;
            var endValueExpr = endColType != null && endColType.Contains("char", StringComparison.OrdinalIgnoreCase)
                ? "m.e"
                : $"TRY_CONVERT({nativeType}, m.e)";

            // 1. Add new column with native type
            await conn.ExecuteAsync($"ALTER TABLE {physicalTable} DROP COLUMN IF EXISTS {newCol};");
            await conn.ExecuteAsync($"ALTER TABLE {physicalTable} ADD {newCol} {nativeType} NULL;");

            // 2. Batch decrypt & populate new column
            long lastId = 0;
            while (true)
            {
                var batch = (await conn.QueryAsync<(long Id, string? Value)>($"""
                    SELECT TOP ({BatchSize}) Id, {physicalCol}
                    FROM {physicalTable}
                    WHERE Id > @lastId AND IsDeleted = 0 AND {physicalCol} IS NOT NULL
                    ORDER BY Id
                    """, new { lastId }, commandTimeout: 300)).ToList();

                if (batch.Count == 0) break;
                lastId = batch.Max(r => r.Id);

                var plainByRowId = new Dictionary<long, string?>();
                var endByRowId = new Dictionary<long, string?>();
                void Add(long rowId, string value)
                {
                    if (isRange && TrySplitRangeJson(value, out var start, out var end))
                    {
                        plainByRowId[rowId] = start is null ? null : NormalizePlaintextForSql(start, field.TypeCode);
                        endByRowId[rowId] = end is null ? null : NormalizePlaintextForSql(end, field.TypeCode);
                    }
                    else
                    {
                        plainByRowId[rowId] = NormalizePlaintextForSql(value, field.TypeCode);
                    }
                }

                foreach (var (rowId, cipher) in batch)
                {
                    if (string.IsNullOrEmpty(cipher)) continue;
                    try
                    {
                        if (encCtx.IsActive && !string.IsNullOrEmpty(encCtx.WrappedDek))
                        {
                            var plain = await _encryptionService.DecryptDataAsync(
                                cipher, encCtx.WrappedDek, tenantId, appId, ct);
                            if (!string.IsNullOrEmpty(plain))
                            {
                                Add(rowId, plain);
                                rowsDecrypted++;
                                continue;
                            }
                        }
                        Add(rowId, cipher);
                    }
                    catch
                    {
                        Add(rowId, cipher);
                    }
                }

                if (plainByRowId.Count > 0)
                {
                    var json = JsonSerializer.Serialize(
                        plainByRowId.Select(p => new { id = p.Key, v = p.Value }));

                    await conn.ExecuteAsync($"""
                        UPDATE t
                        SET {newCol} = TRY_CONVERT({nativeType}, m.v)
                        FROM {physicalTable} t
                        JOIN OPENJSON(@json) WITH (id BIGINT '$.id', v NVARCHAR(MAX) '$.v') m
                          ON t.Id = m.id;
                        """,
                        new { json }, commandTimeout: 300);
                }

                if (endByRowId.Count > 0 && endColType != null)
                {
                    var endJson = JsonSerializer.Serialize(
                        endByRowId.Select(p => new { id = p.Key, e = p.Value }));

                    await conn.ExecuteAsync($"""
                        UPDATE t
                        SET {endCol} = {endValueExpr}
                        FROM {physicalTable} t
                        JOIN OPENJSON(@endJson) WITH (id BIGINT '$.id', e NVARCHAR(MAX) '$.e') m
                          ON t.Id = m.id;
                        """,
                        new { endJson }, commandTimeout: 300);
                }
            }

            // 3. Drop old column and rename new column in one transaction
            await using var tx = await conn.BeginTransactionAsync(ct);
            try
            {
                // 3a. Drop any index on physicalCol or physicalCol_unique_hash
                await conn.ExecuteAsync($"""
                    DECLARE @sqlIdx NVARCHAR(MAX) = N'';
                    SELECT @sqlIdx += N'DROP INDEX ' + QUOTENAME(i.name) + N' ON {physicalTable};'
                    FROM sys.indexes i
                    JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                    JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                    WHERE i.object_id = OBJECT_ID('{physicalTable}')
                      AND (c.name = '{physicalCol}' OR c.name = '{physicalCol}_unique_hash');
                    IF @sqlIdx <> N'' EXEC sp_executesql @sqlIdx;
                    """, transaction: tx, commandTimeout: 300);

                // 3b. Drop unique hash computed column if exists
                await conn.ExecuteAsync($"""
                    IF COL_LENGTH('{physicalTable}', '{physicalCol}_unique_hash') IS NOT NULL
                        ALTER TABLE {physicalTable} DROP COLUMN {physicalCol}_unique_hash;
                    """, transaction: tx, commandTimeout: 300);

                // 3c. Drop default constraints if any
                await conn.ExecuteAsync($"""
                    DECLARE @sqlDef NVARCHAR(MAX) = N'';
                    SELECT @sqlDef += N'ALTER TABLE {physicalTable} DROP CONSTRAINT ' + QUOTENAME(dc.name) + N';'
                    FROM sys.default_constraints dc
                    JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
                    WHERE dc.parent_object_id = OBJECT_ID('{physicalTable}')
                      AND c.name = '{physicalCol}';
                    IF @sqlDef <> N'' EXEC sp_executesql @sqlDef;
                    """, transaction: tx, commandTimeout: 300);

                // 3d. Drop old column
                await conn.ExecuteAsync(
                    $"ALTER TABLE {physicalTable} DROP COLUMN {physicalCol};",
                    transaction: tx, commandTimeout: 300);

                // 3e. Rename new column
                await conn.ExecuteAsync(
                    $"EXEC sp_rename '{physicalTable}.{newCol}', '{physicalCol}', 'COLUMN';",
                    transaction: tx);

                // 3f. Recreate unique index if field was unique
                if (field.IsUnique)
                {
                    var indexName = $"UX_{tableName}_{physicalCol}";
                    await conn.ExecuteAsync($"""
                        IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE object_id = OBJECT_ID('{physicalTable}') AND name = '{indexName}')
                            CREATE UNIQUE NONCLUSTERED INDEX {indexName}
                            ON {physicalTable}({physicalCol})
                            WHERE {physicalCol} IS NOT NULL AND IsDeleted = 0;
                        """, transaction: tx, commandTimeout: 300);
                }

                // 3g. Clear IsEncrypted in metadata
                await conn.ExecuteAsync("""
                    UPDATE meta.AppField
                    SET IsEncrypted = 0, ModifiedOn = SYSUTCDATETIME()
                    WHERE Id = @fieldId
                    """, new { fieldId = field.Id }, transaction: tx);

                await tx.CommitAsync(ct);
            }
            catch
            {
                await tx.RollbackAsync(ct);
                throw;
            }

            // 4. Handle range end column for DateRange / NumericRange
            if (PhysicalNaming.IsRangeTypeCode(field.TypeCode))
            {
                await CleanupRangeEndColumnAsync(
                    conn, physicalTable, tableName, field, nativeType, encCtx, tenantId, appId, ct);
            }

            return new FieldProcessResult(true, rowsDecrypted, null);
        }
        catch (Exception ex)
        {
            return new FieldProcessResult(false, rowsDecrypted, ex.Message);
        }
    }

    private async Task CleanupRangeEndColumnAsync(
        SqlConnection conn,
        string physicalTable,
        string tableName,
        AppField field,
        string nativeType,
        FieldEncryptionContext encCtx,
        long tenantId,
        long appId,
        CancellationToken ct)
    {
        var endCol    = PhysicalNaming.EndColumnName(field.Fid!.Value);
        var newEndCol = $"{endCol}_new";

        var colType = await conn.ExecuteScalarAsync<string?>($"""
            SELECT ty.name FROM sys.columns c
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            JOIN sys.tables t ON t.object_id = c.object_id
            JOIN sys.schemas s ON s.schema_id = t.schema_id
            WHERE s.name = 'data' AND t.name = '{tableName}' AND c.name = '{endCol}'
            """);

        if (colType is null || !colType.Contains("varchar", StringComparison.OrdinalIgnoreCase))
            return;

        await conn.ExecuteAsync($"ALTER TABLE {physicalTable} DROP COLUMN IF EXISTS {newEndCol};");
        await conn.ExecuteAsync($"ALTER TABLE {physicalTable} ADD {newEndCol} {nativeType} NULL;");

        long lastEndId = 0;
        while (true)
        {
            var batch = (await conn.QueryAsync<(long Id, string? Value)>($"""
                SELECT TOP ({BatchSize}) Id, {endCol}
                FROM {physicalTable}
                WHERE Id > @lastEndId AND IsDeleted = 0 AND {endCol} IS NOT NULL
                ORDER BY Id
                """, new { lastEndId }, commandTimeout: 300)).ToList();

            if (batch.Count == 0) break;
            lastEndId = batch.Max(r => r.Id);

            var plainByRowId = new Dictionary<long, string>();
            foreach (var (rowId, cipher) in batch)
            {
                if (string.IsNullOrEmpty(cipher)) continue;
                try
                {
                    if (encCtx.IsActive && !string.IsNullOrEmpty(encCtx.WrappedDek))
                    {
                        var plain = await _encryptionService.DecryptDataAsync(
                            cipher, encCtx.WrappedDek, tenantId, appId, ct);
                        if (!string.IsNullOrEmpty(plain))
                        {
                            plainByRowId[rowId] = NormalizePlaintextForSql(plain, field.TypeCode);
                            continue;
                        }
                    }
                    plainByRowId[rowId] = NormalizePlaintextForSql(cipher, field.TypeCode);
                }
                catch
                {
                    plainByRowId[rowId] = NormalizePlaintextForSql(cipher, field.TypeCode);
                }
            }

            if (plainByRowId.Count > 0)
            {
                var json = JsonSerializer.Serialize(
                    plainByRowId.Select(p => new { id = p.Key, v = p.Value }));

                await conn.ExecuteAsync($"""
                    UPDATE t SET {newEndCol} = TRY_CONVERT({nativeType}, m.v)
                    FROM {physicalTable} t
                    JOIN OPENJSON(@json) WITH (id BIGINT '$.id', v NVARCHAR(MAX) '$.v') m ON t.Id = m.id;
                    """, new { json }, commandTimeout: 300);
            }
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync($"ALTER TABLE {physicalTable} DROP COLUMN {endCol};", transaction: tx);
            await conn.ExecuteAsync($"EXEC sp_rename '{physicalTable}.{newEndCol}', '{endCol}', 'COLUMN';", transaction: tx);
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }
    }

    private record FieldProcessResult(bool Fixed, int RowsDecrypted, string? Error);
}
