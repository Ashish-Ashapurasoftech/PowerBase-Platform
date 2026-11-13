using Dapper;
using Microsoft.Data.SqlClient;
using PowerBase.Application.QbMigration;
using PowerBase.Domain.Constants;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Infrastructure.QbMigration;

/// <summary>Collects the migration report for one QuickBase app -> PowerBase app (every run of that pair). Read-only: the qbm
/// state and meta tables of the tenant, the migrated data tables, core.User in the control database, and QuickBase for the
/// number of records holding a file in attachment fields that were not migrated.</summary>
public sealed class QbmReportCollector(ITenantConnectionFactory tenant, Func<SqlConnection> control, IQuickBaseClient quickBase)
{
    private const int Timeout = 600;

    public async Task<QbmReport> CollectAsync(long runId, long tenantId, IReadOnlyList<QbmReport.DeadLetter> deadLetters, int rowCap = 200_000,
        CancellationToken ct = default)
    {
        await using var conn = await tenant.CreateAsync(ct);
        await conn.OpenAsync(ct);
        var anchor = await conn.QuerySingleOrDefaultAsync<(string QbRealm, string QbAppId, long PbAppId)>(new CommandDefinition(
            "SELECT QbRealm, QbAppId, PbAppId FROM qbm.Run WHERE Id = @runId", new { runId }, cancellationToken: ct));
        if (anchor.QbAppId is null) throw new InvalidOperationException($"Run {runId} does not exist.");
        var app = new { anchor.QbAppId, anchor.PbAppId };

        var runs = (await conn.QueryAsync<QbmReport.Run>(new CommandDefinition(
            "SELECT Id, Status, Control, CreatedOn, ModifiedOn FROM qbm.Run WHERE QbAppId = @QbAppId AND PbAppId = @PbAppId ORDER BY Id",
            app, cancellationToken: ct))).ToList();
        var appName = await conn.ExecuteScalarAsync<string?>(new CommandDefinition(
            "SELECT Name FROM meta.App WHERE Id = @PbAppId", app, cancellationToken: ct)) ?? $"App {anchor.PbAppId}";

        // One row per QuickBase table: the run that planned/loaded it, else the latest run that mapped it.
        var maps = (await conn.QueryAsync<MapRow>(new CommandDefinition("""
            SELECT t.Id, t.RunId, t.QbTableId, t.QbTableName, t.PbTableId, t.Tier, t.Status, t.QbRecordCount, t.QbMinRecordId, t.QbMaxRecordId,
                   t.ValidatedOn, t.ComparedRecords, t.Gate3Differences, t.Gate7NotEncrypted
            FROM qbm.TableMap t JOIN qbm.Run r ON r.Id = t.RunId
            WHERE r.QbAppId = @QbAppId AND r.PbAppId = @PbAppId
            """, app, cancellationToken: ct))).ToList();
        var chosen = maps.GroupBy(m => m.QbTableId)
            .Select(g => g.Where(m => m.Status is "planned" or "loading" or "loaded" or "validated").OrderBy(m => m.RunId).FirstOrDefault()
                         ?? g.OrderByDescending(m => m.RunId).First())
            .ToList();
        var chosenIds = chosen.Select(m => m.Id).ToArray();
        var byPbTable = chosen.GroupBy(m => m.PbTableId).ToDictionary(g => g.Key, g => g.First());

        // Gate 4, exact: every link whose parent record doesn't exist.
        var brokenCounts = new Dictionary<long, long>();
        var brokenLinks = new List<QbmReport.BrokenLink>();
        long brokenTotal = 0;
        var relationships = await conn.QueryAsync<(long ParentTableId, long ChildTableId, int ReferenceFid)>(new CommandDefinition("""
            SELECT ParentTableId, ChildTableId, ReferenceFid FROM meta.Relationship
            WHERE AppId = @PbAppId AND IsDeleted = 0 AND ReferenceFid IS NOT NULL
            """, app, cancellationToken: ct));
        foreach (var rel in relationships)
        {
            if (!byPbTable.TryGetValue(rel.ChildTableId, out var child) || !byPbTable.TryGetValue(rel.ParentTableId, out var parent)) continue;
            var col = PhysicalNaming.ColumnName(rel.ReferenceFid);
            var from = $"""
                FROM {PhysicalNaming.FullTableName(rel.ChildTableId)} c
                WHERE c.IsDeleted = 0 AND c.{col} IS NOT NULL
                  AND NOT EXISTS (SELECT 1 FROM {PhysicalNaming.FullTableName(rel.ParentTableId)} p WHERE p.Id = c.{col} AND p.IsDeleted = 0)
                """;
            var count = await conn.ExecuteScalarAsync<long>(new CommandDefinition($"SELECT COUNT_BIG(*) {from}", commandTimeout: Timeout, cancellationToken: ct));
            if (count == 0) continue;
            brokenCounts[child.Id] = brokenCounts.GetValueOrDefault(child.Id) + count;
            brokenTotal += count;
            var room = rowCap - brokenLinks.Count;
            if (room <= 0) continue;
            var rows = await conn.QueryAsync<(long Id, long Ref)>(new CommandDefinition(
                $"SELECT TOP (@room) c.Id, CAST(c.{col} AS BIGINT) {from} ORDER BY c.Id", new { room }, commandTimeout: Timeout, cancellationToken: ct));
            brokenLinks.AddRange(rows.Select(o => new QbmReport.BrokenLink(child.QbTableName, child.QbTableId, rel.ReferenceFid, parent.QbTableName, o.Id, o.Ref)));
        }

        var tables = new List<QbmReport.Table>();
        foreach (var m in chosen)
        {
            var stats = await conn.QuerySingleAsync<(long Rows, long? MinId, long? MaxId)>(new CommandDefinition(
                $"SELECT COUNT_BIG(*), MIN(Id), MAX(Id) FROM {PhysicalNaming.FullTableName(m.PbTableId)} WHERE IsDeleted = 0",
                commandTimeout: Timeout, cancellationToken: ct));
            tables.Add(new(m.Id, m.RunId, m.QbTableId, m.QbTableName, m.PbTableId, m.Tier, m.Status, m.QbRecordCount, m.QbMinRecordId, m.QbMaxRecordId,
                stats.Rows, stats.MinId, stats.MaxId, m.ValidatedOn, m.ComparedRecords, m.Gate3Differences, m.Gate7NotEncrypted,
                brokenCounts.GetValueOrDefault(m.Id)));
        }
        var qbIdByMap = chosen.ToDictionary(m => m.Id, m => m.QbTableId);
        var qbIdByAnyMap = maps.ToDictionary(m => m.Id, m => m.QbTableId);

        var fields = (await conn.QueryAsync<FieldRow>(new CommandDefinition("""
            SELECT f.TableMapId, t.QbTableName, f.QbFid, f.QbLabel, f.QbType, f.Action, f.PbFid, f.PbTypeCode, f.Reason
            FROM qbm.FieldMap f JOIN qbm.TableMap t ON t.Id = f.TableMapId WHERE f.TableMapId IN @chosenIds
            """, new { chosenIds }, cancellationToken: ct)))
            .Select(f => new QbmReport.Field(f.TableMapId, qbIdByMap[f.TableMapId], f.QbTableName, f.QbFid, f.QbLabel, f.QbType, f.Action, f.PbFid, f.PbTypeCode, f.Reason))
            .ToList();

        var chunks = (await conn.QueryAsync<ChunkRow>(new CommandDefinition("""
            SELECT c.Id, c.RunId, c.TableMapId, t.QbTableName, c.FromRecordId, c.ToRecordId, c.Status, c.RowsRead, c.RowsWritten, c.Attempts,
                   c.StartedOn, c.FinishedOn, c.LastError
            FROM qbm.Chunk c JOIN qbm.TableMap t ON t.Id = c.TableMapId WHERE c.TableMapId IN @chosenIds
            """, new { chosenIds }, commandTimeout: Timeout, cancellationToken: ct)))
            .Select(c => new QbmReport.Chunk(c.Id, c.RunId, qbIdByMap[c.TableMapId], c.QbTableName, c.FromRecordId, c.ToRecordId, c.Status,
                c.RowsRead, c.RowsWritten, c.Attempts, c.StartedOn, c.FinishedOn, c.LastError))
            .ToList();

        var runIds = runs.Select(r => r.Id).ToArray();
        var valueCodes = QbmReportRules.ValueCodes.ToArray();
        const string issueSelect = """
            SELECT TOP (@cap) i.Id, i.RunId, i.TableMapId, t.QbTableName, i.ChunkId, i.QbRecordId, i.QbFid, i.Severity, i.Code, i.Message, i.CreatedOn
            FROM qbm.Issue i LEFT JOIN qbm.TableMap t ON t.Id = i.TableMapId
            """;
        QbmReport.Issue ToIssue(IssueRow i) => new(i.Id, i.RunId, i.TableMapId, i.TableMapId is { } id ? qbIdByAnyMap.GetValueOrDefault(id) : null,
            i.QbTableName, i.ChunkId, i.QbRecordId, i.QbFid, i.Severity, i.Code, i.Message, i.CreatedOn);
        var issueTotal = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT_BIG(*) FROM qbm.Issue WHERE RunId IN @runIds", new { runIds }, commandTimeout: Timeout, cancellationToken: ct));
        var issues = (await conn.QueryAsync<IssueRow>(new CommandDefinition(
            issueSelect + " WHERE i.RunId IN @runIds ORDER BY i.Id", new { cap = rowCap, runIds }, commandTimeout: Timeout, cancellationToken: ct)))
            .Select(ToIssue).ToList();
        // Load issues of tables a later reload replaced are deleted by the reload, so the ones left belong to the current data.
        var valueTotal = await conn.ExecuteScalarAsync<long>(new CommandDefinition(
            "SELECT COUNT_BIG(*) FROM qbm.Issue WHERE TableMapId IN @chosenIds AND Code IN @valueCodes", new { chosenIds, valueCodes },
            commandTimeout: Timeout, cancellationToken: ct));
        var valueIssues = (await conn.QueryAsync<IssueRow>(new CommandDefinition(
            issueSelect + " WHERE i.TableMapId IN @chosenIds AND i.Code IN @valueCodes ORDER BY i.TableMapId, i.QbRecordId",
            new { cap = rowCap, chosenIds, valueCodes }, commandTimeout: Timeout, cancellationToken: ct)))
            .Select(ToIssue).ToList();
        var notFound = (await conn.QueryAsync<string>(new CommandDefinition(
            "SELECT DISTINCT Message FROM qbm.Issue WHERE RunId IN @runIds AND Code = 'QBM_TABLE_NOT_FOUND'", new { runIds }, cancellationToken: ct)))
            .Select(m => m.Split('\'') is { Length: > 2 } p ? p[1] : m)
            .Where(name => !chosen.Any(c => string.Equals(c.QbTableName, name, StringComparison.OrdinalIgnoreCase)))
            .Distinct().ToList();

        var attachments = new List<QbmReport.Attachment>();
        foreach (var f in fields.Where(f => f.Action == "skip" && f.QbType.Equals("file", StringComparison.OrdinalIgnoreCase)))
        {
            var page = await quickBase.QueryAsync(f.QbTableId, [QbSystemFids.RecordId], $"{{{f.QbFid}.XEX.''}}", 0, 1, ct);
            attachments.Add(new(f.QbTableId, f.Table, f.QbFid, f.Label, page.TotalRecords));
        }

        var appUsers = (await conn.QueryAsync<AppUserRow>(new CommandDefinition("""
            SELECT u.UserId, u.UserEmail, u.UserName, r.Name AS RoleName, u.Status, u.CreatedOn
            FROM meta.AppUser u LEFT JOIN meta.AppRole r ON r.Id = u.AppRoleId
            WHERE u.AppId = @PbAppId AND u.IsDeleted = 0 ORDER BY u.UserId
            """, app, cancellationToken: ct))).ToList();
        var accounts = new Dictionary<long, (bool IsActive, DateTime CreatedOn)>();
        if (appUsers.Count > 0)
        {
            await using var controlConn = control();
            await controlConn.OpenAsync(ct);
            foreach (var a in await controlConn.QueryAsync<(long Id, bool IsActive, DateTime CreatedOn)>(new CommandDefinition(
                         "SELECT Id, IsActive, CreatedOn FROM core.[User] WHERE Id IN @ids", new { ids = appUsers.Select(u => u.UserId).ToArray() }, cancellationToken: ct)))
                accounts[a.Id] = (a.IsActive, a.CreatedOn);
        }
        var users = appUsers.Select(u => new QbmReport.User(u.UserId, u.UserEmail, u.UserName, u.RoleName, u.Status,
            accounts.TryGetValue(u.UserId, out var a) ? a.IsActive : null, accounts.TryGetValue(u.UserId, out var b) ? b.CreatedOn : null, u.CreatedOn)).ToList();

        return new QbmReport
        {
            AppName = appName, QbRealm = anchor.QbRealm, QbAppId = anchor.QbAppId, PbAppId = anchor.PbAppId, TenantId = tenantId,
            GeneratedOn = DateTime.UtcNow, RowCap = rowCap, Runs = runs, Tables = tables, Fields = fields, Chunks = chunks,
            Issues = issues, IssueTotal = issueTotal, ValueIssues = valueIssues, ValueIssueTotal = valueTotal,
            BrokenLinks = brokenLinks, BrokenLinkTotal = brokenTotal, Attachments = attachments, Users = users,
            DeadLetters = deadLetters, TablesNotFound = notFound,
        };
    }

    private sealed record MapRow(long Id, long RunId, string QbTableId, string QbTableName, long PbTableId, int Tier, string Status,
        long? QbRecordCount, long? QbMinRecordId, long? QbMaxRecordId, DateTime? ValidatedOn, long? ComparedRecords, int? Gate3Differences, int? Gate7NotEncrypted);

    private sealed record FieldRow(long TableMapId, string QbTableName, int QbFid, string QbLabel, string QbType, string Action, int? PbFid, string? PbTypeCode, string? Reason);

    private sealed record ChunkRow(long Id, long RunId, long TableMapId, string QbTableName, long FromRecordId, long ToRecordId, string Status,
        int? RowsRead, int? RowsWritten, int Attempts, DateTime? StartedOn, DateTime? FinishedOn, string? LastError);

    private sealed record IssueRow(long Id, long RunId, long? TableMapId, string? QbTableName, long? ChunkId, long? QbRecordId, int? QbFid,
        string Severity, string Code, string Message, DateTime CreatedOn);

    private sealed record AppUserRow(long UserId, string? UserEmail, string? UserName, string? RoleName, string? Status, DateTime CreatedOn);
}
