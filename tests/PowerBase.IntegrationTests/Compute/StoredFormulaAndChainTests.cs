using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Compute;
using PowerBase.Application.Imports;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Infrastructure.Compute;
using PowerBase.Infrastructure.Imports;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.IntegrationTests.Compute;

/// <summary>
/// Phase 5 against a real SQL Server. Departments (20) &lt;- Employees (21) &lt;- Tasks (22), the SumOfSum layout: Employee "Sum of Hours"
/// (Summary of Task Hours) and Department "Sum of Sum of Hours" (Summary of that Summary, a chain); Employee formulas "Annual" =
/// [Salary] * 12, "Double hours" = [Sum of Hours] * 2 (reads a stored field) and a unique "Full name" = [First] &amp; " " &amp; [Last].
/// </summary>
public class StoredFormulaAndChainTests : IClassFixture<ComputeSqlFixture>, IAsyncLifetime
{
    private const long Dept = 20, Emp = 21, Task_ = 22;
    private readonly ComputeSqlFixture _db;
    private readonly long _tenantId = Random.Shared.NextInt64(1_000_000, long.MaxValue);
    private readonly ComputeOptions _options = new() { PauseBetweenBatchesMs = 0, SummaryBatchSize = 100, LookupBatchSize = 100 };
    private long _sumOfHours, _sumOfSum, _annual, _doubleHours, _fullName;

    public StoredFormulaAndChainTests(ComputeSqlFixture db) => _db = db;

    private sealed class Factory(string cs) : ITenantConnectionFactory
    {
        public Task<SqlConnection> CreateAsync(CancellationToken ct = default) => Task.FromResult(new SqlConnection(cs));
    }

    private (Factory Factory, IQueryContext Context, IComputeSettings Settings) Tenant()
    {
        var context = Substitute.For<IQueryContext>();
        context.TenantId.Returns(_tenantId);
        var settings = Substitute.For<IComputeSettings>();
        settings.IsComputeEnabledAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);
        return (new Factory(_db.ConnectionString), context, settings);
    }

    private (MaterializeBuilder Builder, ComputeTableProcessor Processor) Worker(ComputeSearchNotifier? search = null)
    {
        var (factory, context, _) = Tenant();
        var cache = new ComputeModelCache();
        var processor = new ComputeTableProcessor(factory, context, cache, null, search);
        return (new MaterializeBuilder(factory, context, new SchemaEngineService(factory), cache, processor, null, search), processor);
    }

    private ComputeOutboxRepository Outbox()
    {
        var (factory, context, settings) = Tenant();
        return new ComputeOutboxRepository(factory, context, settings);
    }

    private MaterializedFieldStateRepository FieldState()
    {
        var (factory, context, settings) = Tenant();
        return new MaterializedFieldStateRepository(factory, context, settings, new MemoryCache(new MemoryCacheOptions()));
    }

    private RecordRepository Records()
    {
        var (factory, context, _) = Tenant();
        return new RecordRepository(factory, context, Substitute.For<IMessagePublisher>(), Substitute.For<IEncryptionService>(),
            Substitute.For<IControlConnectionFactory>(), materialized: FieldState());
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
            IF COL_LENGTH('meta.App', 'SecurityOptions') IS NULL ALTER TABLE meta.App ADD SecurityOptions NVARCHAR(MAX) NULL;
            IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Id = 6) INSERT INTO core.FieldType (Id, Code) VALUES (6, 'Formula_Number');
            IF NOT EXISTS (SELECT 1 FROM core.FieldType WHERE Id = 7) INSERT INTO core.FieldType (Id, Code) VALUES (7, 'Formula_Text');
            IF OBJECT_ID('data.t_{Dept}') IS NOT NULL DROP TABLE data.t_{Dept};
            IF OBJECT_ID('data.t_{Emp}') IS NOT NULL DROP TABLE data.t_{Emp};
            IF OBJECT_ID('data.t_{Task_}') IS NOT NULL DROP TABLE data.t_{Task_};
            DELETE FROM meta.AppField WHERE AppTableId IN ({Dept}, {Emp}, {Task_});
            DELETE FROM meta.AppTable WHERE Id IN ({Dept}, {Emp}, {Task_});
            DELETE FROM meta.App WHERE Id = 2;
            DELETE FROM meta.ComputeOutbox;
            INSERT INTO meta.App (Id) VALUES (2);
            INSERT INTO meta.AppTable (Id, AppId) VALUES ({Dept}, 2), ({Emp}, 2), ({Task_}, 2);
            """);
        foreach (var t in new[] { Dept, Emp, Task_ })
            await conn.ExecuteAsync($"""
                CREATE TABLE data.t_{t} (Id BIGINT IDENTITY PRIMARY KEY, PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(), IsDeleted BIT NOT NULL DEFAULT 0,
                    CreatedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(), CreatedBy BIGINT NOT NULL DEFAULT 0, ModifiedOn DATETIME2(3) NULL, ModifiedBy BIGINT NULL,
                    f_6 {(t == Dept ? "NVARCHAR(MAX)" : "BIGINT")} NULL, f_7 DECIMAL(18,4) NULL, f_8 DECIMAL(18,4) NULL, f_9 NVARCHAR(MAX) NULL, f_10 NVARCHAR(MAX) NULL);
                {(t == Dept ? "" : $"CREATE INDEX IX_t_{t}_f_6 ON data.t_{t}(f_6) WHERE IsDeleted = 0;")}
                """);

        await conn.ExecuteAsync($"""
            INSERT INTO meta.AppField (AppTableId, FieldTypeId, Fid, Name) VALUES
                ({Dept}, 1, 6, 'Name'),
                ({Emp}, 3, 6, 'Department'), ({Emp}, 2, 7, 'Salary'), ({Emp}, 1, 9, 'First'), ({Emp}, 1, 10, 'Last'),
                ({Task_}, 3, 6, 'Employee'), ({Task_}, 2, 8, 'Hours');
            """);
        _sumOfHours = await Add(conn, Emp, 5, 11, "Sum of Hours", $"{{\"ChildTableId\":{Task_},\"ReferenceFid\":6,\"Function\":\"Sum\",\"TargetFid\":8,\"TargetTypeCode\":\"Number\",\"ResultTypeCode\":\"Number\"}}");
        _sumOfSum = await Add(conn, Dept, 5, 8, "Sum of Sum of Hours", $"{{\"ChildTableId\":{Emp},\"ReferenceFid\":6,\"Function\":\"Sum\",\"TargetFid\":11,\"TargetTypeCode\":\"Number\",\"ResultTypeCode\":\"Number\"}}");
        _annual = await Add(conn, Emp, 6, 12, "Annual", "{\"Expression\":\"[Salary] * 12\"}");
        _doubleHours = await Add(conn, Emp, 6, 14, "Double hours", "{\"Expression\":\"[Sum of Hours] * 2\"}");
        _fullName = await Add(conn, Emp, 7, 13, "Full name", "{\"Expression\":\"[First] & \\\" \\\" & [Last]\"}", unique: true);

        await conn.ExecuteAsync($"""
            ;WITH n AS (SELECT TOP (30) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects)
            INSERT INTO data.t_{Dept} (f_6) SELECT CONCAT(N'Dept ', i) FROM n;
            ;WITH n AS (SELECT TOP (300) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects)
            INSERT INTO data.t_{Emp} (f_6, f_7, f_9, f_10) SELECT (i % 30) + 1, i * 100, CONCAT(N'Emp', i), N'Smith' FROM n;
            ;WITH n AS (SELECT TOP (1200) ROW_NUMBER() OVER (ORDER BY (SELECT 1)) AS i FROM sys.all_objects a CROSS JOIN sys.all_objects b)
            INSERT INTO data.t_{Task_} (f_6, f_8) SELECT (i % 300) + 1, (i % 7) + 1 FROM n;
            UPDATE meta.AppField SET MaterializeMode = 1 WHERE Id IN ({_sumOfHours}, {_sumOfSum}, {_annual}, {_doubleHours}, {_fullName});
            """);
    }

    private static Task<long> Add(SqlConnection conn, long table, int type, int fid, string name, string settings, bool unique = false) =>
        conn.ExecuteScalarAsync<long>("INSERT INTO meta.AppField (AppTableId, FieldTypeId, Fid, Name, Settings, IsUnique) OUTPUT inserted.Id VALUES (@table, @type, @fid, @name, @settings, @unique)",
            new { table, type, fid, name, settings, unique });

    public Task DisposeAsync() => Task.CompletedTask;

    /// <summary>Stored values that differ from the truth computed straight from the data (all zero = correct).</summary>
    private async Task<int[]> WrongAsync()
    {
        await using var conn = await OpenAsync();
        return
        [
            await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM data.t_{Emp} e OUTER APPLY (SELECT SUM(t.f_8) s FROM data.t_{Task_} t WHERE t.f_6 = e.Id AND t.IsDeleted = 0) a WHERE ISNULL(e.c_11, -1) <> ISNULL(a.s, -1)"),
            await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM data.t_{Dept} d OUTER APPLY (SELECT SUM(t.f_8) s FROM data.t_{Emp} e JOIN data.t_{Task_} t ON t.f_6 = e.Id AND t.IsDeleted = 0 WHERE e.f_6 = d.Id AND e.IsDeleted = 0) a WHERE ISNULL(d.c_8, -1) <> ISNULL(a.s, -1)"),
            await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM data.t_{Emp} WHERE ISNULL(c_12, -1) <> ISNULL(f_7 * 12, -1)"),
            await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM data.t_{Emp} WHERE ISNULL(c_14, -1) <> ISNULL(c_11 * 2, -1)"),
            await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM data.t_{Emp} WHERE ISNULL(c_13, N'~') <> ISNULL(LTRIM(RTRIM(CONCAT(f_9, N' ', f_10))), N'~')"),
        ];
    }

    private async Task<byte> StateOf(long fieldId)
    {
        await using var conn = await OpenAsync();
        return await conn.ExecuteScalarAsync<byte>("SELECT MaterializeState FROM meta.AppField WHERE Id = @fieldId", new { fieldId });
    }

    [Fact]
    public async Task A_summary_of_a_summary_and_formulas_build_in_one_round_and_are_exact()
    {
        var outcomes = await Worker().Builder.BuildPendingAsync(_options, CancellationToken.None);

        outcomes.Where(o => o.Outcome == "ready").Select(o => o.FieldId)
            .Should().BeEquivalentTo([_sumOfHours, _sumOfSum, _annual, _doubleHours, _fullName]);
        (await WrongAsync()).Should().AllBeEquivalentTo(0);
        await using var conn = await OpenAsync();
        (await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM sys.indexes WHERE name = 'UX_t_{Emp}_c_13'")).Should().Be(1, "the unique formula is enforced by an index");
    }

    [Fact]
    public async Task A_task_change_flows_through_the_chain_to_the_department_and_the_formula()
    {
        var (builder, processor) = Worker();
        await builder.BuildPendingAsync(_options, CancellationToken.None);

        await using var conn = await OpenAsync();
        await conn.ExecuteAsync($"UPDATE data.t_{Task_} SET f_8 = 500 WHERE Id = 7");
        await Outbox().AddAsync([new ComputeChange(Task_, ComputeChangeReason.Update, RecordId: 7, ChangedFids: [8])], null);
        (await WrongAsync())[0].Should().Be(1);

        var first = await processor.DrainAsync(Task_, _options, CancellationToken.None);
        first.TablesToNotify.Should().Contain(Emp, "Employee \"Sum of Hours\" changed, and stored fields read it");
        (await WrongAsync())[0].Should().Be(0);

        await processor.DrainAsync(Emp, _options, CancellationToken.None);   // the queue message for Employees
        (await WrongAsync()).Should().AllBeEquivalentTo(0, "the department total and the formula followed");
    }

    [Fact]
    public async Task A_salary_change_recomputes_the_formula_live()
    {
        var (builder, processor) = Worker();
        await builder.BuildPendingAsync(_options, CancellationToken.None);
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync($"UPDATE data.t_{Emp} SET f_7 = 999 WHERE Id = 3");
        await Outbox().AddAsync([new ComputeChange(Emp, ComputeChangeReason.Update, RecordId: 3, ChangedFids: [7])], null);
        await processor.DrainAsync(Emp, _options, CancellationToken.None);
        (await conn.ExecuteScalarAsync<decimal>($"SELECT c_12 FROM data.t_{Emp} WHERE Id = 3")).Should().Be(11988m);
    }

    [Fact]
    public async Task A_duplicate_unique_formula_value_is_refused_in_the_save_and_nothing_is_kept()
    {
        await Worker().Builder.BuildPendingAsync(_options, CancellationToken.None);
        var (factory, context, _) = Tenant();
        var fields = (await new ComputeSchemaRepository(factory, context).LoadAsync()).FieldsOf(Emp);
        var table = new AppTable { Id = Emp, AppId = 2 };
        var repo = Records();

        // "Emp5 Smith" exists already.
        var duplicate = () => repo.CreateAsync(table, fields, new Dictionary<long, object?> { [9] = "Emp5", [10] = "Smith", [6] = 1L });
        await duplicate.Should().ThrowAsync<ConflictException>().WithMessage("*Full name*must be unique*");
        await using var conn = await OpenAsync();
        (await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM data.t_{Emp}")).Should().Be(300, "the refused record was rolled back with its value");

        // A new name is saved together with its stored value; trailing spaces do not make a different value.
        var id = await repo.CreateAsync(table, fields, new Dictionary<long, object?> { [9] = "New", [10] = "Person ", [6] = 1L });
        (await conn.ExecuteScalarAsync<string>($"SELECT c_13 FROM data.t_{Emp} WHERE PublicId = @id", new { id })).Should().Be("New Person");
        var sameAfterTrim = () => repo.CreateAsync(table, fields, new Dictionary<long, object?> { [9] = "New", [10] = "Person", [6] = 1L });
        await sameAfterTrim.Should().ThrowAsync<ConflictException>();

        // Renaming an existing record into a taken name is refused too, and the record keeps its old name.
        var emp7 = await conn.ExecuteScalarAsync<Guid>($"SELECT PublicId FROM data.t_{Emp} WHERE Id = 7");
        var rename = () => repo.UpdateAsync(table, fields, emp7, new Dictionary<long, object?> { [9] = "Emp8" });
        await rename.Should().ThrowAsync<ConflictException>();
        (await conn.ExecuteScalarAsync<string>($"SELECT f_9 FROM data.t_{Emp} WHERE Id = 7")).Should().Be("Emp7");
    }

    [Fact]
    public async Task Existing_duplicates_refuse_the_unique_build_and_the_field_is_failed()
    {
        await using var conn = await OpenAsync();
        await conn.ExecuteAsync($"UPDATE data.t_{Emp} SET f_9 = N'Emp1' WHERE Id = 2");   // "Emp1 Smith" twice
        var outcomes = await Worker().Builder.BuildPendingAsync(_options, CancellationToken.None);

        var unique = outcomes.Single(o => o.FieldId == _fullName);
        unique.Outcome.Should().Be("failed");
        unique.Reason.Should().Contain("Emp1 Smith");
        (await StateOf(_fullName)).Should().Be(MaterializeStates.Failed);
        (await StateOf(_annual)).Should().Be(MaterializeStates.Ready, "other fields are not affected");
    }

    [Fact]
    public async Task An_import_into_a_table_with_a_unique_formula_is_refused()
    {
        await Worker().Builder.BuildPendingAsync(_options, CancellationToken.None);
        var (factory, context, _) = Tenant();
        var fields = (await new ComputeSchemaRepository(factory, context).LoadAsync()).FieldsOf(Emp);
        var store = new ImportDataStore(factory, context, Substitute.For<IEncryptionService>(), Substitute.For<IMessagePublisher>(),
            NullLogger<ImportDataStore>.Instance, materialized: FieldState());

        var act = () => store.InsertAsync(new AppTable { Id = Emp, AppId = 2 }, fields, [new Dictionary<long, object?> { [9] = "Imp", [10] = "Ort" }], 1);
        await act.Should().ThrowAsync<ImportRowRejectedException>().WithMessage("*Full name*unique formula*");
    }

    [Fact]
    public async Task Changed_stored_values_of_a_searchable_field_are_sent_to_the_search_index()
    {
        await using (var conn = await OpenAsync())
            await conn.ExecuteAsync("UPDATE meta.AppField SET IsSearchable = 1 WHERE Id = @id", new { id = _annual });
        var publisher = Substitute.For<IMessagePublisher>();
        var (factory, context, _) = Tenant();
        var search = new ComputeSearchNotifier(factory, context, publisher);
        var (builder, processor) = Worker(search);
        await builder.BuildPendingAsync(_options, CancellationToken.None);
        publisher.ClearReceivedCalls();

        await using var c = await OpenAsync();
        await c.ExecuteAsync($"UPDATE data.t_{Emp} SET f_7 = 1 WHERE Id IN (3, 4)");
        await Outbox().AddAsync([new ComputeChange(Emp, ComputeChangeReason.MassUpdate, RecordId: 3, ChangedFids: [7]),
                                 new ComputeChange(Emp, ComputeChangeReason.MassUpdate, RecordId: 4, ChangedFids: [7])], null);
        await processor.DrainAsync(Emp, _options, CancellationToken.None);

        await publisher.Received().PublishBatchAsync(Arg.Is<IEnumerable<SearchIndexMessage>>(m => m.Count() == 2 && m.All(x => x.Action == IndexAction.Upsert && x.TableId == Emp)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_formula_saved_empty_and_filled_in_later_is_decided_again_at_once_with_the_summary_that_reads_it()
    {
        await using var conn = await OpenAsync();
        // The formula is created before its expression is typed (the field page saves it empty first); a summary sums it per department.
        await conn.ExecuteAsync("UPDATE meta.AppField SET Settings = N'{}' WHERE Id = @_annual", new { _annual });
        var sumOfAnnual = await Add(conn, Dept, 5, 9, "Sum of Annual",
            $"{{\"ChildTableId\":{Emp},\"ReferenceFid\":6,\"Function\":\"Sum\",\"TargetFid\":12,\"TargetTypeCode\":\"Formula_Number\",\"ResultTypeCode\":\"Number\"}}");
        await conn.ExecuteAsync("UPDATE meta.AppField SET MaterializeMode = 1 WHERE Id = @sumOfAnnual", new { sumOfAnnual });

        await Worker().Builder.BuildPendingAsync(_options, CancellationToken.None);
        async Task<byte> ModeOf(long id) => await conn.ExecuteScalarAsync<byte>("SELECT MaterializeMode FROM meta.AppField WHERE Id = @id", new { id });
        (await ModeOf(_annual)).Should().Be(MaterializeModes.NotAllowed, "no expression yet");
        (await ModeOf(sumOfAnnual)).Should().Be(MaterializeModes.NotAllowed, "it sums a formula that is not stored");

        // The expression is saved: the field change (AppFieldRepository → InvalidateAsync) sends both back to be decided, without waiting a night.
        await conn.ExecuteAsync("UPDATE meta.AppField SET Settings = N'{\"Expression\":\"[Salary] * 12\"}' WHERE Id = @_annual", new { _annual });
        await FieldState().InvalidateAsync([_annual], null);
        (await ModeOf(_annual)).Should().Be(MaterializeModes.On);
        (await ModeOf(sumOfAnnual)).Should().Be(MaterializeModes.On);
        (await ModeOf(_fullName)).Should().Be(MaterializeModes.On, "fields that were not refused are not touched by the re-open");

        var outcomes = await Worker().Builder.BuildPendingAsync(_options, CancellationToken.None);
        outcomes.Where(o => o.Outcome == "ready").Select(o => o.FieldId).Should().Contain([_annual, sumOfAnnual]);
        (await WrongAsync())[2].Should().Be(0, "Annual = Salary × 12 on every employee");
        (await conn.ExecuteScalarAsync<int>($"""
            SELECT COUNT(*) FROM data.t_{Dept} d OUTER APPLY (SELECT SUM(e.f_7 * 12) s FROM data.t_{Emp} e WHERE e.f_6 = d.Id AND e.IsDeleted = 0) a
            WHERE ISNULL(d.c_9, -1) <> ISNULL(a.s, -1)
            """)).Should().Be(0, "Sum of Annual matches the data on every department");

        // A field the rules still refuse is refused again right away (no build, no column).
        await conn.ExecuteAsync("UPDATE meta.AppField SET Settings = N'{\"Expression\":\"Today()\"}' WHERE Id = @_annual", new { _annual });
        await FieldState().InvalidateAsync([_annual], null);
        await Worker().Builder.BuildPendingAsync(_options, CancellationToken.None);
        (await ModeOf(_annual)).Should().Be(MaterializeModes.NotAllowed);
    }

    // ---- Design section 13.3 edge cases ------------------------------------------------------------------------------------------

    [Fact]
    public async Task Two_saves_of_the_same_unique_value_at_the_same_moment_keep_exactly_one()
    {
        // Azure SQL turns READ_COMMITTED_SNAPSHOT on by default (design section 10.1); the test database is a plain one, so mirror it.
        await using (var admin = await OpenAsync())
            await admin.ExecuteAsync("ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT ON WITH ROLLBACK IMMEDIATE");
        await TwoSavesOfTheSameUniqueValueAsync();
    }

    [Fact]
    public async Task Two_saves_of_the_same_unique_value_at_the_same_moment_without_read_committed_snapshot()
    {
        // A local / on-prem SQL Server has the snapshot off: the saves' own SELECT must not deadlock with each other (SQL 1205);
        // the loser gets the "must be unique" message.
        await using (var admin = await OpenAsync())
            await admin.ExecuteAsync("ALTER DATABASE CURRENT SET READ_COMMITTED_SNAPSHOT OFF WITH ROLLBACK IMMEDIATE");
        await TwoSavesOfTheSameUniqueValueAsync();
    }

    private async Task TwoSavesOfTheSameUniqueValueAsync()
    {
        await Worker().Builder.BuildPendingAsync(_options, CancellationToken.None);
        var (factory, context, _) = Tenant();
        var fields = (await new ComputeSchemaRepository(factory, context).LoadAsync()).FieldsOf(Emp);
        var table = new AppTable { Id = Emp, AppId = 2 };

        const int rounds = 15;
        for (var round = 0; round < rounds; round++)
        {
            var first = $"Race{round}";
            var go = new TaskCompletionSource();
            async Task<bool> Save()
            {
                await go.Task;
                try
                {
                    await Records().CreateAsync(table, fields, new Dictionary<long, object?> { [9] = first, [10] = "Person", [6] = 1L });
                    return true;
                }
                catch (ConflictException) { return false; }   // the validation message; any other exception fails the test
            }
            var a = Task.Run(Save);
            var b = Task.Run(Save);
            go.SetResult();
            (await Task.WhenAll(a, b)).Count(saved => saved).Should().Be(1, $"round {round}: two users saved '{first} Person' at once, exactly one may win");
        }

        await using var conn = await OpenAsync();
        (await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM data.t_{Emp} WHERE c_13 LIKE N'Race% Person'")).Should().Be(rounds);
        (await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM (SELECT c_13 FROM data.t_{Emp} WHERE IsDeleted = 0 AND c_13 IS NOT NULL GROUP BY c_13 HAVING COUNT(*) > 1) d"))
            .Should().Be(0, "the table holds no duplicate of the unique formula");
    }

    [Fact]
    public async Task A_formula_that_changes_its_result_type_is_rebuilt_in_the_new_type_and_back()
    {
        await Worker().Builder.BuildPendingAsync(_options, CancellationToken.None);
        await using var conn = await OpenAsync();
        Task<string?> TypeOf() => conn.ExecuteScalarAsync<string?>($"SELECT TYPE_NAME(user_type_id) FROM sys.columns WHERE object_id = OBJECT_ID('data.t_{Emp}') AND name = 'c_12'");
        (await TypeOf()).Should().Be("decimal");

        // Number -> Text: [Salary] * 12 becomes First & "!".
        await conn.ExecuteAsync("UPDATE meta.AppField SET FieldTypeId = 7, Settings = N'{\"Expression\":\"[First] & \\\"!\\\"\"}' WHERE Id = @_annual", new { _annual });
        await FieldState().InvalidateAsync([_annual], null);
        (await StateOf(_annual)).Should().Be(MaterializeStates.None, "back to be rebuilt; reads use the dynamic path meanwhile");

        var toText = await Worker().Builder.BuildPendingAsync(_options, CancellationToken.None);
        toText.Should().Contain(o => o.FieldId == _annual && o.Outcome == "ready");
        (await TypeOf()).Should().Be("nvarchar", "the column was replaced by one of the new type");
        (await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM data.t_{Emp} WHERE ISNULL(c_12, N'~') <> ISNULL(CONCAT(f_9, N'!'), N'~')")).Should().Be(0);

        // Text -> Number again.
        await conn.ExecuteAsync("UPDATE meta.AppField SET FieldTypeId = 6, Settings = N'{\"Expression\":\"[Salary] * 12\"}' WHERE Id = @_annual", new { _annual });
        await FieldState().InvalidateAsync([_annual], null);
        var toNumber = await Worker().Builder.BuildPendingAsync(_options, CancellationToken.None);
        toNumber.Should().Contain(o => o.FieldId == _annual && o.Outcome == "ready");
        (await TypeOf()).Should().Be("decimal");
        (await WrongAsync()).Should().AllBeEquivalentTo(0);
    }

    [Fact]
    public async Task A_deleted_field_and_a_field_that_turns_not_allowed_lose_their_stored_column_and_its_recorded_name()
    {
        await Worker().Builder.BuildPendingAsync(_options, CancellationToken.None);
        await using var conn = await OpenAsync();
        Task<bool> HasColumn(int fid) => conn.ExecuteScalarAsync<int?>($"SELECT COL_LENGTH('data.t_{Emp}', 'c_{fid}')").ContinueWith(t => t.Result is not null);
        Task<string?> NameOf(long id) => conn.ExecuteScalarAsync<string?>("SELECT MaterializedColumnName FROM meta.AppField WHERE Id = @id", new { id });
        (await HasColumn(12)).Should().BeTrue();
        (await HasColumn(14)).Should().BeTrue();
        (await NameOf(_annual)).Should().Be("c_12");

        // "Annual" is deleted; "Double hours" gets a Today() in it (the rules refuse it).
        await conn.ExecuteAsync("UPDATE meta.AppField SET IsDeleted = 1 WHERE Id = @_annual", new { _annual });
        await conn.ExecuteAsync("UPDATE meta.AppField SET Settings = N'{\"Expression\":\"Today()\"}' WHERE Id = @_doubleHours", new { _doubleHours });
        await FieldState().InvalidateAsync([_doubleHours], null);
        await Worker().Builder.BuildPendingAsync(_options, CancellationToken.None);

        (await HasColumn(12)).Should().BeFalse("a deleted field's stored column is dropped");
        (await HasColumn(14)).Should().BeFalse("a field the rules refuse keeps no old values");
        (await NameOf(_annual)).Should().BeNull();
        (await NameOf(_doubleHours)).Should().BeNull();
        (await HasColumn(13)).Should().BeTrue("fields that are still stored are not touched");
        (await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM data.t_{Emp} WHERE ISNULL(c_13, N'~') <> ISNULL(LTRIM(RTRIM(CONCAT(f_9, N' ', f_10))), N'~')")).Should().Be(0);
    }

    [Fact]
    public async Task A_field_whose_source_is_switched_to_encrypted_stays_stored_and_is_rebuilt()
    {
        await Worker().Builder.BuildPendingAsync(_options, CancellationToken.None);
        await using var conn = await OpenAsync();
        (await conn.ExecuteScalarAsync<int>($"SELECT COUNT(c_12) FROM data.t_{Emp}")).Should().BeGreaterThan(0, "Annual = Salary x 12 is stored in plain text");

        // The Salary field is switched to encrypted. Stored values of encrypted data are kept PLAIN in c_ (that is what makes them sortable),
        // so Annual stays stored: its column is rebuilt for the new definition and holds the same exact values.
        var salary = await conn.ExecuteScalarAsync<long>($"SELECT Id FROM meta.AppField WHERE AppTableId = {Emp} AND Fid = 7");
        await conn.ExecuteAsync("UPDATE meta.AppField SET IsEncrypted = 1 WHERE Id = @salary", new { salary });
        await FieldState().InvalidateAsync([salary], null);
        await Worker().Builder.BuildPendingAsync(_options, CancellationToken.None);

        (await conn.ExecuteScalarAsync<byte>("SELECT MaterializeMode FROM meta.AppField WHERE Id = @_annual", new { _annual })).Should().Be(MaterializeModes.On);
        (await conn.ExecuteScalarAsync<byte>("SELECT MaterializeState FROM meta.AppField WHERE Id = @_annual", new { _annual })).Should().Be(MaterializeStates.Ready);
        (await conn.ExecuteScalarAsync<int>($"SELECT COUNT(*) FROM data.t_{Emp} WHERE IsDeleted = 0 AND ISNULL(c_12, -1) <> ISNULL(f_7 * 12, -1)"))
            .Should().Be(0, "a Number is never encrypted in its column, so Annual is still Salary x 12");
    }
}
