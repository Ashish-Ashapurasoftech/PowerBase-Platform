using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.QbMigration;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.QbMigration;
using static PowerBase.QbMigration.Tests.TestData;

namespace PowerBase.QbMigration.Tests;

/// <summary>One chunk end to end against tempdb: a fake QuickBase that pages like the real one, users resolved from the
/// (tempdb) control tables, transform, load, ledger and issues.</summary>
public sealed class QbmChunkProcessorSqlTests : IAsyncLifetime
{
    private const long App = 7_101, Tenant = 77;
    private readonly long _tableId = 990_000 + Random.Shared.Next(9_000);
    private readonly SqlDb.Factory _factory = new();
    private QbmStateStore _state = null!;

    private sealed class FakeQuickBase(List<QbRow> rows, int pageSize, int? lie = null) : IQuickBaseClient
    {
        public Task<IReadOnlyList<QbTable>> GetTablesAsync(string appId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<IReadOnlyList<QbField>> GetFieldsAsync(string tableId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<QbQueryPage> QueryAsync(string tableId, IReadOnlyList<int> select, string? where, int skip, int? top, CancellationToken ct = default)
        {
            var page = rows.Skip(skip).Take(pageSize).ToList();
            return Task.FromResult(new QbQueryPage(page, lie ?? rows.Count, page.Count, skip));
        }
    }

    public async Task InitializeAsync()
    {
        _state = new QbmStateStore(_factory);
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync("""
            IF SCHEMA_ID('meta') IS NULL EXEC('CREATE SCHEMA meta');
            IF SCHEMA_ID('data') IS NULL EXEC('CREATE SCHEMA data');
            IF SCHEMA_ID('core') IS NULL EXEC('CREATE SCHEMA core');
            IF OBJECT_ID('meta.App') IS NULL CREATE TABLE meta.App (Id BIGINT PRIMARY KEY, IsDeleted BIT NOT NULL DEFAULT 0, SecurityOptions NVARCHAR(MAX) NULL, IsEncrypted BIT NOT NULL DEFAULT 0);
            IF OBJECT_ID('core.[User]') IS NULL CREATE TABLE core.[User] (Id BIGINT PRIMARY KEY, EmailNormalized NVARCHAR(320) NOT NULL, IsDeleted BIT NOT NULL DEFAULT 0);
            IF OBJECT_ID('meta.TenantUser') IS NULL CREATE TABLE meta.TenantUser (TenantId BIGINT NOT NULL, UserId BIGINT NOT NULL, IsDeleted BIT NOT NULL DEFAULT 0);
            DELETE FROM meta.App WHERE Id = 7101;
            INSERT INTO meta.App (Id) VALUES (7101);
            DELETE FROM core.[User] WHERE Id IN (8801, 8802);
            DELETE FROM meta.TenantUser WHERE TenantId = 77;
            INSERT INTO core.[User] (Id, EmailNormalized) VALUES (8801, 'ANN@X.COM'), (8802, 'OUTSIDER@X.COM');
            INSERT INTO meta.TenantUser (TenantId, UserId) VALUES (77, 8801);
            """);
        await _state.InitAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "001_qbm_state.sql")));
        await c.ExecuteAsync($"""
            CREATE TABLE data.t_{_tableId} (
                Id BIGINT IDENTITY(1,1) NOT NULL, PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(), IsDeleted BIT NOT NULL DEFAULT 0,
                CreatedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(), CreatedBy BIGINT NOT NULL DEFAULT 0, ModifiedOn DATETIME2(3) NULL,
                ModifiedBy BIGINT NULL, RowVersion ROWVERSION NOT NULL, CONSTRAINT PK_t_{_tableId} PRIMARY KEY CLUSTERED (Id),
                f_6 NVARCHAR(MAX) NULL, f_7 NVARCHAR(MAX) NULL);
            """);
    }

    public async Task DisposeAsync()
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        await c.ExecuteAsync($"""
            DROP TABLE IF EXISTS data.t_{_tableId};
            DELETE FROM qbm.Chunk WHERE TableMapId = {_tableId};
            DELETE FROM qbm.Issue WHERE TableMapId = {_tableId};
            DELETE FROM meta.App WHERE Id = 7101;
            DELETE FROM core.[User] WHERE Id IN (8801, 8802);
            DELETE FROM meta.TenantUser WHERE TenantId = 77;
            """);
    }

    private QbmTableContext Context()
    {
        List<AppField> pb = [.. SystemFields(), Pb(6, "Text", "Name"), Pb(7, "User", "Owner")];
        List<QbField> qb = [.. QbSystemFields(), new(6, "Name", "text", null, []), new(7, "Owner", "user", null, [])];
        return new QbmTableContext(Tenant, 1, _tableId, "bqtest", new AppTable { Id = _tableId, AppId = App, Name = "T" }, pb,
            QbFieldMapper.Map(qb, pb), new Dictionary<int, QbmPowerBaseReader.PbReference>(), new QbTransformOptions { FallbackUserId = 5 });
    }

    private QbmChunkProcessor Processor(IQuickBaseClient qb) =>
        new(qb, new QbmChunkLoader(_factory, Substitute.For<IEncryptionService>()), _state,
            new QbmPowerBaseReader(_factory, () => new SqlConnection(SqlDb.ConnectionString)), NullLogger<QbmChunkProcessor>.Instance);

    private async Task<long> ChunkAsync(long from, long to)
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        return await c.ExecuteScalarAsync<long>(
            "INSERT INTO qbm.Chunk (RunId, TableMapId, FromRecordId, ToRecordId) OUTPUT INSERTED.Id VALUES (1, @t, @from, @to)", new { t = _tableId, from, to });
    }

    private static List<QbRow> Records() =>
    [
        Row(11, (6, "\"one\""), (7, "{\"email\":\"ann@x.com\"}"), (4, "{\"email\":\"ann@x.com\"}"), (1, "\"2020-01-01T00:00:00Z\"")),
        Row(12, (6, "\"two\""), (7, "{\"email\":\"outsider@x.com\"}")),
        Row(15, (6, "\"three\""), (7, "{\"email\":\"nobody@x.com\"}")),
    ];

    [SqlFact]
    public async Task A_chunk_read_over_several_pages_lands_with_users_resolved_and_problems_logged()
    {
        var chunk = await ChunkAsync(10, 20);
        var outcome = await Processor(new FakeQuickBase(Records(), pageSize: 2)).ProcessAsync(Context(), chunk, new QbRange(10, 20));
        outcome.Should().Be(QbmChunkProcessor.Outcome.Loaded);

        await using var c = new SqlConnection(SqlDb.ConnectionString);
        var rows = (await c.QueryAsync<(long Id, string Name, string? Owner, long CreatedBy)>(
            $"SELECT Id, f_6, f_7, CreatedBy FROM data.t_{_tableId} ORDER BY Id")).ToList();
        rows.Should().Equal((11L, "one", "8801", 8801L), (12L, "two", null, 5L), (15L, "three", null, 5L));

        var issues = (await c.QueryAsync<(string Code, long QbRecordId)>(
            "SELECT Code, QbRecordId FROM qbm.Issue WHERE TableMapId = @t AND Code = 'QBM_USER_NOT_MAPPED' ORDER BY QbRecordId", new { t = _tableId })).ToList();
        // outsider@x.com exists but is not a member of this tenant: not used.
        issues.Should().Equal(("QBM_USER_NOT_MAPPED", 12L), ("QBM_USER_NOT_MAPPED", 15L));
        (await c.ExecuteScalarAsync<string>("SELECT Status FROM qbm.Chunk WHERE Id = @chunk", new { chunk })).Should().Be("done");
    }

    [SqlFact]
    public async Task When_quickbase_returns_fewer_records_than_it_promised_nothing_is_written_and_the_chunk_is_failed()
    {
        var chunk = await ChunkAsync(10, 20);
        var act = () => Processor(new FakeQuickBase(Records(), pageSize: 2, lie: 5)).ProcessAsync(Context(), chunk, new QbRange(10, 20));
        await act.Should().ThrowAsync<QbChunkShortException>();

        await using var c = new SqlConnection(SqlDb.ConnectionString);
        (await c.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM data.t_{_tableId}")).Should().Be(0);
        var ledger = await c.QuerySingleAsync<(string Status, int Attempts, string LastError)>(
            "SELECT Status, Attempts, LastError FROM qbm.Chunk WHERE Id = @chunk", new { chunk });
        ledger.Status.Should().Be("failed");
        ledger.Attempts.Should().Be(1);
        ledger.LastError.Should().Contain("said 5 records but returned 3");
    }

    [SqlFact]
    public async Task A_second_delivery_of_a_done_chunk_does_nothing()
    {
        var chunk = await ChunkAsync(10, 20);
        var processor = Processor(new FakeQuickBase(Records(), pageSize: 10));
        await processor.ProcessAsync(Context(), chunk, new QbRange(10, 20));
        (await processor.ProcessAsync(Context(), chunk, new QbRange(10, 20))).Should().Be(QbmChunkProcessor.Outcome.AlreadyDone);
    }

    [SqlFact]
    public async Task A_table_already_loaded_by_another_run_is_reported_as_owned_by_it()
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        var pbTable = 700000 + _tableId;
        await c.ExecuteAsync("""
            INSERT INTO qbm.TableMap (RunId, QbTableId, QbTableName, PbTableId, Tier, Status) VALUES
                (8001, 'qa', 'A', @pbTable, 1, 'validated'), (8002, 'qa', 'A', @pbTable, 1, 'mapped'), (8003, 'qa', 'A', @pbTable, 1, 'blocked')
            """, new { pbTable });
        try
        {
            (await _state.OtherRunForTableAsync(8002, pbTable)).Should().Be(8001);
            (await _state.OtherRunForTableAsync(8001, pbTable)).Should().BeNull(); // mapped or blocked runs don't own it
        }
        finally { await c.ExecuteAsync("DELETE FROM qbm.TableMap WHERE PbTableId = @pbTable", new { pbTable }); }
    }

    [SqlFact]
    public async Task Extending_a_loaded_table_adds_a_chunk_only_for_records_above_the_planned_ranges()
    {
        var chunk = await ChunkAsync(10, 20);
        await Processor(new FakeQuickBase(Records(), pageSize: 10)).ProcessAsync(Context(), chunk, new QbRange(10, 20));
        (await _state.PlannedEndAsync(_tableId)).Should().Be(20);

        // Record 23 was created in QuickBase after the plan.
        var ranges = QbChunkPlanner.Plan(20, 23, 4, 12000);
        await _state.ExtendChunksAsync(1, _tableId, 4, 11, 23, ranges);
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        var chunks = (await c.QueryAsync<(long From, long To, string Status)>(
            "SELECT FromRecordId, ToRecordId, Status FROM qbm.Chunk WHERE TableMapId = @t ORDER BY FromRecordId", new { t = _tableId })).ToList();
        chunks.Should().Equal((10L, 20L, "done"), (20L, 24L, "pending"));

        // The fake ignores the query range, so it holds only what QuickBase returns for 20..23.
        (await Processor(new FakeQuickBase([Row(23, (6, "\"four\""))], pageSize: 10)).ProcessAsync(Context(), await c.ExecuteScalarAsync<long>(
            "SELECT Id FROM qbm.Chunk WHERE TableMapId = @t AND FromRecordId = 20", new { t = _tableId }), new QbRange(20, 24)))
            .Should().Be(QbmChunkProcessor.Outcome.Loaded);
        (await c.QueryAsync<long>($"SELECT Id FROM data.t_{_tableId} ORDER BY Id")).Should().Equal(11L, 12L, 15L, 23L);
    }

    [SqlFact]
    public async Task A_reload_loads_a_done_chunk_again_without_duplicating_rows_or_issues()
    {
        var chunk = await ChunkAsync(10, 20);
        var processor = Processor(new FakeQuickBase(Records(), pageSize: 10));
        await processor.ProcessAsync(Context(), chunk, new QbRange(10, 20));

        await using var c = new SqlConnection(SqlDb.ConnectionString);
        var run = 9000 + _tableId; // own run id, so other tests' chunks are not reset
        await c.ExecuteAsync("UPDATE qbm.Chunk SET RunId = @run WHERE Id = @chunk", new { run, chunk });
        (await _state.ReloadChunksAsync(run)).Should().Be(1);
        (await c.ExecuteScalarAsync<string>("SELECT Status FROM qbm.Chunk WHERE Id = @chunk", new { chunk })).Should().Be("pending");
        (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM qbm.Issue WHERE ChunkId = @chunk", new { chunk })).Should().Be(0);

        (await processor.ProcessAsync(Context(), chunk, new QbRange(10, 20))).Should().Be(QbmChunkProcessor.Outcome.Loaded);
        (await c.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM data.t_{_tableId}")).Should().Be(3);
        (await c.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM qbm.Issue WHERE ChunkId = @chunk AND Code = 'QBM_USER_NOT_MAPPED'", new { chunk })).Should().Be(2);
    }
}
