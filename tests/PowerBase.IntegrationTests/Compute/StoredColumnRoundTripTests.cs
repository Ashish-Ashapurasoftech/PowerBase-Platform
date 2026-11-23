using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Domain.Constants;
using PowerBase.Infrastructure.Compute;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.IntegrationTests.Compute;

/// <summary>
/// Phase 3 against a real SQL Server: a Department / Employee app, a Lookup (Employee's department name) and Summaries (department total
/// salary and head count) switched on, then: the first build, live changes through the outbox (create, raise, move to another department,
/// delete, rename the parent), the version fence, the nightly check, the database guard's slot limit, and a field the rules refuse.
/// Every stored value is compared with the value computed straight from the data.
/// </summary>
public class StoredColumnRoundTripTests : IClassFixture<ComputeSqlFixture>, IAsyncLifetime
{
    private const long Dept = 10, Emp = 11;
    private readonly ComputeSqlFixture _db;
    private readonly long _tenantId = Random.Shared.NextInt64(1_000_000, long.MaxValue);
    private readonly ComputeOptions _options = new() { PauseBetweenBatchesMs = 0, SummaryBatchSize = 100, LookupBatchSize = 100, MaxSessionsPerDatabase = 2 };
    private long _nameLookupId, _totalId, _countId, _combinedId;

    public StoredColumnRoundTripTests(ComputeSqlFixture db) => _db = db;

    private sealed class Factory(string cs) : ITenantConnectionFactory
    {
        public Task<SqlConnection> CreateAsync(CancellationToken ct = default) => Task.FromResult(new SqlConnection(cs));
    }

    private (Factory Factory, IQueryContext Context) Tenant()
    {
        var context = Substitute.For<IQueryContext>();
        context.TenantId.Returns(_tenantId);
        return (new Factory(_db.ConnectionString), context);
    }

    private ComputeOutboxRepository Outbox()
    {
        var (factory, context) = Tenant();
        var settings = Substitute.For<IComputeSettings>();
        settings.IsComputeEnabledAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);
        return new ComputeOutboxRepository(factory, context, settings);
    }

    private (MaterializeBuilder Builder, ComputeTableProcessor Processor) Worker()
    {
        var (factory, context) = Tenant();
        var cache = new ComputeModelCache();
        var processor = new ComputeTableProcessor(factory, context, cache);
        return (new MaterializeBuilder(factory, context, new SchemaEngineService(factory), cache, processor), processor);
    }

    private async Task<SqlConnection> OpenAsync()
    {
        var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        return conn;
    }

    public async Task InitializeAsync()
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync($"""
            IF OBJECT_ID('data.t_{Dept}') IS NOT NULL DROP TABLE data.t_{Dept};
            IF OBJECT_ID('data.t_{Emp}') IS NOT NULL DROP TABLE data.t_{Emp};
            DELETE FROM meta.AppField WHERE AppTableId IN ({Dept}, {Emp});
            DELETE FROM meta.AppTable WHERE Id IN ({Dept}, {Emp});
            DELETE FROM meta.App WHERE Id = 1;
            DELETE FROM meta.ComputeOutbox;
            DELETE FROM meta.Report WHERE AppTableId IN ({Dept}, {Emp});
            DELETE FROM meta.AppRoleRecordFilter WHERE AppTableId IN ({Dept}, {Emp});
            INSERT INTO meta.App (Id) VALUES (1);
            INSERT INTO meta.AppTable (Id, AppId) VALUES ({Dept}, 1), ({Emp}, 1);
            CREATE TABLE data.t_{Dept} (Id BIGINT IDENTITY PRIMARY KEY, PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(), IsDeleted BIT NOT NULL DEFAULT 0,
                CreatedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(), f_6 NVARCHAR(MAX) NULL);
            CREATE TABLE data.t_{Emp} (Id BIGINT IDENTITY PRIMARY KEY, PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(), IsDeleted BIT NOT NULL DEFAULT 0,
                CreatedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(), f_6 BIGINT NULL, f_7 DECIMAL(18,4) NULL);
            CREATE INDEX IX_t_{Emp}_f_6 ON data.t_{Emp}(f_6) WHERE IsDeleted = 0;
            """);
        await conn.ExecuteAsync($"""
            INSERT INTO meta.AppField (AppTableId, FieldTypeId, Fid, Name) VALUES ({Dept}, 1, 6, 'Name'), ({Emp}, 3, 6, 'Department'), ({Emp}, 2, 7, 'Salary');
            """);
        _nameLookupId = await AddFieldAsync(conn, Emp, 4, 8, "Dept Name", $"{{\"SourceTableId\":{Dept},\"ReferenceFid\":6,\"SourceFid\":6}}");
        _totalId = await AddFieldAsync(conn, Dept, 5, 8, "Total Salary", $"{{\"ChildTableId\":{Emp},\"ReferenceFid\":6,\"Function\":\"Sum\",\"TargetFid\":7}}");
        _countId = await AddFieldAsync(conn, Dept, 5, 9, "Head Count", $"{{\"ChildTableId\":{Emp},\"ReferenceFid\":6,\"Function\":\"Count\"}}");
        // A summary the rules refuse for good: its criteria ask the viewer for a value (never the same for everyone).
        _combinedId = await AddFieldAsync(conn, Dept, 5, 10, "Asked Count",
            System.Text.Json.JsonSerializer.Serialize(new { ChildTableId = Emp, ReferenceFid = 6, Function = "Count",
                FilterTree = """{"logic":"and","nodes":[{"condition":{"fieldId":7,"operator":"gt","valueMode":"ask"}}]}""" }));

        // 250 departments (more than two batches of 100), 1,000 employees; some departments have nobody.
        await conn.ExecuteAsync($"""
            ;WITH n AS (SELECT TOP (250) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects)
            INSERT INTO data.t_{Dept} (f_6) SELECT CONCAT(N'Dept ', i) FROM n;
            ;WITH n AS (SELECT TOP (1000) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO data.t_{Emp} (f_6, f_7) SELECT CASE WHEN i % 250 IN (0, 1, 2) THEN NULL ELSE (i % 200) + 1 END, i * 10 FROM n;
            UPDATE meta.AppField SET MaterializeMode = 1 WHERE Id IN ({_nameLookupId}, {_totalId}, {_countId}, {_combinedId});
            """);
    }

    private static Task<long> AddFieldAsync(SqlConnection conn, long table, int type, int fid, string name, string settings) =>
        conn.ExecuteScalarAsync<long>("INSERT INTO meta.AppField (AppTableId, FieldTypeId, Fid, Name, Settings) OUTPUT inserted.Id VALUES (@table, @type, @fid, @name, @settings)",
            new { table, type, fid, name, settings });

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Rows where a stored value differs from the value computed from the data right now (0 = all correct).</summary>
    private async Task<(int Lookup, int Total, int Count)> WrongValuesAsync()
    {
        await using var conn = await OpenAsync();
        var lookup = await conn.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM data.t_{Emp} e LEFT JOIN data.t_{Dept} d ON d.Id = e.f_6 AND d.IsDeleted = 0
            WHERE ISNULL(e.c_8, N'~') COLLATE Latin1_General_BIN2 <> ISNULL(CAST(d.f_6 AS NVARCHAR(400)), N'~') COLLATE Latin1_General_BIN2
            """);
        var total = await conn.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM data.t_{Dept} d
            OUTER APPLY (SELECT SUM(e.f_7) AS s, COUNT(*) AS n FROM data.t_{Emp} e WHERE e.f_6 = d.Id AND e.IsDeleted = 0) a
            WHERE ISNULL(d.c_8, -1) <> ISNULL(a.s, -1)
            """);
        var count = await conn.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM data.t_{Dept} d
            OUTER APPLY (SELECT COUNT(*) AS n FROM data.t_{Emp} e WHERE e.f_6 = d.Id AND e.IsDeleted = 0) a
            WHERE ISNULL(d.c_9, -1) <> a.n
            """);
        return (lookup, total, count);
    }

    private async Task<(byte Mode, byte State, int Version)> StateAsync(long fieldId)
    {
        await using var conn = await OpenAsync();
        return await conn.QuerySingleAsync<(byte, byte, int)>("SELECT MaterializeMode, MaterializeState, MaterializeVersion FROM meta.AppField WHERE Id = @fieldId", new { fieldId });
    }

    [Fact]
    public async Task First_build_then_live_changes_keep_every_stored_value_exact()
    {
        var (builder, processor) = Worker();
        var outcomes = await builder.BuildPendingAsync(_options, CancellationToken.None);

        outcomes.Where(o => o.Outcome == "ready").Select(o => o.FieldId).Should().BeEquivalentTo([_nameLookupId, _totalId, _countId]);
        (await StateAsync(_totalId)).State.Should().Be(MaterializeStates.Ready);
        (await StateAsync(_combinedId)).Mode.Should().Be(MaterializeModes.NotAllowed, "a summary asking the viewer is never stored");
        (await WrongValuesAsync()).Should().Be((0, 0, 0));

        await using var conn = await OpenAsync();
        var outbox = Outbox();

        // Create an employee in department 5 (the save writes the row in its transaction).
        var newId = await conn.ExecuteScalarAsync<long>($"INSERT INTO data.t_{Emp} (f_6, f_7) OUTPUT inserted.Id VALUES (5, 777)");
        await outbox.AddAsync([new ComputeChange(Emp, ComputeChangeReason.Create, RecordId: newId, ChangedFids: [6, 7])], null);
        // Move employee 10 from its department to department 7 (OldRefs from OUTPUT deleted.f_6) and give a raise to employee 11.
        var oldDept = await conn.ExecuteScalarAsync<long>($"UPDATE data.t_{Emp} SET f_6 = 7 OUTPUT deleted.f_6 WHERE Id = 10");
        await outbox.AddAsync([new ComputeChange(Emp, ComputeChangeReason.Update, RecordId: 10, ChangedFids: [6], OldRefs: new Dictionary<int, long?> { [6] = oldDept })], null);
        await conn.ExecuteAsync($"UPDATE data.t_{Emp} SET f_7 = f_7 + 5000 WHERE Id = 11");
        await outbox.AddAsync([new ComputeChange(Emp, ComputeChangeReason.Update, RecordId: 11, ChangedFids: [7])], null);
        // Delete employee 12 (soft delete).
        await conn.ExecuteAsync($"UPDATE data.t_{Emp} SET IsDeleted = 1 WHERE Id = 12");
        await outbox.AddAsync([new ComputeChange(Emp, ComputeChangeReason.Delete, RecordId: 12)], null);

        (await WrongValuesAsync()).Should().NotBe((0, 0, 0), "nothing has been applied yet");
        var drained = await processor.DrainAsync(Emp, _options, CancellationToken.None);
        drained.RowsProcessed.Should().Be(4);
        (await WrongValuesAsync()).Should().Be((0, 0, 0));
        (await outbox.CountPendingAsync(Emp)).Should().Be(0, "applied rows are deleted");

        // Rename department 7 and delete department 9: their employees' lookups follow.
        var renamed = await conn.ExecuteScalarAsync<Guid>($"UPDATE data.t_{Dept} SET f_6 = N'Research' OUTPUT inserted.PublicId WHERE Id = 7");
        await conn.ExecuteAsync($"UPDATE data.t_{Dept} SET IsDeleted = 1 WHERE Id = 9");
        await outbox.AddAsync([
            new ComputeChange(Dept, ComputeChangeReason.Update, RecordPublicId: renamed, ChangedFids: [6]),
            new ComputeChange(Dept, ComputeChangeReason.Delete, RecordId: 9)], null);
        await processor.DrainAsync(Dept, _options, CancellationToken.None);
        (await WrongValuesAsync()).Should().Be((0, 0, 0));
        (await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM data.t_{Emp} WHERE f_6 = 7 AND c_8 = N'Research'")).Should().BeGreaterThan(0);

        // A whole-table change (an import) recomputes everything.
        await conn.ExecuteAsync($"UPDATE data.t_{Emp} SET f_7 = f_7 * 2 WHERE Id % 3 = 0");
        await outbox.AddAsync([ComputeChange.TableWide(Emp)], null);
        await processor.DrainAsync(Emp, _options, CancellationToken.None);
        (await WrongValuesAsync()).Should().Be((0, 0, 0));
    }

    private MaterializedFieldStateRepository StateRepo(bool enabled = true)
    {
        var (factory, context) = Tenant();
        var settings = Substitute.For<IComputeSettings>();
        settings.IsComputeEnabledAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(enabled);
        settings.StoreAllCalculatedFields.Returns(true);
        return new MaterializedFieldStateRepository(factory, context, settings, new Microsoft.Extensions.Caching.Memory.MemoryCache(
            new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
    }

    private static PowerBase.Domain.Entities.AppField Field(long id, long table, int fid, string type) =>
        new() { Id = id, AppTableId = table, Fid = fid, TypeCode = type, Name = $"f{fid}" };

    [Fact]
    public async Task Every_calculated_field_is_switched_on_but_an_excluded_one_and_plain_fields_are_left_alone()
    {
        await using var conn = await OpenAsync();
        // New fields start undecided (0); the admin excluded Head Count (3).
        await conn.ExecuteAsync("UPDATE meta.AppField SET MaterializeMode = 0 WHERE AppTableId IN (@Dept, @Emp)", new { Dept, Emp });
        await conn.ExecuteAsync("UPDATE meta.AppField SET MaterializeMode = 3 WHERE Id = @_countId", new { _countId });
        var (builder, _) = Worker();
        var state = StateRepo();
        (await state.GetStatusAsync(Field(_totalId, Dept, 8, "Summary")))!.Status.Should().Be(StoredColumnStatuses.Waiting, "undecided = about to be switched on");

        (await builder.SwitchOnUndecidedAsync(CancellationToken.None)).Should().Be(3, "the lookup, Total Salary and Asked Count; never the plain fields");
        (await StateAsync(_countId)).Mode.Should().Be(MaterializeModes.Excluded);
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM meta.AppField WHERE AppTableId IN (@Dept, @Emp) AND FieldTypeId IN (1, 2, 3) AND MaterializeMode <> 0", new { Dept, Emp }))
            .Should().Be(0);

        var outcomes = await builder.BuildPendingAsync(_options, CancellationToken.None);
        outcomes.Where(o => o.Outcome == "ready").Select(o => o.FieldId).Should().BeEquivalentTo([_nameLookupId, _totalId]);
        (await StateAsync(_combinedId)).Mode.Should().Be(MaterializeModes.NotAllowed, "the rules still refuse a summary asking the viewer");
        (await builder.SwitchOnUndecidedAsync(CancellationToken.None)).Should().Be(0, "nothing is undecided any more");

        // The field page's status, read fresh from the table.
        var ready = (await state.GetStatusAsync(Field(_totalId, Dept, 8, "Summary")))!;
        ready.Status.Should().Be(StoredColumnStatuses.Ready);
        ready.BuiltOn.Should().NotBeNull();
        (await state.GetStatusAsync(Field(_combinedId, Dept, 10, "Summary")))!.Status.Should().Be(StoredColumnStatuses.NotAllowed);
        (await state.GetStatusAsync(Field(_countId, Dept, 9, "Summary")))!.Status.Should().Be(StoredColumnStatuses.Excluded);
        (await state.GetStatusAsync(Field(999, Dept, 6, "Text"))).Should().BeNull("plain fields have no stored column");
        (await StateRepo(enabled: false).GetStatusAsync(Field(_totalId, Dept, 8, "Summary")))!.Status.Should().Be(StoredColumnStatuses.Disabled);

        // A change waiting for the worker on the table the summary reads (Employees) shows as "updating".
        var total = Field(_totalId, Dept, 8, "Summary");
        total.Settings = $"{{\"ChildTableId\":{Emp},\"ReferenceFid\":6,\"Function\":\"Sum\",\"TargetFid\":7}}";
        (await StateRepo().GetStatusAsync(total))!.Updating.Should().BeFalse();
        await Outbox().AddAsync([new ComputeChange(Emp, ComputeChangeReason.Update, RecordId: 5, ChangedFids: [7])], null);
        (await StateRepo().GetStatusAsync(total))!.Updating.Should().BeTrue();
    }

    [Fact]
    public async Task The_nightly_recheck_switches_on_a_refused_field_only_when_the_rules_now_allow_it()
    {
        var (builder, _) = Worker();
        await builder.BuildPendingAsync(_options, CancellationToken.None);
        (await StateAsync(_combinedId)).Mode.Should().Be(MaterializeModes.NotAllowed);
        (await builder.RecheckNotAllowedAsync(CancellationToken.None)).Should().Be(0, "a summary asking the viewer is still refused");

        // A field marked Not allowed earlier that the rules allow now (here: Head Count, marked by hand to stand in for a changed input).
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync("UPDATE meta.AppField SET MaterializeMode = 2, MaterializeState = 0 WHERE Id = @_countId", new { _countId });
        (await builder.RecheckNotAllowedAsync(CancellationToken.None)).Should().Be(1);
        (await StateAsync(_countId)).Mode.Should().Be(MaterializeModes.On);
        (await builder.BuildPendingAsync(_options, CancellationToken.None)).Should().Contain(o => o.FieldId == _countId && o.Outcome == "ready");
    }

    private async Task<IReadOnlyList<string>> StoredIndexesAsync()
    {
        await using var conn = await OpenAsync();
        return (await conn.QueryAsync<string>($"""
            SELECT name FROM sys.indexes WHERE object_id IN (OBJECT_ID('data.t_{Dept}'), OBJECT_ID('data.t_{Emp}')) AND name LIKE 'IX[_]t[_]%[_]c[_]%' ORDER BY name
            """)).AsList();
    }

    [Fact]
    public async Task Indexes_follow_what_reports_and_role_rules_sort_and_filter_on()
    {
        var (builder, _) = Worker();
        await builder.BuildPendingAsync(_options, CancellationToken.None);
        var (factory, context) = Tenant();
        var maintainer = new MaterializedIndexMaintainer(factory, context, new SchemaEngineService(factory), new ComputeModelCache());

        (await maintainer.SyncAsync(_options, CancellationToken.None)).Should().BeEmpty("no report or rule uses a stored field yet");
        (await StoredIndexesAsync()).Should().BeEmpty();

        // A report sorts Departments by Total Salary (fid 8) and shows Head Count (fid 9) only as a column; a role rule filters on Head Count.
        await using var conn = await OpenAsync();
        var reportId = await conn.ExecuteScalarAsync<long>(
            "INSERT INTO meta.Report (AppTableId, Definition) OUTPUT inserted.Id VALUES (@Dept, @def)",
            new { Dept, def = """{"Columns":[6,8,9],"SortFields":[{"FieldId":8,"Desc":true}]}""" });
        var outcomes = await maintainer.SyncAsync(_options, CancellationToken.None);
        outcomes.Should().ContainSingle(o => o.Outcome == "built" && o.FieldId == _totalId);
        (await StoredIndexesAsync()).Should().Equal($"IX_t_{Dept}_c_8");

        await conn.ExecuteAsync("INSERT INTO meta.AppRoleRecordFilter (AppRoleId, AppTableId, Conjunction, FilterJson) VALUES (1, @Dept, 'and', @json)",
            new { Dept, json = """{"logic":"and","nodes":[{"condition":{"fieldId":9,"operator":"gt","value":"3"}}]}""" });
        await maintainer.SyncAsync(_options, CancellationToken.None);
        (await StoredIndexesAsync()).Should().Equal($"IX_t_{Dept}_c_8", $"IX_t_{Dept}_c_9");

        // The index is the filtered sort index the read path can use, and a second pass changes nothing.
        (await conn.ExecuteScalarAsync<string>($"SELECT filter_definition FROM sys.indexes WHERE name = 'IX_t_{Dept}_c_8'")).Should().Be("([IsDeleted]=(0))");
        (await maintainer.SyncAsync(_options, CancellationToken.None)).Should().BeEmpty();

        // The report is deleted: the index nobody uses any more is dropped; the rule's stays.
        await conn.ExecuteAsync("UPDATE meta.Report SET IsDeleted = 1 WHERE Id = @reportId", new { reportId });
        (await maintainer.SyncAsync(_options, CancellationToken.None)).Should().ContainSingle(o => o.Outcome == "dropped" && o.FieldId == _totalId);
        (await StoredIndexesAsync()).Should().Equal($"IX_t_{Dept}_c_9");

        // Switching the field off drops its column and the index with it.
        await new SchemaEngineService(factory).DropMaterializedColumnAsync(Dept, 9);
        (await StoredIndexesAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task A_version_change_mid_way_stops_the_old_job_without_writing()
    {
        var (builder, _) = Worker();
        await builder.BuildPendingAsync(_options, CancellationToken.None);
        var (_, _, version) = await StateAsync(_totalId);

        await using var conn = await OpenAsync();
        await conn.ExecuteAsync($"UPDATE data.t_{Dept} SET c_8 = -1 WHERE Id = 3");
        await conn.ExecuteAsync("UPDATE meta.AppField SET MaterializeVersion = MaterializeVersion + 1 WHERE Id = @id", new { id = _totalId });

        var (factory, context) = Tenant();
        var model = new ComputeModel(await new ComputeSchemaRepository(factory, context).LoadAsync());
        var plan = model.Decisions.Values.Single(d => d.Field.Id == _totalId).Decision.Plan!;
        await using var guard = await DatabaseGuard.AcquireAsync(factory.CreateAsync, _options, false, TimeSpan.FromSeconds(5), null, CancellationToken.None);

        var act = () => MaterializedColumnWriter.RecomputeAsync(guard!, plan, "DECIMAL(38,4)", version, new WriteScope.Rows([3]), model.Schema, _options, false, CancellationToken.None);
        await act.Should().ThrowAsync<MaterializeVersionChangedException>();
        (await conn.ExecuteScalarAsync<decimal>($"SELECT c_8 FROM data.t_{Dept} WHERE Id = 3")).Should().Be(-1, "the fenced statement wrote nothing");
    }

    [Fact]
    public async Task The_nightly_check_finds_and_fixes_a_value_a_write_path_left_stale()
    {
        var (builder, _) = Worker();
        await builder.BuildPendingAsync(_options, CancellationToken.None);

        await using var conn = await OpenAsync();
        // A support script changes salaries without an outbox row.
        await conn.ExecuteAsync($"UPDATE data.t_{Emp} SET f_7 = 1 WHERE f_6 = 4");
        await conn.ExecuteAsync($"UPDATE data.t_{Emp} SET c_8 = N'wrong' WHERE Id = 20");
        (await WrongValuesAsync()).Should().NotBe((0, 0, 0));

        var outcomes = await builder.ReconcileAsync(_options, CancellationToken.None);
        outcomes.Single(o => o.FieldId == _totalId).Differences.Should().Be(1);
        outcomes.Single(o => o.FieldId == _nameLookupId).Differences.Should().Be(1);
        outcomes.Single(o => o.FieldId == _countId).Differences.Should().Be(0);
        (await WrongValuesAsync()).Should().Be((0, 0, 0));

        (await builder.ReconcileAsync(_options, CancellationToken.None)).Sum(o => o.Differences).Should().Be(0, "a second check finds nothing");
    }

    [Fact]
    public async Task A_field_that_just_started_building_is_seen_before_the_next_change_is_applied()
    {
        var (factory, context) = Tenant();
        var cache = new ComputeModelCache();
        var processor = new ComputeTableProcessor(factory, context, cache);
        var repo = new ComputeSchemaRepository(factory, context);
        await cache.GetAsync(_tenantId, repo, fresh: false, CancellationToken.None);   // cached while Head Count is still "not built"

        // Another replica starts building Head Count and has already passed department 5 ...
        (await new SchemaEngineService(factory).EnsureMaterializedColumnAsync(Dept, 9, "INT")).Should().BeTrue();
        var version = await repo.StartBuildAsync(_countId);
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync($"UPDATE data.t_{Dept} SET c_9 = (SELECT COUNT(*) FROM data.t_{Emp} WHERE f_6 = 5 AND IsDeleted = 0) WHERE Id = 5");
        // ... when an employee joins department 5.
        var id = await conn.ExecuteScalarAsync<long>($"INSERT INTO data.t_{Emp} (f_6, f_7) OUTPUT inserted.Id VALUES (5, 1)");
        await Outbox().AddAsync([new ComputeChange(Emp, ComputeChangeReason.Create, RecordId: id, ChangedFids: [6, 7])], null);

        await processor.DrainAsync(Emp, _options, CancellationToken.None);

        version.Should().NotBeNull();
        (await conn.ExecuteScalarAsync<int>($"SELECT c_9 FROM data.t_{Dept} WHERE Id = 5"))
            .Should().Be(await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM data.t_{Emp} WHERE f_6 = 5 AND IsDeleted = 0"));
    }

    // ── Phase 4: reads ─────────────────────────────────────────────────────────────────────────────────

    private MaterializedFieldStateRepository FieldState(bool computeOn = true)
    {
        var (factory, context) = Tenant();
        var settings = Substitute.For<IComputeSettings>();
        settings.IsComputeEnabledAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(computeOn);
        return new MaterializedFieldStateRepository(factory, context, settings, new Microsoft.Extensions.Caching.Memory.MemoryCache(new Microsoft.Extensions.Caching.Memory.MemoryCacheOptions()));
    }

    [Fact]
    public async Task Sorting_and_filtering_through_the_stored_column_matches_the_true_order_over_every_row()
    {
        var (builder, _) = Worker();
        await builder.BuildPendingAsync(_options, CancellationToken.None);
        var ready = await FieldState().ReadyFieldIdsAsync();
        ready.Should().BeEquivalentTo([_nameLookupId, _totalId, _countId]);

        // What RunReport hands the repository: the Ready summary as a stand-in reading c_8.
        var total = new PowerBase.Domain.Entities.AppField
        {
            Id = _totalId, AppTableId = Dept, Fid = 8, TypeCode = "Summary", Name = "Total Salary",
            Settings = $"{{\"ChildTableId\":{Emp},\"ReferenceFid\":6,\"Function\":\"Sum\",\"TargetFid\":7,\"TargetTypeCode\":\"Number\"}}"
        };
        var lookup = new Dictionary<long, PowerBase.Domain.Entities.AppField> { [8] = MaterializedReadFields.StandIn(total)! };
        var orderBy = PowerBase.Infrastructure.Repositories.RecordRepository.BuildReportOrderBy(
            [new PowerBase.Application.Reports.SortSpec { FieldId = 8, Desc = true }, new PowerBase.Application.Reports.SortSpec { FieldId = 3 }],
            new Dictionary<long, PowerBase.Domain.Entities.AppField>(lookup) { [3] = new() { Id = -3, Fid = 3, IsSystem = true, PhysicalColumnName = "Id", TypeCode = "Number" } }, null);
        var p = new DynamicParameters();
        var where = PowerBase.Infrastructure.Repositories.RecordRepository.BuildFilterTreeWhere(new PowerBase.Application.Reports.FilterGroup
        {
            Nodes = [new() { Condition = new() { FieldId = 8, Operator = "gt", Value = "20000" } }]
        }, p, lookup);

        await using var conn = await OpenAsync();
        var viaStored = (await conn.QueryAsync<long>($"SELECT Id FROM data.t_{Dept} WHERE IsDeleted = 0{where} ORDER BY {orderBy}", p)).ToList();
        var truth = (await conn.QueryAsync<long>($"""
            SELECT d.Id FROM data.t_{Dept} d
            CROSS APPLY (SELECT SUM(e.f_7) AS s FROM data.t_{Emp} e WHERE e.f_6 = d.Id AND e.IsDeleted = 0) a
            WHERE d.IsDeleted = 0 AND a.s > 20000 ORDER BY a.s DESC, d.Id
            """)).ToList();

        viaStored.Should().NotBeEmpty().And.Equal(truth);
    }

    [Fact]
    public async Task Switching_compute_off_for_the_tenant_stops_every_stored_read_at_once()
    {
        var (builder, _) = Worker();
        await builder.BuildPendingAsync(_options, CancellationToken.None);
        (await FieldState(computeOn: true).ReadyFieldIdsAsync()).Should().NotBeEmpty();
        (await FieldState(computeOn: false).ReadyFieldIdsAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task Changing_what_a_summary_reads_sends_it_back_to_be_rebuilt()
    {
        var (builder, _) = Worker();
        await builder.BuildPendingAsync(_options, CancellationToken.None);
        var before = await StateAsync(_totalId);

        await using var conn = await OpenAsync();
        var salaryFieldId = await conn.ExecuteScalarAsync<long>($"SELECT Id FROM meta.AppField WHERE AppTableId = {Emp} AND Fid = 7");
        await FieldState().InvalidateAsync([salaryFieldId], null);

        var after = await StateAsync(_totalId);
        after.State.Should().Be(MaterializeStates.None, "Total Salary reads Salary: back to not built");
        after.Version.Should().Be(before.Version + 1);
        (await StateAsync(_countId)).State.Should().Be(MaterializeStates.Ready, "Head Count does not read Salary");
        (await StateAsync(_nameLookupId)).State.Should().Be(MaterializeStates.Ready);
        (await FieldState().ReadyFieldIdsAsync()).Should().NotContain(_totalId);

        // The worker rebuilds it.
        var rebuilt = await Worker().Builder.BuildPendingAsync(_options, CancellationToken.None);
        rebuilt.Should().ContainSingle(o => o.FieldId == _totalId && o.Outcome == "ready");
        (await WrongValuesAsync()).Should().Be((0, 0, 0));
    }

    [Fact]
    public async Task The_report_says_values_are_updating_while_work_is_waiting()
    {
        var state = FieldState();
        (await state.HasPendingWorkAsync([Dept, Emp])).Should().BeFalse();
        await Outbox().AddAsync([new ComputeChange(Emp, ComputeChangeReason.Update, RecordId: 1, ChangedFids: [7])], null);
        (await FieldState().HasPendingWorkAsync([Dept, Emp])).Should().BeTrue();
        (await FieldState().HasPendingWorkAsync([Dept])).Should().BeFalse();
    }

    [Fact]
    public async Task At_most_the_configured_number_of_worker_sessions_use_one_database()
    {
        var (factory, _) = Tenant();
        await using var first = await DatabaseGuard.AcquireAsync(factory.CreateAsync, _options, false, TimeSpan.Zero, null, CancellationToken.None);
        await using var second = await DatabaseGuard.AcquireAsync(factory.CreateAsync, _options, false, TimeSpan.Zero, null, CancellationToken.None);
        await using var third = await DatabaseGuard.AcquireAsync(factory.CreateAsync, _options, false, TimeSpan.FromSeconds(1), null, CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        third.Should().BeNull();
    }

    [Fact]
    public async Task Background_work_waits_for_the_background_slot_so_live_changes_keep_one()
    {
        var (factory, _) = Tenant();
        await using var background = await DatabaseGuard.AcquireAsync(factory.CreateAsync, _options, true, TimeSpan.Zero, null, CancellationToken.None);
        await using var otherBackground = await DatabaseGuard.AcquireAsync(factory.CreateAsync, _options, true, TimeSpan.Zero, null, CancellationToken.None);
        await using var live = await DatabaseGuard.AcquireAsync(factory.CreateAsync, _options, false, TimeSpan.Zero, null, CancellationToken.None);

        background.Should().NotBeNull();
        otherBackground.Should().BeNull();
        live.Should().NotBeNull();
    }
}
