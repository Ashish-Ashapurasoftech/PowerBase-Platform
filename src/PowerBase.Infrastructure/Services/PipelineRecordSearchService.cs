using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
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
        CancellationToken ct = default)
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
            parameters.Add("offset", 0);
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
}
