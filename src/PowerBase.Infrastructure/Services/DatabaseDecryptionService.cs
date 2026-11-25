using System.Text.Json;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// Decrypts a whole tenant database in place (see <see cref="IDatabaseDecryptionService"/>).
///
/// Per ciphertext column: add f_N_new of the native type, decrypt in batches of 5000 rows, then in ONE transaction
/// drop the old column (and its indexes), rename the new one, and clear AppField.IsEncrypted. A column is never
/// dropped unless every value in it was either decrypted or is plainly not ciphertext, so a wrong key can't destroy data.
/// </summary>
public class DatabaseDecryptionService : IDatabaseDecryptionService
{
    private const int BatchSize = 5000;
    private const int MinCipherBytes = 12 + 16; // nonce + GCM tag: anything shorter can't be ciphertext

    private readonly IQueryContext _queryContext;
    private readonly ITenantConnectionFactory _connectionFactory;
    private readonly IControlConnectionFactory _controlFactory;
    private readonly IEncryptionService _encryptionService;
    private readonly ISchemaEngineService _schemaEngine;
    private readonly ILogger<DatabaseDecryptionService> _logger;

    public DatabaseDecryptionService(
        IQueryContext queryContext,
        ITenantConnectionFactory connectionFactory,
        IControlConnectionFactory controlFactory,
        IEncryptionService encryptionService,
        ISchemaEngineService schemaEngine,
        ILogger<DatabaseDecryptionService> logger)
    {
        _queryContext = queryContext;
        _connectionFactory = connectionFactory;
        _controlFactory = controlFactory;
        _encryptionService = encryptionService;
        _schemaEngine = schemaEngine;
        _logger = logger;
    }

    private sealed class AppRow
    {
        public long Id { get; set; }
        public string Name { get; set; } = "";
        public bool IsEncrypted { get; set; }
        public string? SecurityOptions { get; set; }
    }

    private sealed class FieldRow
    {
        public long Id { get; set; }
        public int Fid { get; set; }
        public string Name { get; set; } = "";
        public string TypeCode { get; set; } = "";
        public long TableId { get; set; }
        public string TableName { get; set; } = "";
        public bool IsEncrypted { get; set; }
        public bool IsSystem { get; set; }
        public bool IsUnique { get; set; }
        public string? SqlDataType { get; set; }
    }

    public async Task<(long Id, string Name)?> FindTenantAsync(string database, CancellationToken ct = default)
    {
        await using var conn = _controlFactory.Create();
        await conn.OpenAsync(ct);
        var row = await conn.QueryFirstOrDefaultAsync<(long Id, string Name)?>(new CommandDefinition(
            "SELECT TOP 1 Id, Name FROM meta.Tenant WHERE DatabaseName = @database AND IsDeleted = 0",
            new { database }, cancellationToken: ct));
        return row;
    }

    public async Task RunAsync(DecryptDatabaseResult job, int tableParallelism, CancellationToken ct = default)
    {
        try
        {
            var tenant = await FindTenantAsync(job.Database, ct)
                ?? throw new InvalidOperationException($"No tenant uses database '{job.Database}'.");
            job.Update(j => { j.TenantId = tenant.Id; j.TenantName = tenant.Name; });
            _queryContext.SetTenantId(tenant.Id);

            _logger.LogInformation("[DecryptDatabase] START {Database} (tenant {TenantId}) dryRun={DryRun}", job.Database, tenant.Id, job.DryRun);

            await using var conn = await _connectionFactory.CreateAsync(ct);
            await conn.OpenAsync(ct);

            var apps = (await conn.QueryAsync<AppRow>(new CommandDefinition(
                "SELECT Id, Name, IsEncrypted, SecurityOptions FROM meta.App WHERE IsDeleted = 0 ORDER BY Id",
                cancellationToken: ct))).ToList();

            foreach (var app in apps)
            {
                ct.ThrowIfCancellationRequested();
                await ProcessAppAsync(conn, app, tenant.Id, job, Math.Max(1, tableParallelism), ct);
            }

            job.Update(j =>
            {
                j.CurrentTable = null;
                j.State = j.FieldsFailed > 0 || j.Errors.Count > 0 ? "CompletedWithErrors" : "Completed";
                j.FinishedUtc = DateTime.UtcNow;
            });
            _logger.LogInformation("[DecryptDatabase] DONE {Database}: state={State}", job.Database, job.State);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[DecryptDatabase] FAILED {Database}", job.Database);
            job.AddError(ex.Message);
            job.Update(j => { j.State = "Failed"; j.FinishedUtc = DateTime.UtcNow; });
        }
    }

    private async Task ProcessAppAsync(SqlConnection conn, AppRow app, long tenantId, DecryptDatabaseResult job, int parallelism, CancellationToken ct)
    {
        job.Update(j => j.AppsScanned++);

        var fields = (await conn.QueryAsync<FieldRow>(new CommandDefinition("""
            SELECT f.Id, f.Fid, f.Name, ft.Code AS TypeCode, f.AppTableId AS TableId, t.Name AS TableName,
                   f.IsEncrypted, f.IsSystem, f.IsUnique, ft.SqlDataType
            FROM meta.AppField f
            JOIN core.FieldType ft ON ft.Id = f.FieldTypeId
            JOIN meta.AppTable t ON t.Id = f.AppTableId
            WHERE t.AppId = @appId AND t.IsDeleted = 0 AND f.IsDeleted = 0 AND f.Fid IS NOT NULL
            """, new { appId = app.Id }, cancellationToken: ct))).ToList();

        // Same rule the read path uses (FieldEncryptionContext.StoresCiphertext).
        var targets = fields
            .Where(f => !f.IsSystem
                        && !PhysicalNaming.IsComputedTypeCode(f.TypeCode)
                        && !PhysicalNaming.IsEncryptionExemptTypeCode(f.TypeCode)
                        && (app.IsEncrypted || f.IsEncrypted))
            .ToList();

        if (targets.Count == 0 && !app.IsEncrypted) return;

        var wrappedDek = ReadWrappedDek(app.SecurityOptions);
        if (string.IsNullOrEmpty(wrappedDek))
        {
            // No DEK means nothing was ever encrypted (writes are plain when encryption isn't active).
            job.AddError($"App '{app.Name}': flagged encrypted but has no DEK — nothing to decrypt, flags left as they are.");
            return;
        }

        job.Update(j => j.FieldsFound += targets.Count);
        _logger.LogInformation("[DecryptDatabase] App '{App}' (Id={AppId}): {Fields} encrypted field(s) in {Tables} table(s)",
            app.Name, app.Id, targets.Count, targets.Select(t => t.TableId).Distinct().Count());

        var failedBefore = job.FieldsFailed;
        using var gate = new SemaphoreSlim(parallelism);
        var tasks = targets.GroupBy(t => t.TableId).Select(async group =>
        {
            await gate.WaitAsync(ct);
            try
            {
                await ProcessTableAsync(group.Key, group.First().TableName, group.ToList(), wrappedDek, tenantId, app.Id, job, ct);
            }
            finally
            {
                gate.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);

        // The app flag goes last, and only when every column converted: reads tolerate a plain value in an encrypted app,
        // so a half-done app keeps working, but an app flagged plain while ciphertext remains would show garbage.
        if (app.IsEncrypted && !job.DryRun && job.FieldsFailed == failedBefore)
        {
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE meta.App SET IsEncrypted = 0, ModifiedOn = SYSUTCDATETIME() WHERE Id = @id",
                new { id = app.Id }, cancellationToken: ct));
            job.Update(j => j.AppsDecrypted++);
            _logger.LogInformation("[DecryptDatabase] App '{App}' is now plain (IsEncrypted = 0).", app.Name);
        }
        else if (app.IsEncrypted && !job.DryRun)
        {
            job.AddError($"App '{app.Name}': some fields failed, so the app stays flagged encrypted. Fix them and run again.");
        }
    }

    private async Task ProcessTableAsync(
        long tableId, string tableName, List<FieldRow> fields, string wrappedDek,
        long tenantId, long appId, DecryptDatabaseResult job, CancellationToken ct)
    {
        job.Update(j => { j.TablesScanned++; j.CurrentTable = tableName; });
        await using var conn = await _connectionFactory.CreateAsync(ct);
        await conn.OpenAsync(ct);

        var physicalTable = PhysicalNaming.FullTableName(tableId);
        var columnTypes = (await conn.QueryAsync<(string Name, string Type, short MaxLength)>(new CommandDefinition("""
            SELECT c.name, ty.name, c.max_length
            FROM sys.columns c
            JOIN sys.types ty ON ty.user_type_id = c.user_type_id
            WHERE c.object_id = OBJECT_ID(@table)
            """, new { table = physicalTable }, cancellationToken: ct)))
            .ToDictionary(c => c.Name, c => (c.Type, c.MaxLength), StringComparer.OrdinalIgnoreCase);

        foreach (var field in fields)
        {
            ct.ThrowIfCancellationRequested();
            var column = PhysicalNaming.ColumnName(field.Fid);
            try
            {
                // Ciphertext lives in VARCHAR(MAX); a column of any other type was already converted (or never encrypted).
                if (!columnTypes.TryGetValue(column, out var type)
                    || !type.Type.Equals("varchar", StringComparison.OrdinalIgnoreCase) || type.MaxLength != -1)
                {
                    _logger.LogInformation("[DecryptDatabase]   {Table}.{Field} ({Column}) already plain — skipped.", tableName, field.Name, column);
                    if (!job.DryRun && columnTypes.ContainsKey(column) && field.IsEncrypted)
                        await conn.ExecuteAsync(new CommandDefinition(
                            "UPDATE meta.AppField SET IsEncrypted = 0, ModifiedOn = SYSUTCDATETIME() WHERE Id = @id",
                            new { id = field.Id }, cancellationToken: ct));
                    continue;
                }

                var rows = await ProcessFieldAsync(conn, tableId, tableName, field, wrappedDek, tenantId, appId, job, ct);
                job.Update(j => j.FieldsDecrypted++);
                _logger.LogInformation("[DecryptDatabase]   {Table}.{Field} ({Column}): {Rows} value(s) decrypted.", tableName, field.Name, column, rows);

                if (!job.DryRun && field.IsUnique)
                    await RebuildUniqueAsync(tableId, field, ct);
            }
            catch (Exception ex)
            {
                job.Update(j => j.FieldsFailed++);
                job.AddError($"{tableName}.{field.Name} ({column}): {ex.Message}");
                _logger.LogError(ex, "[DecryptDatabase]   {Table}.{Field} ({Column}) FAILED", tableName, field.Name, column);
                // Leave no half-built column behind; the old column was not touched.
                try
                {
                    if (!job.DryRun)
                        await conn.ExecuteAsync($"ALTER TABLE {physicalTable} DROP COLUMN IF EXISTS {column}_new;");
                }
                catch { /* best effort */ }
            }
        }
    }

    private async Task<long> ProcessFieldAsync(
        SqlConnection conn, long tableId, string tableName, FieldRow field, string wrappedDek,
        long tenantId, long appId, DecryptDatabaseResult job, CancellationToken ct)
    {
        var physicalTable = PhysicalNaming.FullTableName(tableId);
        var column = PhysicalNaming.ColumnName(field.Fid);
        var newColumn = $"{column}_new";
        var nativeType = string.IsNullOrWhiteSpace(field.SqlDataType) ? "NVARCHAR(MAX)" : field.SqlDataType!;

        if (job.DryRun)
            return await DryRunFieldAsync(conn, physicalTable, column, wrappedDek, tenantId, appId, ct);

        await conn.ExecuteAsync(new CommandDefinition($"ALTER TABLE {physicalTable} DROP COLUMN IF EXISTS {newColumn};", commandTimeout: 600, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition($"ALTER TABLE {physicalTable} ADD {newColumn} {nativeType} NULL;", commandTimeout: 600, cancellationToken: ct));

        long lastId = 0, decrypted = 0, alreadyPlain = 0;
        while (true)
        {
            // Soft-deleted rows are included on purpose: they can be restored, so their values must survive.
            var batch = (await conn.QueryAsync<(long Id, string? Value)>(new CommandDefinition($"""
                SELECT TOP ({BatchSize}) Id, {column}
                FROM {physicalTable}
                WHERE Id > @lastId AND {column} IS NOT NULL
                ORDER BY Id
                """, new { lastId }, commandTimeout: 600, cancellationToken: ct))).ToList();
            if (batch.Count == 0) break;
            lastId = batch[^1].Id;

            var plainByRowId = new Dictionary<long, string>(batch.Count);
            foreach (var (rowId, cipher) in batch)
            {
                if (string.IsNullOrEmpty(cipher)) { plainByRowId[rowId] = ""; continue; }
                try
                {
                    plainByRowId[rowId] = await _encryptionService.DecryptDataAsync(cipher, wrappedDek, tenantId, appId, ct);
                    decrypted++;
                }
                catch (Exception ex) when (LooksLikePlainText(cipher))
                {
                    _ = ex;
                    plainByRowId[rowId] = cipher;   // legacy plain value in a newly-encrypted app
                    alreadyPlain++;
                }
                // Any other failure (valid ciphertext that won't decrypt = wrong key / corrupted) propagates and aborts the
                // field BEFORE the old column is dropped.
            }

            var json = JsonSerializer.Serialize(plainByRowId.Select(p => new { id = p.Key, v = p.Value }));
            await conn.ExecuteAsync(new CommandDefinition($"""
                UPDATE t SET {newColumn} = TRY_CONVERT({nativeType}, m.v)
                FROM {physicalTable} t
                JOIN OPENJSON(@json) WITH (id BIGINT '$.id', v NVARCHAR(MAX) '$.v') m ON t.Id = m.id;
                """, new { json }, commandTimeout: 600, cancellationToken: ct));
        }

        await using var tx = await conn.BeginTransactionAsync(ct);
        try
        {
            await conn.ExecuteAsync(new CommandDefinition($"""
                DECLARE @sql NVARCHAR(MAX) = N'';
                SELECT @sql += N'DROP INDEX ' + QUOTENAME(i.name) + N' ON {physicalTable};'
                FROM sys.indexes i
                JOIN sys.index_columns ic ON ic.object_id = i.object_id AND ic.index_id = i.index_id
                JOIN sys.columns c ON c.object_id = ic.object_id AND c.column_id = ic.column_id
                WHERE i.object_id = OBJECT_ID('{physicalTable}') AND i.is_primary_key = 0
                  AND (c.name = '{column}' OR c.name = '{column}_unique_hash');
                IF @sql <> N'' EXEC sp_executesql @sql;
                """, transaction: tx, commandTimeout: 600, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition($"""
                IF COL_LENGTH('{physicalTable}', '{column}_unique_hash') IS NOT NULL
                    ALTER TABLE {physicalTable} DROP COLUMN {column}_unique_hash;
                """, transaction: tx, commandTimeout: 600, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition($"""
                DECLARE @sql NVARCHAR(MAX) = N'';
                SELECT @sql += N'ALTER TABLE {physicalTable} DROP CONSTRAINT ' + QUOTENAME(dc.name) + N';'
                FROM sys.default_constraints dc
                JOIN sys.columns c ON c.object_id = dc.parent_object_id AND c.column_id = dc.parent_column_id
                WHERE dc.parent_object_id = OBJECT_ID('{physicalTable}') AND c.name = '{column}';
                IF @sql <> N'' EXEC sp_executesql @sql;
                """, transaction: tx, commandTimeout: 600, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition($"ALTER TABLE {physicalTable} DROP COLUMN {column};", transaction: tx, commandTimeout: 600, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition($"EXEC sp_rename '{physicalTable}.{newColumn}', '{column}', 'COLUMN';", transaction: tx, cancellationToken: ct));
            await conn.ExecuteAsync(new CommandDefinition(
                "UPDATE meta.AppField SET IsEncrypted = 0, ModifiedOn = SYSUTCDATETIME() WHERE Id = @id",
                new { id = field.Id }, transaction: tx, cancellationToken: ct));
            await tx.CommitAsync(ct);
        }
        catch
        {
            await tx.RollbackAsync(ct);
            throw;
        }

        job.Update(j => { j.RowsDecrypted += decrypted; j.RowsAlreadyPlain += alreadyPlain; });
        return decrypted;
    }

    /// <summary>Dry run: count the values and try to decrypt a sample, changing nothing.</summary>
    private async Task<long> DryRunFieldAsync(SqlConnection conn, string physicalTable, string column, string wrappedDek, long tenantId, long appId, CancellationToken ct)
    {
        var count = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT_BIG(*) FROM {physicalTable} WHERE {column} IS NOT NULL", commandTimeout: 600, cancellationToken: ct));
        var sample = (await conn.QueryAsync<string>(new CommandDefinition(
            $"SELECT TOP (20) {column} FROM {physicalTable} WHERE {column} IS NOT NULL AND {column} <> '' ORDER BY Id", cancellationToken: ct))).ToList();
        foreach (var cipher in sample)
        {
            try { await _encryptionService.DecryptDataAsync(cipher, wrappedDek, tenantId, appId, ct); }
            catch when (LooksLikePlainText(cipher)) { }
        }
        return count;
    }

    private async Task RebuildUniqueAsync(long tableId, FieldRow field, CancellationToken ct)
    {
        try
        {
            await _schemaEngine.SetUniqueAsync(new AppTable { Id = tableId }, new AppField { Id = field.Id, Fid = field.Fid }, true, ct);
        }
        catch (Exception ex)
        {
            // Decrypted values can now collide (ciphertext was always unique); the data is fine, only the index is missing.
            _logger.LogWarning(ex, "[DecryptDatabase] Could not rebuild the unique index on f_{Fid}.", field.Fid);
        }
    }

    /// <summary>True when the value cannot be AES-GCM ciphertext from this app (not base64, or shorter than nonce + tag).</summary>
    private static bool LooksLikePlainText(string value)
    {
        Span<byte> buffer = new byte[(value.Length * 3 / 4) + 4];
        return !Convert.TryFromBase64String(value, buffer, out var written) || written < MinCipherBytes;
    }

    private static string? ReadWrappedDek(string? securityOptions)
    {
        if (string.IsNullOrWhiteSpace(securityOptions) || !securityOptions.TrimStart().StartsWith('{')) return null;
        try
        {
            return JsonSerializer.Deserialize<PowerBase.Domain.ValueObjects.AppSecurityOptionsSettings>(
                securityOptions, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })?.WrappedDek;
        }
        catch
        {
            return null;
        }
    }
}
