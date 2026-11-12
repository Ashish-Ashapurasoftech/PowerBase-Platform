using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.QbMigration;
using PowerBase.Domain.Constants;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.QbMigration;

/// <summary>The migration's own bookkeeping in the tenant's <c>qbm</c> schema (database/qb-migration/001_qbm_state.sql),
/// plus the few read/repair statements the loader needs on the destination table. Writes nothing outside <c>qbm</c>
/// except RecordCount and the identity seed of a table being migrated.</summary>
public sealed class QbmStateStore(ITenantConnectionFactory connections)
{
    public sealed record ChunkRow(long Id, long RunId, long TableMapId, long FromRecordId, long ToRecordId, string Status, int Attempts);

    /// <summary>Creates the qbm schema in the tenant being migrated. Idempotent.</summary>
    public async Task InitAsync(string script, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        await conn.OpenAsync(ct);
        foreach (var batch in SplitBatches(script))
            await conn.ExecuteAsync(new CommandDefinition(batch, cancellationToken: ct));
    }

    public static IEnumerable<string> SplitBatches(string script) =>
        System.Text.RegularExpressions.Regex.Split(script, @"^\s*GO\s*$", System.Text.RegularExpressions.RegexOptions.Multiline | System.Text.RegularExpressions.RegexOptions.IgnoreCase)
            .Where(b => !string.IsNullOrWhiteSpace(b));

    public async Task<long> CreateRunAsync(string realm, string qbAppId, long pbAppId, long runAsUserId, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO qbm.Run (QbRealm, QbAppId, PbAppId, RunAsUserId) OUTPUT INSERTED.Id VALUES (@realm, @qbAppId, @pbAppId, @runAsUserId)
            """, new { realm, qbAppId, pbAppId, runAsUserId }, cancellationToken: ct));
    }

    public async Task<long> SaveTableMapAsync(long runId, QbTable qb, long pbTableId, int tier, IReadOnlyList<QbFieldMapping> fields, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var status = QbFieldMapper.IsLoadable(fields) ? "mapped" : "blocked";
        var id = await conn.ExecuteScalarAsync<long>(new CommandDefinition("""
            INSERT INTO qbm.TableMap (RunId, QbTableId, QbTableName, PbTableId, Tier, Status) OUTPUT INSERTED.Id
            VALUES (@runId, @qbId, @qbName, @pbTableId, @tier, @status)
            """, new { runId, qbId = qb.Id, qbName = qb.Name, pbTableId, tier, status }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO qbm.FieldMap (TableMapId, QbFid, QbLabel, QbType, PbFid, PbTypeCode, Action, Reason)
            VALUES (@id, @QbFid, @QbLabel, @QbType, @PbFid, @PbTypeCode, @Action, @Reason)
            """, fields.Select(f => new
        {
            id, QbFid = f.Qb.Id, QbLabel = f.Qb.Label, QbType = f.Qb.FieldType, f.PbFid, f.PbTypeCode,
            Action = ActionName(f.Action), f.Reason,
        }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return id;
    }

    public static string ActionName(QbFieldAction action) => action switch
    {
        QbFieldAction.AddressPart => "address-part",
        _ => action.ToString().ToLowerInvariant(),
    };

    /// <summary>Records the QuickBase counts and writes the chunk ledger for a table.</summary>
    public async Task PlanChunksAsync(long runId, long tableMapId, long count, long minId, long maxId, IReadOnlyList<QbRange> ranges, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE qbm.TableMap SET QbRecordCount = @count, QbMinRecordId = @minId, QbMaxRecordId = @maxId, Status = 'planned' WHERE Id = @tableMapId
            """, new { tableMapId, count, minId, maxId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO qbm.Chunk (RunId, TableMapId, FromRecordId, ToRecordId) VALUES (@runId, @tableMapId, @From, @To)
            """, ranges.Select(r => new { runId, tableMapId, r.From, r.To }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    /// <summary>End (exclusive) of the Record ID ranges already planned for a table; 0 when none.</summary>
    public async Task<long> PlannedEndAsync(long tableMapId, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT ISNULL(MAX(ToRecordId), 0) FROM qbm.Chunk WHERE TableMapId = @tableMapId", new { tableMapId }, cancellationToken: ct));
    }

    /// <summary>Records QuickBase's current count and Record ID range for a table already in the run, and adds chunks for
    /// records above the planned ranges (records created in QuickBase since the plan). Existing chunks are kept.</summary>
    public async Task ExtendChunksAsync(long runId, long tableMapId, long count, long minId, long maxId, IReadOnlyList<QbRange> ranges, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE qbm.TableMap SET QbRecordCount = @count, QbMinRecordId = @minId, QbMaxRecordId = @maxId,
                Status = CASE WHEN @added > 0 THEN 'loading' ELSE Status END
            WHERE Id = @tableMapId
            """, new { tableMapId, count, minId, maxId, added = ranges.Count }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO qbm.Chunk (RunId, TableMapId, FromRecordId, ToRecordId) VALUES (@runId, @tableMapId, @From, @To)
            """, ranges.Select(r => new { runId, tableMapId, r.From, r.To }), tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<ChunkRow>> PendingChunksAsync(long runId, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        return (await conn.QueryAsync<ChunkRow>(new CommandDefinition("""
            SELECT c.Id, c.RunId, c.TableMapId, c.FromRecordId, c.ToRecordId, c.Status, c.Attempts
            FROM qbm.Chunk c JOIN qbm.TableMap t ON t.Id = c.TableMapId
            WHERE c.RunId = @runId AND c.Status <> 'done' ORDER BY t.Tier, c.TableMapId, c.FromRecordId
            """, new { runId }, cancellationToken: ct))).ToList();
    }

    /// <summary>Puts this run's done chunks back to pending, so the next run reloads them (each chunk replaces its own rows).
    /// Their load issues are removed, because the reload reports them again. Returns the number of chunks reset.</summary>
    public async Task<int> ReloadChunksAsync(long runId, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            DELETE i FROM qbm.Issue i JOIN qbm.Chunk c ON c.Id = i.ChunkId WHERE c.RunId = @runId AND c.Status = 'done'
            """, new { runId }, tx, commandTimeout: 600, cancellationToken: ct)); // millions of per-row issues on a big app
        var reset = await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE qbm.Chunk SET Status = 'pending' WHERE RunId = @runId AND Status = 'done'", new { runId }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE qbm.TableMap SET Status = 'loading' WHERE RunId = @runId AND Status IN ('loaded', 'validated')", new { runId }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return reset;
    }

    public async Task<IReadOnlyList<ChunkRow>> DoneChunksAsync(long runId, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        return (await conn.QueryAsync<ChunkRow>(new CommandDefinition("""
            SELECT Id, RunId, TableMapId, FromRecordId, ToRecordId, Status, Attempts FROM qbm.Chunk
            WHERE RunId = @runId AND Status = 'done' ORDER BY TableMapId, FromRecordId
            """, new { runId }, cancellationToken: ct))).ToList();
    }

    /// <summary>Marks a chunk running and counts the attempt. Returns false when it is already done (a duplicate message).</summary>
    public async Task<bool> StartChunkAsync(long chunkId, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        return await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE qbm.Chunk SET Status = 'running', Attempts = Attempts + 1, StartedOn = SYSUTCDATETIME() WHERE Id = @chunkId AND Status <> 'done'
            """, new { chunkId }, cancellationToken: ct)) == 1;
    }

    public async Task FailChunkAsync(long chunkId, string error, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            UPDATE qbm.Chunk SET Status = 'failed', LastError = LEFT(@error, 2000) WHERE Id = @chunkId AND Status <> 'done'
            """, new { chunkId, error }, cancellationToken: ct));
    }

    /// <summary>Halves a chunk QuickBase keeps timing out on: this chunk keeps the first half, a new pending chunk gets the second.</summary>
    public async Task<bool> SplitChunkAsync(long chunkId, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        await conn.OpenAsync(ct);
        await using var tx = await conn.BeginTransactionAsync(ct);
        var chunk = await conn.QuerySingleAsync<ChunkRow>(new CommandDefinition(
            "SELECT Id, RunId, TableMapId, FromRecordId, ToRecordId, Status, Attempts FROM qbm.Chunk WITH (UPDLOCK) WHERE Id = @chunkId",
            new { chunkId }, tx, cancellationToken: ct));
        if (QbChunkPlanner.Halve(new QbRange(chunk.FromRecordId, chunk.ToRecordId)) is not { } halves || chunk.Status == "done") return false;
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE qbm.Chunk SET ToRecordId = @to, Status = 'pending' WHERE Id = @chunkId", new { chunkId, to = halves.First.To }, tx, cancellationToken: ct));
        await conn.ExecuteAsync(new CommandDefinition(
            "INSERT INTO qbm.Chunk (RunId, TableMapId, FromRecordId, ToRecordId) VALUES (@RunId, @TableMapId, @from, @to)",
            new { chunk.RunId, chunk.TableMapId, from = halves.Second.From, to = halves.Second.To }, tx, cancellationToken: ct));
        await tx.CommitAsync(ct);
        return true;
    }

    public sealed record TableMapRow(long Id, long RunId, string QbTableId, string QbTableName, long PbTableId, int Tier, string Status,
        long? QbRecordCount, long? QbMinRecordId, long? QbMaxRecordId);

    public async Task<IReadOnlyList<TableMapRow>> TableMapsAsync(long runId, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        return (await conn.QueryAsync<TableMapRow>(new CommandDefinition("""
            SELECT Id, RunId, QbTableId, QbTableName, PbTableId, Tier, Status, QbRecordCount, QbMinRecordId, QbMaxRecordId
            FROM qbm.TableMap WHERE RunId = @runId ORDER BY Tier, Id
            """, new { runId }, cancellationToken: ct))).ToList();
    }

    public sealed record RunRow(long Id, string QbRealm, string QbAppId, long PbAppId, long RunAsUserId, string Status);

    public async Task<RunRow> RunAsync(long runId, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        return await conn.QuerySingleOrDefaultAsync<RunRow>(new CommandDefinition(
            "SELECT Id, QbRealm, QbAppId, PbAppId, RunAsUserId, Status FROM qbm.Run WHERE Id = @runId", new { runId }, cancellationToken: ct))
            ?? throw new InvalidOperationException($"Run {runId} does not exist.");
    }

    public async Task SetTableStatusAsync(long tableMapId, string status, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE qbm.TableMap SET Status = @status WHERE Id = @tableMapId", new { tableMapId, status }, cancellationToken: ct));
    }

    public async Task SetRunStatusAsync(long runId, string status, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("UPDATE qbm.Run SET Status = @status, ModifiedOn = SYSUTCDATETIME() WHERE Id = @runId", new { runId, status }, cancellationToken: ct));
    }

    /// <summary>Emails the data named that have no PowerBase user (from QBM_USER_NOT_MAPPED / QBM_SYSTEM_USER_NOT_MAPPED issues).</summary>
    public async Task<IReadOnlyList<string>> UnmappedEmailsAsync(long runId, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        var messages = await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT Message FROM qbm.Issue WHERE RunId = @runId AND Code IN ('QBM_USER_NOT_MAPPED', 'QBM_SYSTEM_USER_NOT_MAPPED')",
            new { runId }, cancellationToken: ct));
        return messages.Select(m => System.Text.RegularExpressions.Regex.Match(m, @"no PowerBase user for (\S+@\S+?)[;,]"))
            .Where(m => m.Success).Select(m => m.Groups[1].Value).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    public sealed record IssueSummary(string Severity, string Code, int Count);

    public async Task<IReadOnlyList<IssueSummary>> IssueSummaryAsync(long runId, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        return (await conn.QueryAsync<IssueSummary>(new CommandDefinition(
            "SELECT Severity, Code, COUNT(*) AS Count FROM qbm.Issue WHERE RunId = @runId GROUP BY Severity, Code ORDER BY Severity, Code",
            new { runId }, cancellationToken: ct))).ToList();
    }

    public async Task AddIssuesAsync(long runId, long? tableMapId, long? chunkId, IReadOnlyList<QbIssue> issues, CancellationToken ct = default)
    {
        if (issues.Count == 0) return;
        await using var conn = await connections.CreateAsync(ct);
        await conn.ExecuteAsync(new CommandDefinition("""
            INSERT INTO qbm.Issue (RunId, TableMapId, ChunkId, QbRecordId, QbFid, Severity, Code, Message)
            VALUES (@runId, @tableMapId, @chunkId, @QbRecordId, @QbFid, @Severity, @Code, LEFT(@Message, 2000))
            """, issues.Select(i => new { runId, tableMapId, chunkId, i.QbRecordId, i.QbFid, i.Severity, i.Code, i.Message }), cancellationToken: ct));
    }

    /// <summary>The earliest other run that has already planned or loaded this PowerBase table, if any. A table belongs to one run;
    /// later changes to it go through that run (<c>plan --extend</c>, <c>run --reload</c>).</summary>
    public async Task<long?> OtherRunForTableAsync(long runId, long pbTableId, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        return await conn.ExecuteScalarAsync<long?>(new CommandDefinition("""
            SELECT TOP 1 RunId FROM qbm.TableMap
            WHERE PbTableId = @pbTableId AND RunId <> @runId AND Status IN ('planned', 'loading', 'loaded', 'validated')
            ORDER BY RunId
            """, new { runId, pbTableId }, cancellationToken: ct));
    }

    /// <summary>Live rows in a destination table. A table must be empty when a run plans it: the migration never mixes
    /// its records with records people created.</summary>
    public async Task<long> CountRowsAsync(long pbTableId, CancellationToken ct = default)
    {
        await using var conn = await connections.CreateAsync(ct);
        return await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT_BIG(*) FROM {PhysicalNaming.FullTableName(pbTableId)}", cancellationToken: ct));
    }

    public sealed record FinalizeResult(long Rows, long MinId, long MaxId, long IdentityBefore, long IdentityAfter);

    /// <summary>After a table's chunks are all done: set RecordCount to the real count and make sure the next record a person
    /// creates gets an id above every migrated one.</summary>
    public async Task<FinalizeResult> FinalizeTableAsync(long pbTableId, CancellationToken ct = default)
    {
        var table = PhysicalNaming.FullTableName(pbTableId);
        await using var conn = await connections.CreateAsync(ct);
        await conn.OpenAsync(ct);
        var stats = await conn.QuerySingleAsync<(long Rows, long MinId, long MaxId)>(new CommandDefinition(
            $"SELECT COUNT_BIG(*), ISNULL(MIN(Id), 0), ISNULL(MAX(Id), 0) FROM {table} WHERE IsDeleted = 0", cancellationToken: ct));
        var before = await IdentityAsync(conn, table, ct);
        if (stats.MaxId > before)
            await conn.ExecuteAsync(new CommandDefinition($"DBCC CHECKIDENT ('{table}', RESEED, {stats.MaxId}) WITH NO_INFOMSGS", cancellationToken: ct));
        var after = await IdentityAsync(conn, table, ct);
        await conn.ExecuteAsync(new CommandDefinition(
            "UPDATE meta.AppTable SET RecordCount = @rows WHERE Id = @pbTableId", new { rows = stats.Rows, pbTableId }, cancellationToken: ct));
        return new FinalizeResult(stats.Rows, stats.MinId, stats.MaxId, before, after);
    }

    private static async Task<long> IdentityAsync(SqlConnection conn, string table, CancellationToken ct) =>
        await conn.ExecuteScalarAsync<long?>(new CommandDefinition($"SELECT CAST(IDENT_CURRENT('{table}') AS BIGINT)", cancellationToken: ct)) ?? 0;
}
