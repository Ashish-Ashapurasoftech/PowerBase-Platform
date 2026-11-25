using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.AppLogs;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.AppLogs;

/// <summary>applog.AppLog in the logs database. Singleton (it only holds the connection factory).</summary>
public sealed class AppLogRepository : IAppLogRepository
{
    private readonly LogsConnectionFactory _factory;

    public AppLogRepository(LogsConnectionFactory factory) => _factory = factory;

    private static readonly (string Column, Type Type, Func<AppLogEntry, object?> Value)[] Columns =
    [
        ("TenantId", typeof(long), e => e.TenantId),
        ("AppId", typeof(long), e => e.AppId),
        ("TableId", typeof(long), e => e.TableId),
        ("RecordNumber", typeof(long), e => e.RecordNumber),
        ("UserId", typeof(long), e => e.UserId),
        ("UserName", typeof(string), e => Cut(e.UserName, 256)),
        ("UserEmail", typeof(string), e => Cut(e.UserEmail, 256)),
        ("Action", typeof(string), e => Cut(e.Action, 100) ?? ""),
        ("EntityType", typeof(string), e => Cut(e.EntityType, 100) ?? ""),
        ("EntityId", typeof(string), e => Cut(e.EntityId, 100)),
        ("Description", typeof(string), e => e.Description),
        ("OldValues", typeof(string), e => e.OldValues),
        ("NewValues", typeof(string), e => e.NewValues),
        ("HttpMethod", typeof(string), e => Cut(e.HttpMethod, 10)),
        ("Path", typeof(string), e => Cut(e.Path, 2000)),
        ("StatusCode", typeof(int), e => e.StatusCode),
        ("DurationMs", typeof(int), e => e.DurationMs),
        ("RequestBody", typeof(string), e => e.RequestBody),
        ("IpAddress", typeof(string), e => Cut(e.IpAddress, 50)),
        ("UserAgent", typeof(string), e => Cut(e.UserAgent, 500)),
        ("OccurredOn", typeof(DateTime), e => e.OccurredOn),
    ];

    private static string? Cut(string? value, int max) => value is null || value.Length <= max ? value : value[..max];

    public async Task InsertManyAsync(IReadOnlyList<AppLogEntry> entries, CancellationToken ct = default)
    {
        if (entries.Count == 0) return;
        var table = new DataTable();
        foreach (var (column, type, _) in Columns) table.Columns.Add(column, type).AllowDBNull = true;
        foreach (var entry in entries)
            table.Rows.Add(Columns.Select(c => c.Value(entry) ?? DBNull.Value).ToArray());

        await using var connection = _factory.Create();
        await connection.OpenAsync(ct);
        using var bulk = new SqlBulkCopy(connection) { DestinationTableName = "applog.AppLog", BulkCopyTimeout = 60 };
        foreach (var (column, _, _) in Columns) bulk.ColumnMappings.Add(column, column);
        await bulk.WriteToServerAsync(table, ct);
    }

    private const string ListColumns = """
        l.Id, l.TenantId, l.AppId, l.TableId, l.RecordNumber, l.UserId, l.UserName, l.UserEmail, l.Action, l.EntityType, l.EntityId,
        l.Description, l.HttpMethod, l.Path, l.StatusCode, l.DurationMs, l.IpAddress, l.OccurredOn,
        CAST(CASE WHEN l.OldValues IS NOT NULL OR l.NewValues IS NOT NULL THEN 1 ELSE 0 END AS BIT) AS HasDetails
        """;

    public async Task<(IReadOnlyList<AppLogEntry> Items, int Total)> QueryAsync(AppLogFilter filter, CancellationToken ct = default)
    {
        var conditions = new List<string> { "l.TenantId = @tenantId" };
        if (filter.AppId.HasValue) conditions.Add("l.AppId = @appId");
        if (filter.From.HasValue) conditions.Add("l.OccurredOn >= @from");
        if (filter.To.HasValue) conditions.Add("l.OccurredOn <= @to");
        if (!string.IsNullOrWhiteSpace(filter.Email)) conditions.Add("l.UserEmail = @email");
        if (!string.IsNullOrWhiteSpace(filter.EntityType)) conditions.Add("l.EntityType = @entityType");
        if (!string.IsNullOrWhiteSpace(filter.Action)) conditions.Add("l.Action = @action");
        if (filter.TableId.HasValue) conditions.Add("l.TableId = @tableId");
        if (filter.RecordNumber.HasValue) conditions.Add("l.RecordNumber = @recordNumber");
        var where = "WHERE " + string.Join(" AND ", conditions);
        var pageSize = Math.Clamp(filter.PageSize, 1, 200);
        var offset = (Math.Max(filter.Page, 1) - 1) * pageSize;

        var parameters = new
        {
            tenantId = filter.TenantId, appId = filter.AppId, from = filter.From, to = filter.To, email = filter.Email,
            entityType = filter.EntityType, action = filter.Action, tableId = filter.TableId, recordNumber = filter.RecordNumber,
        };
        await using var connection = _factory.Create();
        var total = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            $"SELECT COUNT(*) FROM applog.AppLog l {where}", parameters, cancellationToken: ct));
        var items = (await connection.QueryAsync<AppLogEntry>(new CommandDefinition(
            $"SELECT {ListColumns} FROM applog.AppLog l {where} ORDER BY l.OccurredOn DESC, l.Id DESC OFFSET {offset} ROWS FETCH NEXT {pageSize} ROWS ONLY",
            parameters, cancellationToken: ct))).AsList();
        return (items, total);
    }

    public async Task<AppLogEntry?> GetAsync(long tenantId, long id, CancellationToken ct = default)
    {
        await using var connection = _factory.Create();
        return await connection.QuerySingleOrDefaultAsync<AppLogEntry>(new CommandDefinition(
            $"SELECT {ListColumns}, l.OldValues, l.NewValues, l.RequestBody, l.UserAgent FROM applog.AppLog l WHERE l.Id = @id AND l.TenantId = @tenantId",
            new { id, tenantId }, cancellationToken: ct));
    }
}
