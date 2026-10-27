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

    private const string AppIsEncryptedSql = "SELECT IsEncrypted FROM meta.App WHERE Id = @appId AND IsDeleted = 0";

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

    public FieldColumnConversionService(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
    {
        _connectionFactory = connectionFactory;
        _queryContext = queryContext;
    }

    public async Task<ColumnConversionPreview> PreviewAsync(
        AppTable table, AppField field, Func<object?, ConvertedValue> convert, int sampleSize, CancellationToken ct = default)
    {
        await using var connection = await OpenAsync(ct);
        await EnsurePlainColumnAsync(connection, table, field, ct);

        var column = PhysicalNaming.GetPhysicalColumnName(field);
        int total = 0, nonEmpty = 0, lost = 0;
        var samples = new List<ColumnConversionSample>();

        await foreach (var batch in ReadBatchesAsync(connection, null, PhysicalNaming.FullTableName(table.Id), column, ct))
        {
            foreach (var (rowId, before) in batch)
            {
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
        await EnsurePlainColumnAsync(connection, table, field, ct);
        var sqlDataType = metadata.SqlDataType;

        var physicalTable = PhysicalNaming.FullTableName(table.Id);
        var column = PhysicalNaming.GetPhysicalColumnName(field);
        var oldColumn = FieldColumnConversionSql.OldColumnName(field.Fid!.Value);
        var updateSql = FieldColumnConversionSql.BatchUpdateSql(physicalTable, column, sqlDataType);

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
                foreach (var (rowId, before) in batch)
                {
                    scanned++;
                    var after = convert(before);
                    if (after.Value is not null) kept++;
                    else if (after.Lost) lost++;
                    converted.Add(new(rowId, after.Value));
                }
                await WriteBatchAsync(connection, tx, updateSql, converted, ct);
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
        await EnsureNotEncryptedAsync(connection, table, field, ct);
        var sqlDataType = metadata.SqlDataType;

        var physicalTable = PhysicalNaming.FullTableName(table.Id);
        var column = PhysicalNaming.GetPhysicalColumnName(field);
        if (await ColumnExistsAsync(connection, null, physicalTable, column, ct))
            throw new InvalidOperationException($"{physicalTable}.{column} already exists; a formula field should have no column.");

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
        await EnsurePlainColumnAsync(connection, table, field, ct);
        var column = PhysicalNaming.GetPhysicalColumnName(field);
        // As text: only User ↔ Text/Email reads this, and both store NVARCHAR. (QueryAsync<object> would hand back
        // whole Dapper rows, not the values.)
        var values = await connection.QueryAsync<string>(new CommandDefinition(
            $"SELECT DISTINCT TOP (@max) CONVERT(NVARCHAR(MAX), {column}) FROM {PhysicalNaming.FullTableName(table.Id)} WHERE {column} IS NOT NULL;",
            new { max }, commandTimeout: CommandTimeoutSeconds, cancellationToken: ct));
        return values.Cast<object>().ToList();
    }

    private async Task UpdateMetadataAsync(DbConnection connection, DbTransaction tx, AppField field, FieldTypeMetadata metadata, CancellationToken ct)
    {
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

    /// <summary>The column must exist and hold plain values — ciphertext (encrypted app/field) can't be converted in
    /// SQL; the same restriction the Percent Display-As switch has ("turn encryption off first").</summary>
    private static async Task EnsurePlainColumnAsync(DbConnection connection, AppTable table, AppField field, CancellationToken ct)
    {
        await EnsureNotEncryptedAsync(connection, table, field, ct);
        var physicalTable = PhysicalNaming.FullTableName(table.Id);
        var column = PhysicalNaming.GetPhysicalColumnName(field);
        if (!await ColumnExistsAsync(connection, null, physicalTable, column, ct))
            throw new InvalidOperationException($"Physical column {physicalTable}.{column} does not exist.");
    }

    private static async Task EnsureNotEncryptedAsync(DbConnection connection, AppTable table, AppField field, CancellationToken ct)
    {
        var appIsEncrypted = await connection.ExecuteScalarAsync<bool>(
            new CommandDefinition(AppIsEncryptedSql, new { appId = table.AppId }, cancellationToken: ct));
        if (appIsEncrypted || field.IsEncrypted)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["targetTypeCode"] = ["An encrypted field can't change type. Turn encryption off first."],
            });
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
