using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Imports;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Imports.Tests;

/// <summary>Creates the run, definition, table and app tables the run repository's SQL joins, in tempdb only, and removes
/// every row it adds.</summary>
public sealed class RunSqlFixture : IAsyncLifetime
{
    private sealed class Factory : ITenantConnectionFactory
    {
        public Task<SqlConnection> CreateAsync(CancellationToken ct = default) => Task.FromResult(new SqlConnection(SqlFixture.ConnectionString));
    }

    public ImportRunRepository Runs { get; private set; } = null!;
    public long DefinitionId { get; private set; }
    public Guid AppPublicId { get; } = Guid.NewGuid();
    public Guid TablePublicId { get; } = Guid.NewGuid();
    private long _appId;
    private long _tableId;
    private readonly List<long> _runIds = new();

    public async Task InitializeAsync()
    {
        await using var c = new SqlConnection(SqlFixture.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync("""
            IF SCHEMA_ID('meta') IS NULL EXEC('CREATE SCHEMA meta');
            IF OBJECT_ID('meta.App') IS NULL CREATE TABLE meta.App (Id BIGINT IDENTITY(1,1) PRIMARY KEY, IsDeleted BIT NOT NULL DEFAULT 0, SecurityOptions NVARCHAR(MAX) NULL, IsEncrypted BIT NOT NULL DEFAULT 0);
            IF COL_LENGTH('meta.App', 'PublicId') IS NULL ALTER TABLE meta.App ADD PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID();
            IF OBJECT_ID('meta.AppTable') IS NULL CREATE TABLE meta.AppTable (Id BIGINT IDENTITY(1,1) PRIMARY KEY, RecordCount INT NOT NULL DEFAULT 0);
            IF COL_LENGTH('meta.AppTable', 'PublicId') IS NULL ALTER TABLE meta.AppTable ADD PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID();
            IF COL_LENGTH('meta.AppTable', 'AppId') IS NULL ALTER TABLE meta.AppTable ADD AppId BIGINT NOT NULL DEFAULT 1;
            IF OBJECT_ID('meta.ImportDefinition') IS NULL CREATE TABLE meta.ImportDefinition (Id BIGINT IDENTITY(1,1) PRIMARY KEY, Name NVARCHAR(200) NOT NULL, DestinationTableId BIGINT NOT NULL);
            IF OBJECT_ID('meta.ImportRun') IS NULL CREATE TABLE meta.ImportRun (
                Id BIGINT IDENTITY(1,1) PRIMARY KEY, PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(), ImportDefinitionId BIGINT NOT NULL,
                TriggeredBy NVARCHAR(10) NOT NULL DEFAULT 'manual', TriggeredByUserId BIGINT NOT NULL, Status NVARCHAR(12) NOT NULL,
                Progress TINYINT NOT NULL DEFAULT 0, RowsRead BIGINT NOT NULL DEFAULT 0, Inserted BIGINT NOT NULL DEFAULT 0, Updated BIGINT NOT NULL DEFAULT 0,
                Skipped BIGINT NOT NULL DEFAULT 0, Errored BIGINT NOT NULL DEFAULT 0, LastCommittedSourceId BIGINT NOT NULL DEFAULT 0, SourceMaxId BIGINT NULL,
                CancelRequested BIT NOT NULL DEFAULT 0, StartedOn DATETIME2(3) NULL, CompletedOn DATETIME2(3) NULL, ErrorDetail NVARCHAR(1000) NULL,
                FeedbackFileUrl NVARCHAR(500) NULL, DefinitionSnapshotJson NVARCHAR(MAX) NOT NULL DEFAULT '{}', CreatedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME());
            IF COL_LENGTH('meta.ImportRun', 'CancelRequested') IS NULL ALTER TABLE meta.ImportRun ADD CancelRequested BIT NOT NULL DEFAULT 0;
            IF COL_LENGTH('meta.AppTable', 'Name') IS NULL ALTER TABLE meta.AppTable ADD Name NVARCHAR(200) NULL;
            IF OBJECT_ID('meta.ImportRunTarget') IS NULL CREATE TABLE meta.ImportRunTarget (
                ImportRunId BIGINT NOT NULL, TargetIndex TINYINT NOT NULL, DestinationTableId BIGINT NOT NULL,
                Inserted BIGINT NOT NULL DEFAULT 0, Updated BIGINT NOT NULL DEFAULT 0, Skipped BIGINT NOT NULL DEFAULT 0, Errored BIGINT NOT NULL DEFAULT 0,
                CONSTRAINT PK_ImportRunTarget PRIMARY KEY (ImportRunId, TargetIndex));
            """);
        // Explicit ids from a range no real tenant uses: the tables may already exist from the data store tests without identity columns.
        _appId = 800_000_000L + Random.Shared.Next(99_000_000);
        _tableId = 800_000_000L + Random.Shared.Next(99_000_000);
        await c.ExecuteAsync("INSERT INTO meta.App (Id, PublicId) VALUES (@id, @p)", new { id = _appId, p = AppPublicId });
        await c.ExecuteAsync("INSERT INTO meta.AppTable (Id, PublicId, AppId, Name) VALUES (@id, @p, @a, 'Nightly home')", new { id = _tableId, p = TablePublicId, a = _appId });
        DefinitionId = await c.ExecuteScalarAsync<long>("INSERT INTO meta.ImportDefinition (Name, DestinationTableId) OUTPUT INSERTED.Id VALUES ('Nightly sync', @t)", new { t = _tableId });
        var context = Substitute.For<IQueryContext>();
        context.TenantId.Returns(1);
        Runs = new ImportRunRepository(new Factory(), context);
    }

    public async Task<long> AddRunAsync(string status, long userId = 5, DateTime? completedOn = null)
    {
        await using var c = new SqlConnection(SqlFixture.ConnectionString);
        var id = await c.ExecuteScalarAsync<long>(
            "INSERT INTO meta.ImportRun (ImportDefinitionId, TriggeredByUserId, Status, CompletedOn) OUTPUT INSERTED.Id VALUES (@d, @u, @s, @c)",
            new { d = DefinitionId, u = userId, s = status, c = completedOn });
        _runIds.Add(id);
        return id;
    }

    public async Task<dynamic> RowAsync(long id)
    {
        await using var c = new SqlConnection(SqlFixture.ConnectionString);
        return await c.QuerySingleAsync("SELECT Status, CancelRequested, CompletedOn, ErrorDetail, RowsRead, Inserted, Errored FROM meta.ImportRun WHERE Id = @id", new { id });
    }

    /// <summary>Another destination table (named, with its public id) for a run that fills several.</summary>
    public async Task<(long Id, Guid PublicId)> AddTableAsync(string name)
    {
        var id = 800_000_000L + Random.Shared.Next(99_000_000);
        var publicId = Guid.NewGuid();
        await using var c = new SqlConnection(SqlFixture.ConnectionString);
        await c.ExecuteAsync("INSERT INTO meta.AppTable (Id, PublicId, AppId, Name) VALUES (@id, @p, @a, @n)", new { id, p = publicId, a = _appId, n = name });
        _extraTables.Add(id);
        return (id, publicId);
    }

    public long HomeTableId => _tableId;
    private readonly List<long> _extraTables = new();

    public async Task DisposeAsync()
    {
        await using var c = new SqlConnection(SqlFixture.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync("DELETE FROM meta.ImportRunTarget WHERE ImportRunId IN (SELECT Id FROM meta.ImportRun WHERE ImportDefinitionId = @d)", new { d = DefinitionId });
        foreach (var t in _extraTables) await c.ExecuteAsync("DELETE FROM meta.AppTable WHERE Id = @t", new { t });
        await c.ExecuteAsync("DELETE FROM meta.ImportRun WHERE ImportDefinitionId = @d; DELETE FROM meta.ImportDefinition WHERE Id = @d; DELETE FROM meta.AppTable WHERE Id = @t; DELETE FROM meta.App WHERE Id = @a;",
            new { d = DefinitionId, t = _tableId, a = _appId });
    }
}

/// <summary>The run repository's SQL for stopping a run and listing a user's runs, against a real SQL Server.</summary>
public class ImportRunRepositorySqlTests : IClassFixture<RunSqlFixture>
{
    private readonly RunSqlFixture _db;
    public ImportRunRepositorySqlTests(RunSqlFixture db) => _db = db;

    [SqlFact]
    public async Task Asking_a_running_run_to_stop_sets_a_flag_that_the_next_progress_update_reports()
    {
        var id = await _db.AddRunAsync("running");

        (await _db.Runs.AdvanceAsync(id, new ImportChunkResult(10, 10, 8, 0, 1, 1), 20)).Should().BeFalse();
        (await _db.Runs.RequestCancelAsync(id)).Should().Be(ImportCancelOutcome.Requested);
        (await _db.Runs.AdvanceAsync(id, new ImportChunkResult(20, 10, 9, 0, 0, 1), 40)).Should().BeTrue("the same call that records the chunk reports the request");

        var row = await _db.RowAsync(id);
        ((string)row.Status).Should().Be("running", "the worker, not the request, ends a running run");
        ((long)row.RowsRead).Should().Be(20);
        ((long)row.Inserted).Should().Be(17, "the counters of both chunks were added");
    }

    [SqlFact]
    public async Task A_queued_run_is_cancelled_at_once_and_a_cancelled_run_cannot_be_started()
    {
        var id = await _db.AddRunAsync("queued");

        (await _db.Runs.RequestCancelAsync(id)).Should().Be(ImportCancelOutcome.Cancelled);

        var row = await _db.RowAsync(id);
        ((string)row.Status).Should().Be("cancelled");
        ((object)row.CompletedOn).Should().NotBeNull();
        ((string)row.ErrorDetail).Should().Contain("before it started");
        (await _db.Runs.MarkRunningAsync(id, 100)).Should().BeFalse("a worker that picks it up afterwards must leave it cancelled");
        ((string)(await _db.RowAsync(id)).Status).Should().Be("cancelled");
    }

    [SqlFact]
    public async Task A_queued_run_starts_normally_and_a_running_run_can_be_marked_again()
    {
        var id = await _db.AddRunAsync("queued");

        (await _db.Runs.MarkRunningAsync(id, 500)).Should().BeTrue();
        (await _db.Runs.MarkRunningAsync(id, 999)).Should().BeTrue();

        ((string)(await _db.RowAsync(id)).Status).Should().Be("running");
    }

    [SqlTheory]
    [InlineData("success")]
    [InlineData("partial")]
    [InlineData("failed")]
    [InlineData("cancelled")]
    public async Task A_run_that_has_already_finished_cannot_be_stopped_or_changed(string status)
    {
        var id = await _db.AddRunAsync(status);

        (await _db.Runs.RequestCancelAsync(id)).Should().Be(ImportCancelOutcome.NotActive);

        var row = await _db.RowAsync(id);
        ((string)row.Status).Should().Be(status);
        ((bool)row.CancelRequested).Should().BeFalse();
    }

    [SqlFact]
    public async Task Stopping_a_run_that_does_not_exist_is_not_active()
    {
        (await _db.Runs.RequestCancelAsync(long.MaxValue)).Should().Be(ImportCancelOutcome.NotActive);
    }

    [SqlFact]
    public async Task The_notice_list_holds_the_users_active_runs_and_those_finished_since_the_given_time_and_nothing_else()
    {
        var user = 7000 + Random.Shared.Next(900);
        var since = DateTime.UtcNow.AddMinutes(-5);
        var queued = await _db.AddRunAsync("queued", user);
        var running = await _db.AddRunAsync("running", user);
        var recent = await _db.AddRunAsync("success", user, DateTime.UtcNow.AddMinutes(-1));
        await _db.AddRunAsync("success", user, DateTime.UtcNow.AddHours(-2));   // finished long ago
        await _db.AddRunAsync("running", user + 1000);                           // someone else's
        await _db.AddRunAsync("failed", user + 1000, DateTime.UtcNow);           // someone else's

        var (serverTime, runs) = await _db.Runs.ListForUserAsync(user, since, 20);

        runs.Should().HaveCount(3);
        runs.Select(r => r.Status).Should().BeEquivalentTo("queued", "running", "success");
        runs.Should().OnlyContain(r => r.DefinitionName == "Nightly sync" && r.AppId == _db.AppPublicId && r.TableId == _db.TablePublicId);
        serverTime.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromMinutes(1));
        runs[0].Should().NotBeNull();
        _ = (queued, running, recent);
    }

    [SqlFact]
    public async Task Without_a_since_time_only_active_runs_are_listed()
    {
        var user = 8000 + Random.Shared.Next(900);
        await _db.AddRunAsync("running", user);
        await _db.AddRunAsync("success", user, DateTime.UtcNow);

        var (_, runs) = await _db.Runs.ListForUserAsync(user, null, 20);

        runs.Should().ContainSingle().Which.Status.Should().Be("running");
    }

    [SqlFact]
    public async Task The_list_is_newest_first_and_capped()
    {
        var user = 9000 + Random.Shared.Next(900);
        for (var i = 0; i < 4; i++) await _db.AddRunAsync("running", user);

        var (_, runs) = await _db.Runs.ListForUserAsync(user, null, 3);

        runs.Should().HaveCount(3);
    }

    // ---- an import that fills several tables ----

    [SqlFact]
    public async Task Each_tables_counts_are_kept_added_up_and_totalled_into_the_run()
    {
        var id = await _db.AddRunAsync("running");
        var (second, secondPublic) = await _db.AddTableAsync("Contacts");

        await _db.Runs.InitTargetsAsync(id, [_db.HomeTableId, second]);
        await _db.Runs.InitTargetsAsync(id, [_db.HomeTableId, second]); // a repeat (a retry) must not fail or double the rows
        await _db.Runs.AddTargetCountsAsync(id, [new ImportTargetCounts(0, 10, 2, 1, 1)]);
        await _db.Runs.AddTargetCountsAsync(id, [new ImportTargetCounts(0, 5, 0, 0, 0), new ImportTargetCounts(1, 7, 0, 3, 2)]);
        await _db.Runs.SyncTotalsFromTargetsAsync(id);

        var targets = await _db.Runs.ListTargetsAsync(id);
        targets.Should().HaveCount(2);
        targets[0].Should().Be(new ImportRunTargetItem(_db.TablePublicId, "Nightly home", 15, 2, 1, 1));
        targets[1].Should().Be(new ImportRunTargetItem(secondPublic, "Contacts", 7, 0, 3, 2));
        var row = await _db.RowAsync(id);
        ((long)row.Inserted).Should().Be(22);
        ((long)row.Errored).Should().Be(3);
    }

    [SqlFact]
    public async Task A_run_into_one_table_has_no_table_rows_and_syncing_leaves_its_counters_alone()
    {
        var id = await _db.AddRunAsync("running");
        await _db.Runs.AdvanceAsync(id, new ImportChunkResult(10, 10, 8, 0, 1, 1), 20);

        await _db.Runs.SyncTotalsFromTargetsAsync(id);

        (await _db.Runs.ListTargetsAsync(id)).Should().BeEmpty();
        ((long)(await _db.RowAsync(id)).Inserted).Should().Be(8, "a run with no table rows keeps its own counters");
    }

    [SqlFact]
    public async Task Counting_for_a_table_the_run_does_not_have_changes_nothing()
    {
        var id = await _db.AddRunAsync("running");
        await _db.Runs.InitTargetsAsync(id, [_db.HomeTableId]);

        await _db.Runs.AddTargetCountsAsync(id, [new ImportTargetCounts(3, 9, 9, 9, 9)]);

        (await _db.Runs.ListTargetsAsync(id)).Single().Inserted.Should().Be(0);
    }

    [SqlFact]
    public async Task A_note_is_added_after_any_existing_detail_and_never_overflows_the_column()
    {
        var id = await _db.AddRunAsync("success");

        await _db.Runs.AppendDetailAsync(id, "First.");
        await _db.Runs.AppendDetailAsync(id, "Second.");
        ((string)(await _db.RowAsync(id)).ErrorDetail).Should().Be("First. Second.");

        await _db.Runs.AppendDetailAsync(id, new string('x', 5000));
        ((string)(await _db.RowAsync(id)).ErrorDetail).Length.Should().Be(1000);
    }
}
