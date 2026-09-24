using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Constants;
using PowerBase.Infrastructure.Repositories;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Services;

public class PipelineRecordSearchService : IPipelineRecordSearchService
{
    private readonly ITenantConnectionFactory _connectionFactory;
    private readonly IQueryContext _queryContext;
    private readonly IEncryptionService _encryptionService;

    public PipelineRecordSearchService(
        ITenantConnectionFactory connectionFactory,
        IQueryContext queryContext,
        IEncryptionService encryptionService)
    {
        _connectionFactory = connectionFactory;
        _queryContext = queryContext;
        _encryptionService = encryptionService;
    }

    public async Task<IReadOnlyList<IReadOnlyDictionary<string, object?>>> SearchAsync(
        AppTable table,
        IReadOnlyList<AppField> fields,
        int? maxResults = null,
        FilterGroup? filterTree = null,
        CancellationToken ct = default,
        int page = 1)
    {
        var buildFieldColsMethod = typeof(RecordRepository).GetMethod("BuildFieldColumnList", BindingFlags.NonPublic | BindingFlags.Static);
        var buildFilterTreeMethod = typeof(RecordRepository).GetMethod("BuildFilterTreeWhere", BindingFlags.NonPublic | BindingFlags.Static);
        var buildOwnerMethod = typeof(RecordRepository).GetMethod("BuildOwnerWhere", BindingFlags.NonPublic | BindingFlags.Static);

        var fieldCols = (string)buildFieldColsMethod!.Invoke(null, new object[] { fields })!;
        var parameters = new DynamicParameters();

        var fieldLookup = fields.Where(f => f.Fid.HasValue).GroupBy(f => (long)f.Fid!.Value).ToDictionary(g => g.Key, g => g.First());
        var filterWhere = (string)buildFilterTreeMethod!.Invoke(null, new object[] { filterTree, parameters, fieldLookup })!
            + (string)buildOwnerMethod!.Invoke(null, new object[] { (long?)null, parameters })!;

        var orderBy = "Id";

        string paginationClause = "";
        if (maxResults.HasValue)
        {
            parameters.Add("offset", (Math.Max(1, page) - 1) * maxResults.Value);
            parameters.Add("pageSize", maxResults.Value);
            paginationClause = "\nOFFSET @offset ROWS FETCH NEXT @pageSize ROWS ONLY";
        }

        var sql = $"""
            SELECT Id, PublicId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy{fieldCols}
            FROM {PhysicalNaming.FullTableName(table.Id)}
            WHERE IsDeleted = 0{filterWhere}
            ORDER BY {orderBy}{paginationClause}
            """;

        await using var connection = await _connectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        var rows = await connection.QueryAsync(new CommandDefinition(sql, parameters, cancellationToken: ct));

        var mutableRows = rows.Select(r => (IDictionary<string, object?>)ToDictionary(r)).ToList();

        var enc = await FieldEncryptionContext.ResolveAsync(connection, table.AppId, _queryContext.TenantId, _encryptionService, null, ct);
        await enc.DecryptRowsAsync(mutableRows, fields, ct);

        return mutableRows.Cast<IReadOnlyDictionary<string, object?>>().ToList();
    }

    private static IReadOnlyDictionary<string, object?> ToDictionary(dynamic row)
    {
        var dict = (IDictionary<string, object>)row;
        return dict.ToDictionary(kvp => kvp.Key, kvp => kvp.Value == DBNull.Value ? null : (object?)kvp.Value);
    }

    public async IAsyncEnumerable<IReadOnlyList<IReadOnlyDictionary<string, object?>>> ReadCopySnapshotAsync(
        AppTable table, IReadOnlyList<AppField> fields, FilterGroup? filterTree,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        // A server-side temporary snapshot gives stable pagination even if the source
        // is also the destination. HOLDLOCK protects only snapshot materialization.
        if (fields.Any(f => PhysicalNaming.IsComputedTypeCode(f.TypeCode)))
            throw PowerBase.Application.Pipelines.CopyRecordsDefinition.Error("Computed source fields are not supported by the Copy Records snapshot reader.");
        var columnMethod = typeof(RecordRepository).GetMethod("BuildFieldColumnList", BindingFlags.NonPublic | BindingFlags.Static)!;
        var filterMethod = typeof(RecordRepository).GetMethod("BuildFilterTreeWhere", BindingFlags.NonPublic | BindingFlags.Static)!;
        var columns = (string)columnMethod.Invoke(null, new object[] { fields })!;
        var parameters = new DynamicParameters();
        var lookup = fields.Where(f => f.Fid.HasValue).ToDictionary(f => (long)f.Fid!.Value);
        var where = (string)filterMethod.Invoke(null, new object?[] { filterTree, parameters, lookup })!;
        await using var connection = await _connectionFactory.CreateAsync(ct);
        // TenantConnectionFactory returns a closed SqlConnection. Keep it explicitly open
        // for the full snapshot lifetime because the temp table, transaction, paging, and
        // field decryption all share this physical session.
        await connection.OpenAsync(ct);
        // Create at connection scope, outside a parameterized sp_executesql batch.
        // An expression strips Id's IDENTITY property; a constant join can be optimized away.
        await connection.ExecuteAsync(new CommandDefinition($"""
            SELECT TOP (0) ISNULL(Id + CONVERT(bigint, 0), CONVERT(bigint, 0)) AS Id,
                PublicId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy{columns}
            INTO #CopyRecordsSnapshot FROM {PhysicalNaming.FullTableName(table.Id)};
            CREATE UNIQUE CLUSTERED INDEX IX_CopyRecordsSnapshot ON #CopyRecordsSnapshot(Id);
            """, cancellationToken: ct));
        // Use a table-scoped serializable lock without leaving SERIALIZABLE on the pooled
        // session, which would break READPAST in the pipeline outbox relay.
        using (var transaction = connection.BeginTransaction(System.Data.IsolationLevel.ReadCommitted))
        {
            await connection.ExecuteAsync(new CommandDefinition($"""
                INSERT INTO #CopyRecordsSnapshot
                SELECT Id, PublicId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy{columns}
                FROM {PhysicalNaming.FullTableName(table.Id)} WITH (HOLDLOCK)
                WHERE IsDeleted = 0{where};
                """, parameters, transaction, commandTimeout: 3600, cancellationToken: ct));
            transaction.Commit();
        }
        var enc = await FieldEncryptionContext.ResolveAsync(connection, table.AppId, _queryContext.TenantId, _encryptionService, null, ct);
        long afterId = 0;
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            var rows = (await connection.QueryAsync(new CommandDefinition(
                "SELECT TOP (250) * FROM #CopyRecordsSnapshot WHERE Id > @afterId ORDER BY Id",
                new { afterId }, cancellationToken: ct))).Select(r => (IDictionary<string, object?>)ToDictionary(r)).ToList();
            if (rows.Count == 0) yield break;
            afterId = Convert.ToInt64(rows[^1]["Id"]);
            await enc.DecryptRowsAsync(rows, fields, ct);
            yield return rows.Cast<IReadOnlyDictionary<string, object?>>().ToList();
        }
    }
}
