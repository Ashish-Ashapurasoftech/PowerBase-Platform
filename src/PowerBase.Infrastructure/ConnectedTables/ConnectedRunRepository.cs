using Dapper;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.ConnectedTables.Interfaces;
using PowerBase.Domain.ConnectedTables;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.ConnectedTables;

public sealed class ConnectedRunRepository(ITenantConnectionFactory connections, IQueryContext context)
    : TenantRepositoryBase(connections, context), IConnectedRunRepository
{
    private const string Columns = """
        Id, PublicId, ConnectedTableId, TriggeredBy, TriggeredByUserId, Status, IsIncremental, ConfirmMassDelete, CancelRequested,
        RowsRead, Inserted, Updated, Unchanged, Reverted, Deleted, Skipped, Errored, HeldDeleteCount, SourceMaxId, SnapshotJson,
        ErrorDetail, QueuedOn, StartedOn, CompletedOn
        """;

    private const string InsertSql = """
        INSERT INTO connected.Run (ConnectedTableId, TriggeredBy, TriggeredByUserId, Status, ConfirmMassDelete, SnapshotJson)
        OUTPUT inserted.Id, inserted.PublicId
        VALUES (@ConnectedTableId, @TriggeredBy, @TriggeredByUserId, 'queued', @ConfirmMassDelete, @SnapshotJson)
        """;

    private const string GetByPublicIdSql = $"SELECT {Columns} FROM connected.Run WHERE PublicId = @publicId";
    private const string GetActiveSql = $"""
        SELECT TOP 1 {Columns} FROM connected.Run
        WHERE ConnectedTableId = @connectedTableId AND Status IN ('queued', 'running')
        ORDER BY Id DESC
        """;
    private const string ListByTableSql = $"SELECT TOP (@take) {Columns} FROM connected.Run WHERE ConnectedTableId = @connectedTableId ORDER BY Id DESC";

    private const string MarkRunningSql = """
        UPDATE connected.Run SET Status = 'running', StartedOn = SYSUTCDATETIME(), SourceMaxId = @sourceMaxId, IsIncremental = @incremental
        WHERE Id = @id AND Status = 'queued' AND CancelRequested = 0
        """;

    private const string ResetSql = """
        UPDATE connected.Run
        SET Status = 'queued', StartedOn = NULL, RowsRead = 0, Inserted = 0, Updated = 0, Unchanged = 0, Reverted = 0, Deleted = 0,
            Skipped = 0, Errored = 0, HeldDeleteCount = NULL, ErrorDetail = NULL
        WHERE Id = @id AND Status = 'running'
        """;

    private const string SaveProgressSql = """
        UPDATE connected.Run
        SET RowsRead = @RowsRead, Inserted = @Inserted, Updated = @Updated, Unchanged = @Unchanged, Reverted = @Reverted,
            Deleted = @Deleted, Skipped = @Skipped, Errored = @Errored
        WHERE Id = @Id
        """;

    private const string CompleteSql = """
        UPDATE connected.Run
        SET Status = @status, ErrorDetail = @error, CompletedOn = SYSUTCDATETIME(), HeldDeleteCount = @HeldDeleteCount,
            RowsRead = @RowsRead, Inserted = @Inserted, Updated = @Updated, Unchanged = @Unchanged, Reverted = @Reverted,
            Deleted = @Deleted, Skipped = @Skipped, Errored = @Errored
        WHERE Id = @Id
        """;

    private const string RequestCancelSql = """
        UPDATE connected.Run
        SET CancelRequested = 1,
            Status = CASE WHEN Status = 'queued' THEN 'cancelled' ELSE Status END,
            CompletedOn = CASE WHEN Status = 'queued' THEN SYSUTCDATETIME() ELSE CompletedOn END
        WHERE Id = @id AND Status IN ('queued', 'running')
        """;

    private const string IsCancelSql = "SELECT CancelRequested FROM connected.Run WHERE Id = @id";

    private const string InsertIssueSql = """
        INSERT INTO connected.RunIssue (RunId, SourceRecordId, SourceFid, Outcome, ReasonCode, Message)
        VALUES (@RunId, @SourceRecordId, @SourceFid, @Outcome, @ReasonCode, @Message)
        """;

    private const string ListIssuesSql = """
        SELECT TOP (@take) Id, RunId, SourceRecordId, SourceFid, Outcome, ReasonCode, Message
        FROM connected.RunIssue WHERE RunId = @runId ORDER BY Id
        """;

    public async Task<(long Id, Guid PublicId)> CreateAsync(ConnectedRun run, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        try
        {
            return await conn.QuerySingleAsync<(long, Guid)>(new CommandDefinition(InsertSql, run, cancellationToken: ct));
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
        {
            // UX_ConnectedRun_Active: a refresh of this table is already queued or running.
            throw ConnectedTableException.Conflict(ConnectedErrorCodes.RefreshInProgress, "This table is already refreshing. Wait for that refresh to finish.");
        }
    }

    public async Task<ConnectedRun?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ConnectedRun>(new CommandDefinition(GetByPublicIdSql, new { publicId }, cancellationToken: ct));
    }

    public async Task<ConnectedRun?> GetActiveForTableAsync(long connectedTableId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ConnectedRun>(new CommandDefinition(GetActiveSql, new { connectedTableId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ConnectedRun>> ListByTableAsync(long connectedTableId, int take, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<ConnectedRun>(new CommandDefinition(ListByTableSql, new { connectedTableId, take }, cancellationToken: ct))).AsList();
    }

    public async Task<bool> MarkRunningAsync(long id, long sourceMaxId, bool incremental, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(MarkRunningSql, new { id, sourceMaxId, incremental }, cancellationToken: ct)) > 0;
    }

    public async Task ResetToQueuedAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(ResetSql, new { id }, cancellationToken: ct));
    }

    public async Task SaveProgressAsync(ConnectedRun run, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(SaveProgressSql, run, cancellationToken: ct));
    }

    public async Task CompleteAsync(ConnectedRun run, string status, string? error, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(CompleteSql, new
        {
            run.Id, status, error = ConnectedConnectionRepository.Truncate(error, 1000), run.HeldDeleteCount,
            run.RowsRead, run.Inserted, run.Updated, run.Unchanged, run.Reverted, run.Deleted, run.Skipped, run.Errored
        }, cancellationToken: ct));
        run.Status = status;
        run.ErrorDetail = error;
    }

    public async Task<bool> RequestCancelAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition(RequestCancelSql, new { id }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> IsCancelRequestedAsync(long id, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(IsCancelSql, new { id }, cancellationToken: ct));
    }

    public async Task AddIssuesAsync(IReadOnlyList<ConnectedRunIssue> issues, CancellationToken ct = default)
    {
        if (issues.Count == 0) return;
        foreach (var i in issues) i.Message = ConnectedConnectionRepository.Truncate(i.Message, 500) ?? string.Empty;
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(InsertIssueSql, issues, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ConnectedRunIssue>> ListIssuesAsync(long runId, int take, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<ConnectedRunIssue>(new CommandDefinition(ListIssuesSql, new { runId, take }, cancellationToken: ct))).AsList();
    }
}
