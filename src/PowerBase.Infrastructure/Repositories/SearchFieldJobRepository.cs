using System.Data;
using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.Repositories;

/// <summary>meta.SearchFieldJob in the current tenant's database. See <see cref="ISearchFieldJobs"/>.</summary>
public class SearchFieldJobRepository : TenantRepositoryBase, ISearchFieldJobs
{
    // 0 = not checked, 1 = table exists, 2 = missing (re-checked every minute). Per process: the table is created by a migration.
    private static int _tableState;
    private static long _tableCheckedTicks;

    private const string Columns = "Id, TableId, AppId, Action, FieldId, Slot, EndSlot, AfterId";

    public SearchFieldJobRepository(ITenantConnectionFactory connectionFactory, IQueryContext queryContext)
        : base(connectionFactory, queryContext)
    {
    }

    private static async Task<bool> TableExistsAsync(IDbConnection connection, CancellationToken ct)
    {
        var state = Volatile.Read(ref _tableState);
        if (state == 1) return true;
        if (state == 2 && DateTime.UtcNow.Ticks - Interlocked.Read(ref _tableCheckedTicks) < TimeSpan.FromMinutes(1).Ticks) return false;

        var exists = await connection.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT CASE WHEN OBJECT_ID('meta.SearchFieldJob') IS NULL THEN 0 ELSE 1 END", cancellationToken: ct)) == 1;
        Interlocked.Exchange(ref _tableCheckedTicks, DateTime.UtcNow.Ticks);
        Volatile.Write(ref _tableState, exists ? 1 : 2);
        return exists;
    }

    private sealed record Row(long Id, long TableId, long AppId, byte Action, long FieldId, string? Slot, string? EndSlot, long AfterId);

    private static SearchFieldJobEntry ToEntry(Row r) => new(r.Id, r.TableId, r.AppId, (IndexAction)r.Action, r.FieldId, r.Slot, r.EndSlot, r.AfterId);

    public async Task<long?> StartAsync(SearchIndexMessage job, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        if (!await TableExistsAsync(connection, ct)) return null;

        return await connection.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO meta.SearchFieldJob (TableId, AppId, Action, FieldId, Slot, EndSlot, AfterId)
            OUTPUT inserted.Id
            VALUES (@tableId, @appId, @action, @fieldId, @slot, @endSlot, @afterId)
            """, new
        {
            tableId = job.TableId,
            appId = job.AppId,
            action = (byte)job.Action,
            fieldId = job.FieldId ?? 0,
            slot = job.Slot,
            endSlot = job.EndSlot,
            afterId = job.AfterId ?? 0
        }, cancellationToken: ct));
    }

    public async Task<bool> AdvanceAsync(long jobId, long fromAfterId, long toAfterId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        // Progress also clears the attempt count: a job that moves is not stalled.
        return await connection.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.SearchFieldJob SET AfterId = @toAfterId, UpdatedOn = SYSUTCDATETIME(), Attempts = 0, LastError = NULL
            WHERE Id = @jobId AND AfterId = @fromAfterId
            """, new { jobId, fromAfterId, toAfterId }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> CompleteAsync(long jobId, long fromAfterId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        return await connection.ExecuteAsync(new CommandDefinition(
            "DELETE FROM meta.SearchFieldJob WHERE Id = @jobId AND AfterId = @fromAfterId",
            new { jobId, fromAfterId }, cancellationToken: ct)) > 0;
    }

    public async Task<IReadOnlyList<SearchFieldJobEntry>> ClaimStalledAsync(int max, TimeSpan stalledAfter, int maxAttempts, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        if (!await TableExistsAsync(connection, ct)) return [];

        // Same claim as the outbox's: several instances can claim at once without taking the same job.
        var rows = await connection.QueryAsync<Row>(new CommandDefinition("""
            ;WITH due AS (
                SELECT TOP (@max) *
                FROM meta.SearchFieldJob WITH (UPDLOCK, READPAST, ROWLOCK)
                WHERE Attempts < @maxAttempts AND UpdatedOn < DATEADD(SECOND, -@stalledSeconds, SYSUTCDATETIME())
                ORDER BY Id
            )
            UPDATE due SET Attempts = Attempts + 1, UpdatedOn = SYSUTCDATETIME()
            OUTPUT inserted.Id, inserted.TableId, inserted.AppId, inserted.Action, inserted.FieldId, inserted.Slot, inserted.EndSlot, inserted.AfterId;
            """, new { max, maxAttempts, stalledSeconds = (int)stalledAfter.TotalSeconds }, cancellationToken: ct));
        return rows.Select(ToEntry).ToList();
    }

    public async Task<IReadOnlyList<SearchFieldJobEntry>> ListForTableAsync(long tableId, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        if (!await TableExistsAsync(connection, ct)) return [];

        var rows = await connection.QueryAsync<Row>(new CommandDefinition(
            $"SELECT {Columns} FROM meta.SearchFieldJob WHERE TableId = @tableId", new { tableId }, cancellationToken: ct));
        return rows.Select(ToEntry).ToList();
    }

    public async Task<int> ReleaseParkedAsync(int maxAttempts, CancellationToken ct = default)
    {
        await using var connection = await ConnectionFactory.CreateAsync(ct);
        await connection.OpenAsync(ct);
        if (!await TableExistsAsync(connection, ct)) return 0;
        return await connection.ExecuteAsync(new CommandDefinition(
            "UPDATE meta.SearchFieldJob SET Attempts = 0 WHERE Attempts >= @maxAttempts", new { maxAttempts }, cancellationToken: ct));
    }
}
