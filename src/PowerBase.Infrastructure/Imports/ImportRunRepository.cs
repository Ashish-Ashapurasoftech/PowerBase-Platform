using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;

namespace PowerBase.Infrastructure.Imports;

public sealed class ImportRunRepository(ITenantConnectionFactory connections, IQueryContext context)
    : TenantRepositoryBase(connections, context), IImportRunRepository
{
    public async Task<long> CreateAsync(ImportRun run, CancellationToken ct = default)
    {
        run.PublicId = Guid.NewGuid();
        // A key already used for this import adds nothing. Two requests racing past the check are stopped by the unique index.
        const string sql = """
            INSERT INTO meta.ImportRun (PublicId, ImportDefinitionId, TriggeredBy, TriggeredByUserId, Status, DefinitionSnapshotJson, IdempotencyKey)
            OUTPUT INSERTED.Id
            SELECT @PublicId, @ImportDefinitionId, @TriggeredBy, @TriggeredByUserId, @Status, @DefinitionSnapshotJson, @IdempotencyKey
            WHERE @IdempotencyKey IS NULL
               OR NOT EXISTS (SELECT 1 FROM meta.ImportRun WHERE ImportDefinitionId = @ImportDefinitionId AND IdempotencyKey = @IdempotencyKey)
            """;
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        try
        {
            run.Id = await conn.ExecuteScalarAsync<long?>(new CommandDefinition(sql, run, cancellationToken: ct)) ?? 0;
        }
        catch (Microsoft.Data.SqlClient.SqlException ex) when (ex.Number is 2601 or 2627)
        {
            run.Id = 0;
        }
        return run.Id;
    }

    public async Task<Guid?> FindPublicIdByKeyAsync(long definitionId, string idempotencyKey, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.ExecuteScalarAsync<Guid?>(new CommandDefinition(
            "SELECT PublicId FROM meta.ImportRun WHERE ImportDefinitionId = @definitionId AND IdempotencyKey = @idempotencyKey",
            new { definitionId, idempotencyKey }, cancellationToken: ct));
    }

    public async Task<ImportRun?> GetByPublicIdAsync(Guid publicId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<ImportRun>(new CommandDefinition(
            "SELECT * FROM meta.ImportRun WHERE PublicId = @publicId", new { publicId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ImportRunListItem>> ListByDefinitionAsync(long definitionId, int take, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP (@take) PublicId, TriggeredBy, Status, Progress, RowsRead, Inserted, Updated, Skipped, Errored, StartedOn, CompletedOn, Unchanged
            FROM meta.ImportRun WHERE ImportDefinitionId = @definitionId ORDER BY Id DESC
            """;
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<ImportRunListItem>(new CommandDefinition(sql, new { definitionId, take }, cancellationToken: ct))).AsList();
    }

    public async Task<bool> HasActiveRunAsync(long definitionId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition(
            "SELECT CAST(CASE WHEN EXISTS (SELECT 1 FROM meta.ImportRun WHERE ImportDefinitionId = @definitionId AND Status IN ('queued', 'running')) THEN 1 ELSE 0 END AS BIT)",
            new { definitionId }, cancellationToken: ct));
    }

    public async Task<bool> MarkRunningAsync(long runId, long sourceMaxId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        // Only a run that is still waiting (or already running) may start: one cancelled in the meantime stays cancelled.
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.ImportRun
            SET Status = 'running', StartedOn = COALESCE(StartedOn, SYSUTCDATETIME()),
                SourceMaxId = COALESCE(SourceMaxId, @sourceMaxId)
            WHERE Id = @runId AND Status IN ('queued', 'running')
            """, new { runId, sourceMaxId }, cancellationToken: ct)) > 0;
    }

    public async Task<bool> AdvanceAsync(long runId, ImportChunkResult c, byte progress, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        // The same round trip that records the chunk reports whether a user asked to stop, so cancelling costs no extra query.
        return await conn.ExecuteScalarAsync<bool>(new CommandDefinition("""
            UPDATE meta.ImportRun
            SET RowsRead = RowsRead + @RowsRead, Inserted = Inserted + @Inserted, Updated = Updated + @Updated,
                Skipped = Skipped + @Skipped, Errored = Errored + @Errored, Unchanged = Unchanged + @Unchanged, LastCommittedSourceId = @LastSourceId, Progress = @progress
            OUTPUT inserted.CancelRequested
            WHERE Id = @runId
            """, new { runId, progress, c.RowsRead, c.Inserted, c.Updated, c.Skipped, c.Errored, c.Unchanged, c.LastSourceId }, cancellationToken: ct));
    }

    public async Task<ImportCancelOutcome> RequestCancelAsync(long runId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        var before = await conn.ExecuteScalarAsync<string?>(new CommandDefinition("""
            UPDATE meta.ImportRun
            SET Status = CASE WHEN Status = 'queued' THEN 'cancelled' ELSE Status END,
                CancelRequested = CASE WHEN Status = 'running' THEN 1 ELSE CancelRequested END,
                CompletedOn = CASE WHEN Status = 'queued' THEN SYSUTCDATETIME() ELSE CompletedOn END,
                ErrorDetail = CASE WHEN Status = 'queued' THEN 'Cancelled before it started.' ELSE ErrorDetail END
            OUTPUT deleted.Status
            WHERE Id = @runId AND Status IN ('queued', 'running')
            """, new { runId }, cancellationToken: ct));
        return before switch { "queued" => ImportCancelOutcome.Cancelled, "running" => ImportCancelOutcome.Requested, _ => ImportCancelOutcome.NotActive };
    }

    public async Task<(DateTime ServerTime, IReadOnlyList<ImportRunNotice> Runs)> ListForUserAsync(
        long userId, DateTime? completedSince, int take, CancellationToken ct = default)
    {
        const string sql = """
            SELECT SYSUTCDATETIME();
            SELECT TOP (@take) r.PublicId AS RunId, d.Name AS DefinitionName, a.PublicId AS AppId, t.PublicId AS TableId, r.Status, r.Progress,
                   r.RowsRead, r.Inserted, r.Updated, r.Skipped, r.Errored, r.CompletedOn
            FROM meta.ImportRun r
            JOIN meta.ImportDefinition d ON d.Id = r.ImportDefinitionId
            JOIN meta.AppTable t ON t.Id = d.DestinationTableId
            JOIN meta.App a ON a.Id = t.AppId
            WHERE r.TriggeredByUserId = @userId
              AND (r.Status IN ('queued', 'running') OR (@completedSince IS NOT NULL AND r.CompletedOn >= @completedSince))
            ORDER BY r.Id DESC
            """;
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        using var result = await conn.QueryMultipleAsync(new CommandDefinition(sql, new { userId, completedSince, take }, cancellationToken: ct));
        var serverTime = await result.ReadSingleAsync<DateTime>();
        return (serverTime, (await result.ReadAsync<ImportRunNotice>()).AsList());
    }

    public async Task CompleteAsync(long runId, string status, string? errorDetail, string? feedbackPath = null, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE meta.ImportRun
            SET Status = @status, CompletedOn = SYSUTCDATETIME(), ErrorDetail = @errorDetail, FeedbackFileUrl = @feedbackPath,
                Progress = CASE WHEN @status IN ('success', 'partial') THEN 100 ELSE Progress END
            WHERE Id = @runId
            """, new { runId, status, errorDetail, feedbackPath }, cancellationToken: ct));
    }

    public async Task InitTargetsAsync(long runId, IReadOnlyList<long> destinationTableIds, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        // Re-running this for the same run (a retry) must not fail or double the rows: a target that is already there is left alone.
        await conn.ExecuteAsync(new CommandDefinition(
            "IF NOT EXISTS (SELECT 1 FROM meta.ImportRunTarget WHERE ImportRunId = @runId AND TargetIndex = @index) " +
            "INSERT INTO meta.ImportRunTarget (ImportRunId, TargetIndex, DestinationTableId) VALUES (@runId, @index, @tableId)",
            destinationTableIds.Select((tableId, index) => new { runId, index = (byte)index, tableId }).ToList(), cancellationToken: ct));
    }

    public async Task AddTargetCountsAsync(long runId, IReadOnlyList<ImportTargetCounts> counts, CancellationToken ct = default)
    {
        if (counts.Count == 0) return;
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE meta.ImportRunTarget SET Inserted = Inserted + @Inserted, Updated = Updated + @Updated, Skipped = Skipped + @Skipped, Errored = Errored + @Errored, Unchanged = Unchanged + @Unchanged " +
            "WHERE ImportRunId = @runId AND TargetIndex = @Index",
            counts.Select(c => new { runId, c.Index, c.Inserted, c.Updated, c.Skipped, c.Errored, c.Unchanged }).ToList(), cancellationToken: ct));
    }

    public async Task SyncTotalsFromTargetsAsync(long runId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE r SET Inserted = t.Inserted, Updated = t.Updated, Skipped = t.Skipped, Errored = t.Errored, Unchanged = t.Unchanged
            FROM meta.ImportRun r
            CROSS APPLY (SELECT SUM(Inserted) AS Inserted, SUM(Updated) AS Updated, SUM(Skipped) AS Skipped, SUM(Errored) AS Errored, SUM(Unchanged) AS Unchanged
                         FROM meta.ImportRunTarget WHERE ImportRunId = r.Id HAVING COUNT(*) > 0) t
            WHERE r.Id = @runId
            """, new { runId }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ImportHistoryRow>> ListHistoryAsync(ImportHistoryQuery q, CancellationToken ct = default)
    {
        // A run that filled several tables is a row per table (from its target rows); a run into one table is one row from the run itself.
        // The columns of both halves are the same, in the order ImportHistoryRow takes them.
        const string sql = """
            WITH base AS (
                SELECT r.Id AS RunDbId, r.PublicId AS RunId, d.PublicId AS ImportId, d.Name AS ImportName, d.SourceKind, d.DestinationTableId,
                       JSON_VALUE(r.DefinitionSnapshotJson, '$.file.fileName') AS SourceFileName,
                       r.Status, r.TriggeredBy AS [Trigger], r.TriggeredByUserId, r.CreatedOn, r.StartedOn, r.CompletedOn, r.ErrorDetail,
                       r.FeedbackFileUrl, r.FilesExpiredOn, r.RowsRead, r.Inserted, r.Updated, r.Unchanged, r.Skipped, r.Errored
                FROM meta.ImportRun r
                JOIN meta.ImportDefinition d ON d.Id = r.ImportDefinitionId
                WHERE d.AppId = @appId AND r.CreatedOn >= @from AND (@to IS NULL OR r.CreatedOn < @to)
                  AND (@importId IS NULL OR d.PublicId = @importId) AND (@status IS NULL OR r.Status = @status) AND (@trigger IS NULL OR r.TriggeredBy = @trigger)
            ),
            hist AS (
                SELECT b.RunId, b.ImportId, b.ImportName, t.PublicId AS TableId, t.Name AS TableName,
                       (SELECT COUNT(*) FROM meta.ImportRunTarget x WHERE x.ImportRunId = b.RunDbId) AS TableCount,
                       b.SourceKind, b.SourceFileName, b.Status, b.[Trigger], b.TriggeredByUserId, b.CreatedOn, b.StartedOn, b.CompletedOn,
                       b.RowsRead, rt.Inserted, rt.Updated, rt.Unchanged, rt.Skipped, rt.Errored, b.ErrorDetail,
                       CAST(CASE WHEN rt.DetailsFilePath IS NULL THEN 0 ELSE 1 END AS BIT) AS HasDetails, b.FilesExpiredOn,
                       b.RunDbId, rt.TargetIndex
                FROM base b
                JOIN meta.ImportRunTarget rt ON rt.ImportRunId = b.RunDbId
                JOIN meta.AppTable t ON t.Id = rt.DestinationTableId
                UNION ALL
                SELECT b.RunId, b.ImportId, b.ImportName, t.PublicId, t.Name, 1,
                       b.SourceKind, b.SourceFileName, b.Status, b.[Trigger], b.TriggeredByUserId, b.CreatedOn, b.StartedOn, b.CompletedOn,
                       b.RowsRead, b.Inserted, b.Updated, b.Unchanged, b.Skipped, b.Errored, b.ErrorDetail,
                       CAST(CASE WHEN b.FeedbackFileUrl IS NULL THEN 0 ELSE 1 END AS BIT), b.FilesExpiredOn,
                       b.RunDbId, 0
                FROM base b
                JOIN meta.AppTable t ON t.Id = b.DestinationTableId
                WHERE NOT EXISTS (SELECT 1 FROM meta.ImportRunTarget x WHERE x.ImportRunId = b.RunDbId)
            )
            SELECT RunId, ImportId, ImportName, TableId, TableName, TableCount, SourceKind, SourceFileName, Status, [Trigger], TriggeredByUserId,
                   CreatedOn, StartedOn, CompletedOn, RowsRead, Inserted, Updated, Unchanged, Skipped, Errored, ErrorDetail, HasDetails, FilesExpiredOn,
                   COUNT(*) OVER() AS Total
            FROM hist
            WHERE @tableId IS NULL OR TableId = @tableId
            ORDER BY CreatedOn DESC, RunDbId DESC, TargetIndex
            OFFSET @skip ROWS FETCH NEXT @take ROWS ONLY
            """;
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<ImportHistoryRow>(new CommandDefinition(sql,
            new { appId = q.AppId, from = q.From, to = q.To, importId = q.ImportId, tableId = q.TableId, status = q.Status, trigger = q.Trigger, skip = q.Skip, take = q.Take },
            cancellationToken: ct))).AsList();
    }

    public async Task<(string Path, string TableName)?> GetTargetDetailsAsync(long runId, Guid tablePublicId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        var row = await conn.QuerySingleOrDefaultAsync<(string Path, string TableName)?>(new CommandDefinition("""
            SELECT rt.DetailsFilePath, t.Name FROM meta.ImportRunTarget rt JOIN meta.AppTable t ON t.Id = rt.DestinationTableId
            WHERE rt.ImportRunId = @runId AND t.PublicId = @tablePublicId AND rt.DetailsFilePath IS NOT NULL
            """, new { runId, tablePublicId }, cancellationToken: ct));
        return row;
    }

    public async Task<IReadOnlyList<ImportExpiredFiles>> ListWithExpiredFilesAsync(DateTime cutoff, int take, CancellationToken ct = default)
    {
        const string sql = """
            SELECT TOP (@take) Id AS RunId, FeedbackFileUrl, DefinitionSnapshotJson
            FROM meta.ImportRun
            WHERE FilesExpiredOn IS NULL AND CompletedOn IS NOT NULL AND CompletedOn < @cutoff
            ORDER BY CompletedOn;
            """;
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        var runs = (await conn.QueryAsync<(long RunId, string? FeedbackFileUrl, string DefinitionSnapshotJson)>(
            new CommandDefinition(sql, new { take, cutoff }, cancellationToken: ct))).AsList();
        if (runs.Count == 0) return [];
        var targets = (await conn.QueryAsync<(long RunId, string Path)>(new CommandDefinition(
            "SELECT ImportRunId, DetailsFilePath FROM meta.ImportRunTarget WHERE ImportRunId IN @ids AND DetailsFilePath IS NOT NULL",
            new { ids = runs.Select(r => r.RunId).ToList() }, cancellationToken: ct))).ToLookup(t => t.RunId, t => t.Path);
        return runs.Select(r => new ImportExpiredFiles(r.RunId, r.FeedbackFileUrl, r.DefinitionSnapshotJson, targets[r.RunId].ToList())).ToList();
    }

    public async Task MarkFilesExpiredAsync(long runId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE meta.ImportRun SET FilesExpiredOn = SYSUTCDATETIME() WHERE Id = @runId",
            new { runId }, cancellationToken: ct));
    }

    public async Task SetTargetDetailsAsync(long runId, byte targetIndex, string path, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE meta.ImportRunTarget SET DetailsFilePath = @path WHERE ImportRunId = @runId AND TargetIndex = @targetIndex",
            new { runId, targetIndex, path }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ImportRunTargetItem>> ListTargetsAsync(long runId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<ImportRunTargetItem>(new CommandDefinition("""
            SELECT t.PublicId AS TableId, t.Name AS TableName, rt.Inserted, rt.Updated, rt.Skipped, rt.Errored, rt.Unchanged,
                   CAST(CASE WHEN rt.DetailsFilePath IS NULL THEN 0 ELSE 1 END AS BIT) AS HasDetails
            FROM meta.ImportRunTarget rt JOIN meta.AppTable t ON t.Id = rt.DestinationTableId
            WHERE rt.ImportRunId = @runId ORDER BY rt.TargetIndex
            """, new { runId }, cancellationToken: ct))).AsList();
    }

    public async Task AppendDetailAsync(long runId, string note, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE meta.ImportRun SET ErrorDetail = LEFT(CASE WHEN ErrorDetail IS NULL OR ErrorDetail = '' THEN @note ELSE ErrorDetail + ' ' + @note END, 1000) WHERE Id = @runId",
            new { runId, note }, cancellationToken: ct));
    }

    public async Task<IReadOnlyList<ImportRunIssueItem>> ListIssuesAsync(long runId, int take, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return (await conn.QueryAsync<ImportRunIssueItem>(new CommandDefinition(
            "SELECT TOP (@take) SourceRowRef, ColumnFid, Outcome, ReasonCode, Message, ExistingRecordRef FROM meta.ImportRunIssue WHERE ImportRunId = @runId ORDER BY Id",
            new { runId, take }, cancellationToken: ct))).AsList();
    }

    public async Task<int> CountIssuesAsync(long runId, CancellationToken ct = default)
    {
        await using var conn = await ConnectionFactory.CreateAsync(ct);
        return await conn.ExecuteScalarAsync<int>(new CommandDefinition(
            "SELECT COUNT(*) FROM meta.ImportRunIssue WHERE ImportRunId = @runId", new { runId }, cancellationToken: ct));
    }

    public async Task AddIssuesAsync(long runId, IReadOnlyList<ImportRunIssue> issues, int cap, CancellationToken ct = default)
    {
        var stored = await CountIssuesAsync(runId, ct);
        var room = cap - stored;
        if (room <= 0 || issues.Count == 0) return;

        var table = new DataTable();
        table.Columns.Add("ImportRunId", typeof(long));
        table.Columns.Add("SourceRowRef", typeof(long));
        table.Columns.Add("ColumnFid", typeof(int));
        table.Columns.Add("Outcome", typeof(string));
        table.Columns.Add("ReasonCode", typeof(string));
        table.Columns.Add("Message", typeof(string));
        table.Columns.Add("ExistingRecordRef", typeof(long));
        foreach (var i in issues.Take(room))
            table.Rows.Add(runId, (object?)i.SourceRowRef ?? DBNull.Value, (object?)i.ColumnFid ?? DBNull.Value, i.Outcome, i.ReasonCode, i.Message,
                (object?)i.ExistingRecordRef ?? DBNull.Value);

        await using var conn = await ConnectionFactory.CreateAsync(ct);
        await conn.OpenAsync(ct);
        using var bulk = new SqlBulkCopy(conn) { DestinationTableName = "meta.ImportRunIssue" };
        foreach (DataColumn c in table.Columns) bulk.ColumnMappings.Add(c.ColumnName, c.ColumnName);
        await bulk.WriteToServerAsync(table, ct);
    }
}
