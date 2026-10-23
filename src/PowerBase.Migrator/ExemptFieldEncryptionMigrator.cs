using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Configuration;
using PowerBase.Domain.Constants;
using PowerBase.Infrastructure.Services;

namespace PowerBase.Migrator;

public static class ExemptFieldEncryptionMigrator
{
    private const int BatchSize = 5000;

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

    public record TenantInfo(long Id, string Name, string? DatabaseName, string? ServerRef, string ProvisioningState);

    public class AffectedFieldInfo
    {
        public long FieldId       { get; set; }
        public int Fid            { get; set; }
        public string FieldName   { get; set; } = "";
        public string TypeCode    { get; set; } = "";
        public long TableId       { get; set; }
        public string TableName   { get; set; } = "";
        public long AppId         { get; set; }
        public string AppName     { get; set; } = "";
        public bool IsUnique      { get; set; }
        public int NonNullRows    { get; set; }
    }

    private static AesEncryptionService CreateEncryptionService(string masterKeyBase64)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Encryption:IsEncryptionLocal"] = "true",
                ["Encryption:MasterKey"] = masterKeyBase64
            })
            .Build();
        return new AesEncryptionService(config);
    }

    public static async Task<List<TenantInfo>> GetTenantsAsync(string controlConnectionString)
    {
        await using var conn = new SqlConnection(controlConnectionString);
        await conn.OpenAsync();

        var rows = await conn.QueryAsync<TenantInfo>("""
            SELECT Id, Name, DatabaseName, ServerRef, ProvisioningState
            FROM meta.Tenant
            WHERE IsDeleted = 0
            ORDER BY Id
            """);

        return rows.ToList();
    }

    public static async Task<List<AffectedFieldInfo>> ScanTenantAsync(
        string tenantConnectionString,
        long tenantId)
    {
        var results = new List<AffectedFieldInfo>();
        await using var conn = new SqlConnection(tenantConnectionString);
        await conn.OpenAsync();

        var sql = """
            SELECT 
                f.Id AS FieldId,
                f.Fid,
                f.Name AS FieldName,
                ft.Code AS TypeCode,
                t.Id AS TableId,
                t.Name AS TableName,
                a.Id AS AppId,
                a.Name AS AppName,
                f.IsUnique
            FROM meta.AppField f
            JOIN core.FieldType ft ON ft.Id = f.FieldTypeId
            JOIN meta.AppTable t ON t.Id = f.AppTableId
            JOIN meta.App a ON a.Id = t.AppId
            WHERE f.IsDeleted = 0
              AND t.IsDeleted = 0
              AND a.IsDeleted = 0
              AND f.IsEncrypted = 1
              AND f.Fid IS NOT NULL
            """;

        var fields = (await conn.QueryAsync<AffectedFieldInfo>(sql)).ToList();

        foreach (var field in fields)
        {
            if (PhysicalNaming.IsComputedTypeCode(field.TypeCode)) continue;
            if (!PhysicalNaming.IsEncryptionExemptTypeCode(field.TypeCode)) continue;
            if (!NativeSqlType.ContainsKey(field.TypeCode)) continue;

            var physicalTable = PhysicalNaming.FullTableName(field.TableId);
            var physicalCol   = PhysicalNaming.ColumnName(field.Fid);

            int count = 0;
            try
            {
                count = await conn.ExecuteScalarAsync<int>(
                    $"SELECT COUNT(*) FROM {physicalTable} WHERE IsDeleted = 0 AND {physicalCol} IS NOT NULL");
            }
            catch
            {
                // Table might not exist yet
            }

            field.NonNullRows = count;
            results.Add(field);
        }

        return results;
    }

    public static async Task<bool> RunMigrationForTenantAsync(
        string tenantConnectionString,
        long tenantId,
        string tenantLabel,
        string masterKeyBase64,
        bool dryRun = false,
        bool singleTableOnly = false)
    {
        Console.WriteLine($"\n=======================================================");
        Console.WriteLine($"Tenant: {tenantLabel} (Id={tenantId})");
        Console.WriteLine($"=======================================================");

        var encService = CreateEncryptionService(masterKeyBase64);
        var affectedFields = await ScanTenantAsync(tenantConnectionString, tenantId);

        if (affectedFields.Count == 0)
        {
            Console.WriteLine("  ✓ No exempt-but-encrypted fields found in this tenant.");
            return false;
        }

        Console.WriteLine($"  Found {affectedFields.Count} exempt-but-encrypted field(s):");
        foreach (var f in affectedFields)
        {
            Console.WriteLine($"    • [{f.AppName} -> {f.TableName}] {f.FieldName} (Fid: {f.Fid}, Type: {f.TypeCode}) — Rows: {f.NonNullRows}");
        }

        if (dryRun)
        {
            Console.WriteLine("\n  [DRY RUN] Validating encryption key & sample decryptions...");
            await using var verifyConn = new SqlConnection(tenantConnectionString);
            await verifyConn.OpenAsync();

            int sampleCount = 0;
            foreach (var f in affectedFields.Where(x => x.NonNullRows > 0))
            {
                if (sampleCount >= 5) break;

                var encCtx = await FieldEncryptionContext.ResolveAsync(
                    verifyConn, f.AppId, tenantId, encService, null, CancellationToken.None);

                var sample = await verifyConn.QueryFirstOrDefaultAsync<string>(
                    $"SELECT TOP 1 {PhysicalNaming.ColumnName(f.Fid)} FROM {PhysicalNaming.FullTableName(f.TableId)} WHERE IsDeleted = 0 AND {PhysicalNaming.ColumnName(f.Fid)} IS NOT NULL");

                if (!string.IsNullOrEmpty(sample))
                {
                    sampleCount++;
                    try
                    {
                        var decrypted = await encService.DecryptDataAsync(sample, encCtx.WrappedDek!, tenantId, f.AppId);
                        Console.ForegroundColor = ConsoleColor.Green;
                        Console.WriteLine($"    ✓ Sample [{f.TableName}.{f.FieldName} ({f.TypeCode})]:");
                        Console.WriteLine($"      Cipher: {(sample.Length > 24 ? sample[..24] + "..." : sample)}");
                        Console.WriteLine($"      Decrypted Plaintext: '{decrypted}'");
                        Console.ResetColor();
                    }
                    catch (Exception ex)
                    {
                        Console.ForegroundColor = ConsoleColor.Red;
                        Console.WriteLine($"    ✗ Sample Decrypt FAILED for [{f.TableName}.{f.FieldName}]: {ex.Message}");
                        Console.ResetColor();
                    }
                }
            }

            Console.WriteLine("\n  [DRY RUN] Done. No database columns were altered.");
            return false;
        }

        // Filter to single table if requested
        var fieldsToMigrate = affectedFields;
        if (singleTableOnly)
        {
            var firstTableId = affectedFields.First().TableId;
            fieldsToMigrate = affectedFields.Where(f => f.TableId == firstTableId).ToList();
            Console.ForegroundColor = ConsoleColor.Cyan;
            Console.WriteLine($"\n  >>> SINGLE TABLE MODE: Migrating Table '{fieldsToMigrate.First().TableName}' (Id={firstTableId}) with {fieldsToMigrate.Count} exempt field(s) <<<");
            Console.ResetColor();
        }

        await using var conn = new SqlConnection(tenantConnectionString);
        await conn.OpenAsync();

        foreach (var f in fieldsToMigrate)
        {
            var nativeType    = NativeSqlType[f.TypeCode];
            var physicalTable = PhysicalNaming.FullTableName(f.TableId);
            var tableName     = PhysicalNaming.TableName(f.TableId);
            var physicalCol   = PhysicalNaming.ColumnName(f.Fid);
            var newCol        = $"{physicalCol}_new";

            Console.WriteLine($"\n  Migrating: {f.FieldName} (Table {tableName}, {f.TypeCode} -> {nativeType}, {f.NonNullRows} rows)...");

            var encCtx = await FieldEncryptionContext.ResolveAsync(
                conn, f.AppId, tenantId, encService, null, CancellationToken.None);

            // 1. Add new column with native type
            await conn.ExecuteAsync($"ALTER TABLE {physicalTable} DROP COLUMN IF EXISTS {newCol};");
            await conn.ExecuteAsync($"ALTER TABLE {physicalTable} ADD {newCol} {nativeType} NULL;");

            // 2. Batch decrypt & populate new column
            long lastId = 0;
            int totalProcessed = 0;
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

                var plainByRowId = new Dictionary<long, string>();
                foreach (var (rowId, cipher) in batch)
                {
                    if (string.IsNullOrEmpty(cipher)) continue;
                    try
                    {
                        if (encCtx.IsActive && !string.IsNullOrEmpty(encCtx.WrappedDek))
                        {
                            var plain = await encService.DecryptDataAsync(
                                cipher, encCtx.WrappedDek, tenantId, f.AppId);
                            if (!string.IsNullOrEmpty(plain))
                            {
                                plainByRowId[rowId] = NormalizePlaintextForSql(plain, f.TypeCode);
                                continue;
                            }
                        }
                        plainByRowId[rowId] = NormalizePlaintextForSql(cipher, f.TypeCode);
                    }
                    catch
                    {
                        plainByRowId[rowId] = NormalizePlaintextForSql(cipher, f.TypeCode);
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

                totalProcessed += batch.Count;
                if (f.NonNullRows > 5000 && (totalProcessed % 25000 == 0 || totalProcessed >= f.NonNullRows))
                {
                    Console.WriteLine($"    Progress: {totalProcessed} / {f.NonNullRows} rows...");
                }
            }

            // 3. Drop old column and rename new column in one transaction
            await using var tx = await conn.BeginTransactionAsync();
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
                if (f.IsUnique)
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
                    """, new { fieldId = f.FieldId }, transaction: tx);

                await tx.CommitAsync();
            }
            catch (Exception ex)
            {
                await tx.RollbackAsync();
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"    FAILED to finalize column {physicalCol}: {ex.Message}");
                Console.ResetColor();
                throw;
            }

            // 4. Handle range end column (e.g. DateRange / NumericRange)
            if (PhysicalNaming.IsRangeTypeCode(f.TypeCode))
            {
                await CleanupRangeEndColumnAsync(
                    conn, physicalTable, tableName, f.Fid, nativeType, encCtx, tenantId, f.AppId, encService);
            }

            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine($"    ✓ Successfully migrated {physicalCol} ({f.FieldName}) -> {nativeType}. IsEncrypted reset to 0.");
            Console.ResetColor();
        }
        return true;
    }

    private static async Task CleanupRangeEndColumnAsync(
        SqlConnection conn,
        string physicalTable,
        string tableName,
        int fid,
        string nativeType,
        FieldEncryptionContext encCtx,
        long tenantId,
        long appId,
        AesEncryptionService encService)
    {
        var endCol    = PhysicalNaming.EndColumnName(fid);
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
                        var plain = await encService.DecryptDataAsync(
                            cipher, encCtx.WrappedDek, tenantId, appId);
                        if (!string.IsNullOrEmpty(plain))
                        {
                            plainByRowId[rowId] = NormalizePlaintextForSql(plain, "Date");
                            continue;
                        }
                    }
                    plainByRowId[rowId] = NormalizePlaintextForSql(cipher, "Date");
                }
                catch
                {
                    plainByRowId[rowId] = NormalizePlaintextForSql(cipher, "Date");
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

        await using var tx = await conn.BeginTransactionAsync();
        try
        {
            await conn.ExecuteAsync($"ALTER TABLE {physicalTable} DROP COLUMN {endCol};", transaction: tx);
            await conn.ExecuteAsync($"EXEC sp_rename '{physicalTable}.{newEndCol}', '{endCol}', 'COLUMN';", transaction: tx);
            await tx.CommitAsync();
        }
        catch
        {
            await tx.RollbackAsync();
            throw;
        }
    }
}
