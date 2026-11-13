using System.Data.Common;
using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Fields.TypeConversion;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Services;

/// <summary>
/// Change Type's physical-column work (see <see cref="IFieldColumnConversionService"/>). Mirrors the schema engine's
/// RestoreColumnFromReferenceAsync pattern — park the old column, add a fresh one of the new SQL type, copy, drop —
/// generalised to any supported type pair, batched, and all-or-nothing in one transaction.
/// </summary>
public sealed class FieldColumnConversionService : IFieldColumnConversionService
{
    private const int BatchSize = 2000;
    private const int CommandTimeoutSeconds = 600;


    // TypeCode is derived from FieldTypeId (ft.Code join), so the type swap is a FieldTypeId update — same as
    // AppFieldRepository.UpdateFieldTypeAsync, plus the settings/default/unique values the new type needs.
    private const string UpdateFieldMetadataSql = """
        UPDATE meta.AppField
        SET FieldTypeId = @fieldTypeId, Settings = @settings, DefaultValue = @defaultValue, IsUnique = @isUnique, IsRequired = @isRequired,
            ModifiedOn = SYSUTCDATETIME(), ModifiedBy = @modifiedBy
        WHERE Id = @id AND IsDeleted = 0
        """;

    private readonly ITenantConnectionFactory _connectionFactory;
    private readonly IQueryContext _queryContext;
    private readonly IMetadataCache _metadataCache;
    private readonly IEncryptionService _encryptionService;

    public FieldColumnConversionService(
        ITenantConnectionFactory connectionFactory, IQueryContext queryContext, IMetadataCache metadataCache, IEncryptionService encryptionService)
    {
        _connectionFactory = connectionFactory;
        _queryContext = queryContext;
        _metadataCache = metadataCache;
        _encryptionService = encryptionService;
    }

    public async Task<ColumnConversionPreview> PreviewAsync(
        AppTable table, AppField field, Func<object?, ConvertedValue> convert, int sampleSize, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureColumnExistsAsync(connection, table, field, ct);
        var decrypt = await DecryptorAsync(connection, table, field, ct);

        var column = PhysicalNaming.GetPhysicalColumnName(field);
        int total = 0, nonEmpty = 0, lost = 0;
        var samples = new List<ColumnConversionSample>();

        await foreach (var batch in ReadBatchesAsync(connection, null, PhysicalNaming.FullTableName(table.Id), column, ct))
        {
            foreach (var (rowId, stored) in batch)
            {
                var before = decrypt is null ? stored : await decrypt(stored);
                total++;
                var after = convert(before);
                if (IsBlank(before)) continue;
                nonEmpty++;
                if (after.Lost) lost++;
                if (samples.Count < sampleSize) samples.Add(new ColumnConversionSample(rowId, before, after.Value));
            }
        }

        return new ColumnConversionPreview(total, nonEmpty, lost, samples);
    }

    public async Task<ColumnConversionResult> ConvertAsync(
        AppTable table, AppField field, FieldTypeMetadata metadata, Func<object?, ConvertedValue> convert, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureColumnExistsAsync(connection, table, field, ct);

        // Whether values are stored encrypted depends on the type (an encrypted app encrypts text-like types only), so the old
        // and the new type are decided separately: decrypt on read when the old type is encrypted, encrypt on write when the new one is.
        var readEncrypted = await EncryptionForAsync(connection, table, field, field.TypeCode, ct);
        var writeEncrypted = await EncryptionForAsync(connection, table, field, metadata.TargetTypeCode ?? field.TypeCode, ct);
        if (readEncrypted is not null && writeEncrypted is not null)
            return await ConvertEncryptedAsync(connection, readEncrypted, table, field, metadata, convert, ct);

        // Otherwise a new column: ciphertext text when the new type is encrypted, its own SQL type when not.
        var sqlDataType = writeEncrypted is not null ? "VARCHAR(MAX)" : metadata.SqlDataType;

        var physicalTable = PhysicalNaming.FullTableName(table.Id);
        var column = PhysicalNaming.GetPhysicalColumnName(field);
        var oldColumn = FieldColumnConversionSql.OldColumnName(field.Fid!.Value);
        var updateSql = writeEncrypted is not null
            ? FieldColumnConversionSql.EncryptedBatchUpdateSql(physicalTable, column)
            : FieldColumnConversionSql.BatchUpdateSql(physicalTable, column, sqlDataType);

        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            var dropped = await DropIndexesOnColumnAsync(connection, tx, physicalTable, field, ct);
            await ExecAsync(connection, tx, $"EXEC sp_rename '{physicalTable}.{column}', '{oldColumn}', 'COLUMN';", ct);
            await ExecAsync(connection, tx, $"ALTER TABLE {physicalTable} ADD {column} {sqlDataType} NULL;", ct);

            int scanned = 0, kept = 0, lost = 0;
            await foreach (var batch in ReadBatchesAsync(connection, tx, physicalTable, oldColumn, ct))
            {
                var converted = new List<KeyValuePair<long, object?>>(batch.Count);
                var encrypted = new List<KeyValuePair<long, string?>>(batch.Count);
                foreach (var (rowId, stored) in batch)
                {
                    scanned++;
                    var before = readEncrypted is not null && stored is string cipher ? await readEncrypted.DecryptValueAsync(cipher, ct) : stored;
                    var after = convert(before);
                    if (after.Value is not null) kept++;
                    else if (after.Lost) lost++;

                    if (writeEncrypted is null)
                    {
                        converted.Add(new(rowId, after.Value));
                        continue;
                    }
                    var plain = FieldColumnConversionSql.ToEncryptedPlainText(after.Value);
                    if (!string.IsNullOrEmpty(plain)) encrypted.Add(new(rowId, await writeEncrypted.EncryptValueAsync(field, plain, ct)));
                }
                if (writeEncrypted is null)
                    await WriteBatchAsync(connection, tx, updateSql, converted, ct);
                else if (encrypted.Count > 0)
                    await connection.ExecuteAsync(new CommandDefinition(updateSql,
                        new { json = FieldColumnConversionSql.EncryptedBatchJson(encrypted) }, tx, CommandTimeoutSeconds, cancellationToken: ct));
            }

            await ExecAsync(connection, tx, $"ALTER TABLE {physicalTable} DROP COLUMN {oldColumn};", ct);
            await UpdateMetadataAsync(connection, tx, field, metadata, ct);
            await tx.CommitAsync(ct);
            return new ColumnConversionResult(scanned, kept, lost, dropped);
        }
        catch (SqlException ex)
        {
            await tx.RollbackAsync(ct);
            throw ConversionFailed(physicalTable, column, ex);
        }
    }

    public async Task<ColumnConversionResult> CreateFromValuesAsync(
        AppTable table, AppField field, FieldTypeMetadata metadata, IReadOnlyDictionary<long, object?> valuesByRowId, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var physicalTable = PhysicalNaming.FullTableName(table.Id);
        var column = PhysicalNaming.GetPhysicalColumnName(field);
        if (await ColumnExistsAsync(connection, null, physicalTable, column, ct))
            throw new InvalidOperationException($"{physicalTable}.{column} already exists; a formula field should have no column.");

        // A formula has no stored values; what decides encryption is the NEW type (an encrypted app encrypts text-like types only).
        if (await EncryptionForAsync(connection, table, field, metadata.TargetTypeCode ?? field.TypeCode, ct) is { } encryption)
            return await CreateEncryptedFromValuesAsync(connection, encryption, table, field, metadata, valuesByRowId, ct);

        var sqlDataType = metadata.SqlDataType;
        var updateSql = FieldColumnConversionSql.BatchUpdateSql(physicalTable, column, sqlDataType);
        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            await ExecAsync(connection, tx, $"ALTER TABLE {physicalTable} ADD {column} {sqlDataType} NULL;", ct);
            foreach (var chunk in valuesByRowId.Where(p => p.Value is not null).Chunk(BatchSize))
                await WriteBatchAsync(connection, tx, updateSql, chunk, ct);
            await UpdateMetadataAsync(connection, tx, field, metadata, ct);
            await tx.CommitAsync(ct);
        }
        catch (SqlException ex)
        {
            await tx.RollbackAsync(ct);
            throw ConversionFailed(physicalTable, column, ex);
        }

        var kept = valuesByRowId.Count(p => p.Value is not null);
        return new ColumnConversionResult(valuesByRowId.Count, kept, 0, []);
    }

    public async Task<ColumnConversionResult> DropAsync(AppTable table, AppField field, FieldTypeMetadata metadata, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        var physicalTable = PhysicalNaming.FullTableName(table.Id);
        var column = PhysicalNaming.GetPhysicalColumnName(field);

        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            var dropped = await DropIndexesOnColumnAsync(connection, tx, physicalTable, field, ct);
            await ExecAsync(connection, tx, $"""
                IF COL_LENGTH('{physicalTable}', '{column}') IS NOT NULL
                    ALTER TABLE {physicalTable} DROP COLUMN {column};
                """, ct);
            await UpdateMetadataAsync(connection, tx, field, metadata, ct);
            await tx.CommitAsync(ct);
            return new ColumnConversionResult(0, 0, 0, dropped);
        }
        catch (SqlException ex)
        {
            await tx.RollbackAsync(ct);
            throw ConversionFailed(physicalTable, column, ex);
        }
    }

    public async Task ApplyMetadataAsync(AppField field, FieldTypeMetadata metadata, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await using var tx = await connection.BeginTransactionAsync(ct);
        await UpdateMetadataAsync(connection, tx, field, metadata, ct);
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<object>> ReadDistinctValuesAsync(AppTable table, AppField field, int max, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await EnsureColumnExistsAsync(connection, table, field, ct);
        var column = PhysicalNaming.GetPhysicalColumnName(field);

        // Encrypted values differ even when the plain values are equal, so DISTINCT has to happen after decrypting.
        if (await DecryptorAsync(connection, table, field, ct) is { } decrypt)
        {
            var distinct = new HashSet<string>(StringComparer.Ordinal);
            await foreach (var batch in ReadBatchesAsync(connection, null, PhysicalNaming.FullTableName(table.Id), column, ct))
            {
                foreach (var (_, stored) in batch)
                {
                    if (await decrypt(stored) is string plain && !string.IsNullOrWhiteSpace(plain)) distinct.Add(plain);
                    if (distinct.Count >= max) return distinct.Cast<object>().ToList();
                }
            }
            return distinct.Cast<object>().ToList();
        }

        // As text: only User ↔ Text/Email reads this, and both store NVARCHAR. (QueryAsync<object> would hand back
        // whole Dapper rows, not the values.)
        var values = await connection.QueryAsync<string>(new CommandDefinition(
            $"SELECT DISTINCT TOP (@max) CONVERT(NVARCHAR(MAX), {column}) FROM {PhysicalNaming.FullTableName(table.Id)} WHERE {column} IS NOT NULL;",
            new { max }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return values.Cast<object>().ToList();
    }

    private async Task UpdateMetadataAsync(DbConnection connection, DbTransaction tx, AppField field, FieldTypeMetadata metadata, CancellationToken ct)
    {
        _metadataCache.Invalidate(_queryContext.TenantId, MetadataCacheGroups.Fields, inTransaction: true); // the field lists are cached (AppFieldRepository)
        _metadataCache.Invalidate(_queryContext.TenantId, MetadataCacheGroups.Access, inTransaction: true); // field-access maps join the field table
        var affected = await connection.ExecuteAsync(new CommandDefinition(UpdateFieldMetadataSql, new
        {
            id = field.Id,
            fieldTypeId = metadata.FieldTypeId,
            settings = metadata.Settings,
            defaultValue = metadata.DefaultValue,
            isUnique = metadata.IsUnique,
            isRequired = metadata.IsRequired,
            modifiedBy = _queryContext.UserId,
        }, tx, CommandTimeoutSeconds, cancellationToken: ct));
        if (affected == 0) throw new NotFoundException("Field", field.PublicId);
    }

    // ── helpers ──────────────────────────────────────────────────────────────

    private async Task<DbConnection> OpenAsync(CancellationToken ct)
    {
        var connection = await _connectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        return connection;
    }


    private static async Task EnsureColumnExistsAsync(DbConnection connection, AppTable table, AppField field, CancellationToken ct)
    {
        var physicalTable = PhysicalNaming.FullTableName(table.Id);
        var column = PhysicalNaming.GetPhysicalColumnName(field);
        if (!await ColumnExistsAsync(connection, null, physicalTable, column, ct))
            throw new InvalidOperationException($"Physical column {physicalTable}.{column} does not exist.");
    }

    /// <summary>
    /// Null when the field's column holds plain values. Otherwise a function that turns one stored value into its plain
    /// text, using the app's own key exactly as record reads do (FieldEncryptionContext). Same rule as everywhere else
    /// for which columns hold ciphertext (FieldEncryptionContext.StoresCiphertext). Tenants on SQL Always Encrypted
    /// (secure enclaves) are not supported yet and are refused with a clear message.
    /// </summary>
    private async Task<Func<object?, Task<object?>>?> DecryptorAsync(DbConnection connection, AppTable table, AppField field, CancellationToken ct)
    {
        // Reads never refuse: on an Always Encrypted tenant the change is stopped up front (GetEncryptionBlockerAsync).
        var encryption = await EncryptionForAsync(connection, table, field, field.TypeCode, ct, forWrite: false);
        if (encryption is null) return null;
        return async stored => stored is string text ? await encryption.DecryptValueAsync(text, ct) : stored;
    }

    public const string AlwaysEncryptedBlocker =
        "This workspace uses SQL Always Encrypted, so the type of an encrypted field can't be changed here yet.";

    public async Task<string?> GetEncryptionBlockerAsync(AppTable table, AppField field, string targetTypeCode, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        if (!UsesAlwaysEncrypted(connection)) return null;
        var encryption = await FieldEncryptionContext.ResolveAsync(connection, table.AppId, _queryContext.TenantId, _encryptionService, ct: ct);
        return StoresCiphertext(encryption, field, field.TypeCode) || StoresCiphertext(encryption, field, targetTypeCode)
            ? AlwaysEncryptedBlocker
            : null;
    }

    /// <summary>The app's encryption context when <paramref name="field"/>, as a field of type <paramref name="typeCode"/>, stores
    /// ciphertext — the same rule record reads and saves use (FieldEncryptionContext.StoresCiphertext: encrypted app or the field's
    /// own Encrypted setting, and a type that isn't exempt, PhysicalNaming.IsEncryptionExemptTypeCode) — otherwise null.
    /// Always Encrypted tenants use a different scheme: writes are refused outright (safety net behind the preview blocker),
    /// reads return null so nothing is decrypted with the app key.</summary>
    private async Task<FieldEncryptionContext?> EncryptionForAsync(
        DbConnection connection, AppTable table, AppField field, string typeCode, CancellationToken ct, bool forWrite = true)
    {
        var encryption = await FieldEncryptionContext.ResolveAsync(connection, table.AppId, _queryContext.TenantId, _encryptionService, ct: ct);
        if (!StoresCiphertext(encryption, field, typeCode)) return null;

        if (UsesAlwaysEncrypted(connection))
        {
            if (!forWrite) return null;
            throw new ValidationException(new Dictionary<string, string[]> { ["targetTypeCode"] = [AlwaysEncryptedBlocker] });
        }
        return encryption;
    }

    private static bool StoresCiphertext(FieldEncryptionContext encryption, AppField field, string typeCode) =>
        FieldEncryptionContext.StoresCiphertext(
            new AppField { IsSystem = field.IsSystem, IsEncrypted = field.IsEncrypted, TypeCode = typeCode }, encryption.IsAppEncrypted);

    private static bool UsesAlwaysEncrypted(DbConnection connection) =>
        new SqlConnectionStringBuilder(connection.ConnectionString).ColumnEncryptionSetting == SqlConnectionColumnEncryptionSetting.Enabled;

    /// <summary>
    /// Formula → stored on an encrypted field: the new column is ciphertext text (VARCHAR(MAX)), exactly what the schema
    /// engine creates for an encrypted field, and each evaluated value is stored as the plain text a record save would
    /// write, encrypted with the app's key. One transaction with the field's new type.
    /// </summary>
    private async Task<ColumnConversionResult> CreateEncryptedFromValuesAsync(
        DbConnection connection, FieldEncryptionContext encryption, AppTable table, AppField field, FieldTypeMetadata metadata,
        IReadOnlyDictionary<long, object?> valuesByRowId, CancellationToken ct)
    {
        var physicalTable = PhysicalNaming.FullTableName(table.Id);
        var column = PhysicalNaming.GetPhysicalColumnName(field);
        var updateSql = FieldColumnConversionSql.EncryptedBatchUpdateSql(physicalTable, column);

        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            await ExecAsync(connection, tx, $"ALTER TABLE {physicalTable} ADD {column} VARCHAR(MAX) NULL;", ct);
            var kept = 0;
            foreach (var chunk in valuesByRowId.Where(p => p.Value is not null).Chunk(BatchSize))
            {
                var encrypted = new List<KeyValuePair<long, string?>>(chunk.Length);
                foreach (var (rowId, value) in chunk)
                {
                    var plain = FieldColumnConversionSql.ToEncryptedPlainText(value);
                    if (string.IsNullOrEmpty(plain)) continue;
                    encrypted.Add(new(rowId, await encryption.EncryptValueAsync(field, plain, ct)));
                    kept++;
                }
                await connection.ExecuteAsync(new CommandDefinition(updateSql,
                    new { json = FieldColumnConversionSql.EncryptedBatchJson(encrypted) }, tx, CommandTimeoutSeconds, cancellationToken: ct));
            }
            await UpdateMetadataAsync(connection, tx, field, metadata, ct);
            await tx.CommitAsync(ct);
            return new ColumnConversionResult(valuesByRowId.Count, kept, 0, []);
        }
        catch (SqlException ex)
        {
            await tx.RollbackAsync(ct);
            throw ConversionFailed(physicalTable, column, ex);
        }
    }

    /// <summary>
    /// Encrypted column: it is ciphertext text (VARCHAR) whatever the field's type, so the column itself never changes.
    /// Each value is decrypted, converted, written as the plain text a record save would store, re-encrypted with the
    /// app's key and put back on the same row; values that can't be converted are cleared. One transaction, together
    /// with the field's new type, so data and type always agree.
    /// </summary>
    private async Task<ColumnConversionResult> ConvertEncryptedAsync(
        DbConnection connection, FieldEncryptionContext encryption, AppTable table, AppField field, FieldTypeMetadata metadata,
        Func<object?, ConvertedValue> convert, CancellationToken ct)
    {
        var physicalTable = PhysicalNaming.FullTableName(table.Id);
        var column = PhysicalNaming.GetPhysicalColumnName(field);
        var updateSql = FieldColumnConversionSql.EncryptedBatchUpdateSql(physicalTable, column);

        await using var tx = await connection.BeginTransactionAsync(ct);
        try
        {
            int scanned = 0, kept = 0, lost = 0;
            await foreach (var batch in ReadBatchesAsync(connection, tx, physicalTable, column, ct))
            {
                var encrypted = new List<KeyValuePair<long, string?>>(batch.Count);
                foreach (var (rowId, stored) in batch)
                {
                    scanned++;
                    var before = stored is string cipher ? await encryption.DecryptValueAsync(cipher, ct) : stored;
                    var after = convert(before);
                    if (after.Value is not null) kept++;
                    else if (after.Lost) lost++;

                    var plain = FieldColumnConversionSql.ToEncryptedPlainText(after.Value);
                    encrypted.Add(new(rowId, string.IsNullOrEmpty(plain) ? null : await encryption.EncryptValueAsync(field, plain, ct)));
                }
                await connection.ExecuteAsync(new CommandDefinition(updateSql,
                    new { json = FieldColumnConversionSql.EncryptedBatchJson(encrypted) }, tx, CommandTimeoutSeconds, cancellationToken: ct));
            }

            // Unique turned off by the change: drop its index too (on ciphertext it can't reject anything, but it shouldn't linger).
            var dropped = field.IsUnique && !metadata.IsUnique
                ? await DropIndexesOnColumnAsync(connection, tx, physicalTable, field, ct)
                : [];
            await UpdateMetadataAsync(connection, tx, field, metadata, ct);
            await tx.CommitAsync(ct);
            return new ColumnConversionResult(scanned, kept, lost, dropped);
        }
        catch (SqlException ex)
        {
            await tx.RollbackAsync(ct);
            throw ConversionFailed(physicalTable, column, ex);
        }
    }

    private static async Task<bool> ColumnExistsAsync(DbConnection connection, DbTransaction? tx, string physicalTable, string column, CancellationToken ct) =>
        await connection.ExecuteScalarAsync<int?>(new CommandDefinition(
            $"SELECT COL_LENGTH('{physicalTable}', '{column}')", transaction: tx, cancellationToken: ct)) is not null;

    private static async Task<IReadOnlyList<string>> DropIndexesOnColumnAsync(
        DbConnection connection, DbTransaction tx, string physicalTable, AppField field, CancellationToken ct)
    {
        var column = PhysicalNaming.GetPhysicalColumnName(field);
        var digestColumn = FieldColumnConversionSql.UniqueDigestColumnName(field.Fid!.Value);
        var indexes = (await connection.QueryAsync<string>(new CommandDefinition(
            FieldColumnConversionSql.IndexesOnColumnSql, new { table = physicalTable, column, digestColumn }, tx, cancellationToken: ct))).ToList();

        foreach (var index in indexes)
            await ExecAsync(connection, tx, $"DROP INDEX [{index.Replace("]", "]]")}] ON {physicalTable};", ct);
        await ExecAsync(connection, tx, $"ALTER TABLE {physicalTable} DROP COLUMN IF EXISTS {digestColumn};", ct);
        return indexes;
    }

    /// <summary>Keyset-paged read of (row Id, value), every row including soft-deleted ones.</summary>
    private static async IAsyncEnumerable<List<(long RowId, object? Value)>> ReadBatchesAsync(
        DbConnection connection, DbTransaction? tx, string physicalTable, string column,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var sql = $"SELECT TOP (@batchSize) Id, {column} FROM {physicalTable} WHERE Id > @lastId ORDER BY Id;";
        long lastId = 0;
        while (true)
        {
            var batch = new List<(long, object?)>(BatchSize);
            await using (var cmd = connection.CreateCommand())
            {
                cmd.CommandText = sql;
                cmd.Transaction = tx;
                cmd.CommandTimeout = CommandTimeoutSeconds;
                AddParameter(cmd, "@batchSize", BatchSize);
                AddParameter(cmd, "@lastId", lastId);
                await using var reader = await cmd.ExecuteReaderAsync(ct);
                while (await reader.ReadAsync(ct))
                {
                    var id = reader.GetInt64(0);
                    batch.Add((id, reader.IsDBNull(1) ? null : reader.GetValue(1)));
                    lastId = id;
                }
            }
            if (batch.Count == 0) yield break;
            yield return batch;
            if (batch.Count < BatchSize) yield break;
        }
    }

    private static void AddParameter(DbCommand cmd, string name, object value)
    {
        var p = cmd.CreateParameter();
        p.ParameterName = name;
        p.Value = value;
        cmd.Parameters.Add(p);
    }

    private static Task WriteBatchAsync(
        DbConnection connection, DbTransaction tx, string updateSql, IEnumerable<KeyValuePair<long, object?>> values, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(updateSql, new { json = FieldColumnConversionSql.BatchJson(values) }, tx,
            CommandTimeoutSeconds, cancellationToken: ct));

    private static Task ExecAsync(DbConnection connection, DbTransaction tx, string sql, CancellationToken ct) =>
        connection.ExecuteAsync(new CommandDefinition(sql, transaction: tx, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));

    private static bool IsBlank(object? value) => value is null || value is string s && string.IsNullOrWhiteSpace(s);

    private static ValidationException ConversionFailed(string physicalTable, string column, SqlException ex)
    {
        Console.WriteLine($"[FieldColumnConversion] {physicalTable}.{column} not converted, rolled back: {ex.Message}");
        return new ValidationException(new Dictionary<string, string[]>
        {
            ["targetTypeCode"] = ["The field's data couldn't be converted, so nothing was changed. Please try again or contact support."],
        });
    }
}
