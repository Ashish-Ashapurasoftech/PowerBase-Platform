using ClosedXML.Excel;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using PowerBase.Application.QbMigration;
using PowerBase.Infrastructure.QbMigration;

namespace PowerBase.QbMigration.Tests;

public class QbRunControlTests
{
    [Theory]
    [InlineData(null, QbRunCommand.Run)]
    [InlineData("pause", QbRunCommand.Pause)]
    [InlineData(" STOP ", QbRunCommand.Stop)]
    [InlineData("anything else", QbRunCommand.Run)]
    public void Flags_are_read_leniently(string? flag, QbRunCommand expected) => QbRunControl.Parse(flag).Should().Be(expected);

    [Fact]
    public async Task Running_lets_the_next_chunk_go_and_stop_does_not()
    {
        var flag = (string?)null;
        var control = new QbRunControl(_ => Task.FromResult(flag), TimeSpan.Zero);
        (await control.WaitUntilAllowedAsync(default)).Should().BeTrue();
        flag = "stop";
        (await control.WaitUntilAllowedAsync(default)).Should().BeFalse();
        control.Stopped.Should().BeTrue();
    }

    [Fact]
    public async Task Pause_waits_until_resumed()
    {
        var reads = 0;
        var changes = new List<QbRunCommand>();
        // Paused for the first three reads, then resumed.
        var control = new QbRunControl(_ => Task.FromResult<string?>(++reads <= 3 ? "pause" : null), TimeSpan.FromMilliseconds(5), changes.Add);
        (await control.WaitUntilAllowedAsync(default)).Should().BeTrue();
        reads.Should().BeGreaterThan(3);
        changes.Should().Equal(QbRunCommand.Pause, QbRunCommand.Run);
    }

    [Fact]
    public async Task Pause_then_stop_ends_the_wait_with_no()
    {
        var reads = 0;
        var control = new QbRunControl(_ => Task.FromResult<string?>(++reads <= 2 ? "pause" : "stop"), TimeSpan.FromMilliseconds(5));
        (await control.WaitUntilAllowedAsync(default)).Should().BeFalse();
    }

    [Fact]
    public async Task Many_workers_asking_read_the_flag_once_per_interval()
    {
        var reads = 0;
        var control = new QbRunControl(_ => { Interlocked.Increment(ref reads); return Task.FromResult<string?>(null); }, TimeSpan.FromMinutes(1));
        await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => control.WaitUntilAllowedAsync(default)));
        reads.Should().Be(1);
    }
}

public class QbmReportRulesTests
{
    private static readonly DateTime Validated = new(2026, 11, 12, 6, 58, 0);

    private static QbmReport.Table Table(long map, string qbId, string status = "validated", long qb = 10, long pb = 10, int? gate3 = 0) =>
        new(map, 1, qbId, $"T{map}", map + 100, 1, status, qb, qb == 0 ? 0 : 1, qb == 0 ? 0 : qb, pb, pb == 0 ? null : 1, pb == 0 ? null : pb,
            Validated, qb, gate3, 0, 0);

    private static QbmReport.Issue Issue(string code, long? map, DateTime on, string message = "m", long? record = null, int? fid = null, string? qbTable = null) =>
        new(1, 1, map, qbTable, "T", null, record, fid, "error", code, message, on);

    private static QbmReport Report(params QbmReport.Table[] tables) => new()
    {
        AppName = "App", QbRealm = "r", QbAppId = "a", PbAppId = 1, TenantId = 17, GeneratedOn = Validated, Tables = tables,
    };

    [Fact]
    public void Gate_findings_from_before_the_latest_validation_are_superseded()
    {
        var t = Table(5, "q5");
        var byMap = new Dictionary<long, QbmReport.Table> { [5] = t };
        QbmReportRules.StateOf(Issue("QBM_GATE3_MISMATCH", 5, Validated.AddDays(-6)), byMap, new HashSet<string>()).State.Should().Be(QbmReportRules.Superseded);
        QbmReportRules.StateOf(Issue("QBM_GATE3_MISMATCH", 5, Validated.AddMinutes(1)), byMap, new HashSet<string>()).State.Should().Be(QbmReportRules.Current);
    }

    [Fact]
    public void A_block_in_a_run_that_did_not_load_the_table_is_resolved_when_another_run_loaded_it()
    {
        var byMap = new Dictionary<long, QbmReport.Table> { [99] = Table(99, "q7") };
        var block = Issue("QBM_FIELD_BLOCK", 7, Validated, qbTable: "q7");
        QbmReportRules.StateOf(block, byMap, new HashSet<string> { "q7" }).State.Should().Be(QbmReportRules.Resolved);
        QbmReportRules.StateOf(block, byMap, new HashSet<string>()).State.Should().Be(QbmReportRules.Superseded);
    }

    [Fact]
    public void Not_migrated_lists_blocked_tables_files_empty_fields_users_and_links()
    {
        var report = Report(Table(1, "q1"), Table(2, "q2", status: "blocked", pb: 0)) with
        {
            Fields =
            [
                new(1, "q1", "T1", 8, "Contract", "file", "skip", null, null, "File attachments move in the pilot stage."),
                new(1, "q1", "T1", 9, "Old notes", "text", "skip", null, null, "No PowerBase field (the QBL import didn't create it), and it is empty in every QuickBase record."),
                new(1, "q1", "T1", 10, "Name", "text", "load", 10, "Text", null),
                new(2, "q2", "T2", 6, "Broken", "text", "block", null, null, "No PowerBase field with number 6"),
            ],
            Attachments = [new("q1", "T1", 8, "Contract", 66)],
            ValueIssues =
            [
                Issue("QBM_USER_NOT_MAPPED", 1, Validated, "'Owner': no PowerBase user for ; stored empty.", 4, 54, "q1"),
                Issue("QBM_REFERENCE_NOT_FOUND", 1, Validated, "'Dept': no parent record with key '\"HR\"'; stored empty.", 3, 9, "q1"),
            ],
        };

        var items = QbmReportRules.NotMigrated(report);

        items.Select(i => i.Category).Should().Equal("Table not loaded", "File attachment", "Field not created", "User value left empty", "Link value left empty");
        items[0].RecordsAffected.Should().Be(10);
        items[1].RecordsAffected.Should().Be(66);
        items[2].NextStep.Should().Be("None (no data lost)");
        items[3].Why.Should().Contain("no email address");
        items[3].Field.Should().Be("Owner");
        items[4].What.Should().Be("Link to parent key \"HR\" was stored empty");
    }

    [Fact]
    public void A_user_reported_once_per_chunk_does_not_claim_a_record_count()
    {
        var report = Report(Table(1, "q1")) with
        {
            ValueIssues = [Issue("QBM_USER_NOT_MAPPED", 1, Validated, "'Owner': no PowerBase user for a@b.com; stored empty (first record in this chunk; others not listed).", 4, 54, "q1")],
        };
        var item = QbmReportRules.NotMigrated(report).Single();
        item.RecordsAffected.Should().BeNull();
        item.Why.Should().Be("a@b.com has no PowerBase user.");
    }

    [Fact]
    public void The_workbook_has_every_sheet_and_the_totals()
    {
        var report = Report(Table(1, "q1", qb: 1_000_002, pb: 1_000_002), Table(2, "q2", qb: 0, pb: 0, gate3: null)) with
        {
            Runs = [new(1, "validated", null, Validated.AddDays(-6), Validated)],
            Chunks = [new(11, 1, "q1", "T1", 1, 1_000_003, "done", 1_000_002, 1_000_002, 2, Validated, Validated.AddMinutes(6), null)],
            Fields = [new(1, "q1", "T1", 6, "Name", "text", "load", 6, "Text", null)],
            DeadLetters = [new(1, 11, "T1", "ChunkFailed", "boom", Validated)],
        };
        using var stream = new MemoryStream();

        QbmReportWriter.Write(report, stream);

        stream.Position = 0;
        using var wb = new XLWorkbook(stream);
        wb.Worksheets.Select(w => w.Name).Should().Equal("Summary", "Tables", "Not Migrated", "Broken Links", "Fields", "Chunks", "Issues", "Users", "Dead Letters");
        var summary = wb.Worksheet("Summary");
        var cells = summary.RangeUsed()!.Rows().Where(r => !r.Cell(2).IsEmpty()).GroupBy(r => r.Cell(1).GetString()).ToDictionary(g => g.Key, g => g.First().Cell(2));
        cells["QuickBase records"].GetDouble().Should().Be(1_000_002);
        cells["PowerBase records"].GetDouble().Should().Be(1_000_002);
        cells["% migrated"].GetDouble().Should().Be(1);
        cells["Dead-letter messages"].GetDouble().Should().Be(1);
        cells["Gate 3 – every value compared"].GetString().Should().Be("1 of 2");
        var tables = wb.Worksheet("Tables");
        tables.Cell(2, 2).GetString().Should().Be("T1");
        tables.Cell(2, 9).GetDouble().Should().Be(0);
        tables.Cell(3, 17).GetString().Should().Be("Not run");
        wb.Worksheet("Chunks").Cell(2, 6).GetDouble().Should().Be(1_000_002); // last Record ID, inclusive
        wb.Worksheet("Dead Letters").Cell(2, 2).GetDouble().Should().Be(11);
    }
}

/// <summary>Operator controls against tempdb (the qbm schema with 002_qbm_control.sql applied).</summary>
public sealed class QbmOperatorSqlTests : IAsyncLifetime
{
    private readonly SqlDb.Factory _factory = new();
    private QbmStateStore _state = null!;
    private long _run;

    public async Task InitializeAsync()
    {
        _state = new QbmStateStore(_factory);
        foreach (var file in Directory.GetFiles(Path.Combine(AppContext.BaseDirectory, "sql"), "*.sql").Order(StringComparer.Ordinal))
            await _state.InitAsync(await File.ReadAllTextAsync(file));
        _run = await _state.CreateRunAsync("realm", $"op{Random.Shared.Next(1_000_000)}", 7_301, 1);
    }

    public async Task DisposeAsync()
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        await c.ExecuteAsync("""
            DELETE FROM qbm.Chunk WHERE RunId = @run; DELETE FROM qbm.TableMap WHERE RunId = @run; DELETE FROM qbm.Run WHERE Id = @run;
            """, new { run = _run });
    }

    private async Task<(long Map, long[] Chunks)> TableWithChunksAsync(params string[] statuses)
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        var map = await c.ExecuteScalarAsync<long>("""
            INSERT INTO qbm.TableMap (RunId, QbTableId, QbTableName, PbTableId, Status, QbRecordCount) OUTPUT INSERTED.Id
            VALUES (@run, 'qbt', 'Orders', 1, 'loading', 30)
            """, new { run = _run });
        var ids = new List<long>();
        for (var i = 0; i < statuses.Length; i++)
            ids.Add(await c.ExecuteScalarAsync<long>("""
                INSERT INTO qbm.Chunk (RunId, TableMapId, FromRecordId, ToRecordId, Status, RowsWritten, FinishedOn, LastError) OUTPUT INSERTED.Id
                VALUES (@run, @map, @from, @to, @status, CASE WHEN @status = 'done' THEN 10 END, CASE WHEN @status = 'done' THEN SYSUTCDATETIME() END,
                        CASE WHEN @status = 'failed' THEN 'QuickBase said no' END)
                """, new { run = _run, map, from = i * 10 + 1, to = i * 10 + 11, status = statuses[i] }));
        return (map, ids.ToArray());
    }

    [Fact]
    public async Task Pause_and_stop_are_stored_and_resume_clears_them()
    {
        (await _state.ControlAsync(_run)).Should().BeNull();
        await _state.SetControlAsync(_run, "pause");
        (await _state.ControlAsync(_run)).Should().Be("pause");
        await _state.SetControlAsync(_run, null);
        (await _state.ControlAsync(_run)).Should().BeNull();
        await FluentActions.Awaiting(() => _state.SetControlAsync(-1, "stop")).Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task Only_one_worker_can_hold_a_run()
    {
        await using (var first = await _state.TryLockRunAsync(_run))
        {
            first.Should().NotBeNull();
            (await _state.TryLockRunAsync(_run)).Should().BeNull();
        }
        await using var again = await _state.TryLockRunAsync(_run);
        again.Should().NotBeNull("the lock is released when the first worker ends");
    }

    [Fact]
    public async Task Progress_counts_chunks_and_records()
    {
        await TableWithChunksAsync("done", "done", "failed");
        var p = await _state.ProgressAsync(_run);
        p.Should().BeEquivalentTo(new { Tables = 1, QbRecords = 30L, RecordsWritten = 20L, Chunks = 3, ChunksDone = 2, ChunksFailed = 1, ChunksPending = 0, RecordsLast10Minutes = 20L });
    }

    [Fact]
    public async Task A_run_with_no_chunks_has_zero_progress()
    {
        var p = await _state.ProgressAsync(_run);
        p.Chunks.Should().Be(0);
        p.ChunksDone.Should().Be(0);
    }

    [Fact]
    public async Task Retry_puts_failed_chunks_back_to_pending_but_never_a_done_one()
    {
        var (_, chunks) = await TableWithChunksAsync("done", "failed");
        (await _state.ResetChunksAsync(chunks)).Should().Be(1);
        var details = await _state.ChunkDetailsAsync(_run, chunks);
        details.Select(d => d.Status).Should().Equal("done", "pending");
        details[1].LastError.Should().Be("QuickBase said no", "the last error stays for reference");
    }

    [Fact]
    public async Task The_latest_validation_is_saved_per_table()
    {
        var (map, _) = await TableWithChunksAsync("done");
        var started = new DateTime(2026, 11, 12, 6, 58, 0);
        await _state.SaveValidationAsync(map, started, 30, 0, 0);
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        var row = await c.QuerySingleAsync<(DateTime ValidatedOn, long ComparedRecords, int Gate3Differences)>(
            "SELECT ValidatedOn, ComparedRecords, Gate3Differences FROM qbm.TableMap WHERE Id = @map", new { map });
        row.Should().Be((started, 30L, 0));
    }
}
