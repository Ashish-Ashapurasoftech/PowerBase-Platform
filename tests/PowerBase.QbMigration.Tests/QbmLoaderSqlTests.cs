using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.QbMigration;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.QbMigration;
using static PowerBase.QbMigration.Tests.TestData;

namespace PowerBase.QbMigration.Tests;

/// <summary>Runs only when a SQL Server is reachable (QBM_TEST_SQL, or the local default instance). Works in tempdb with
/// throw-away tables, never in a tenant database.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        try { using var c = new SqlConnection(SqlDb.ConnectionString); c.Open(); }
        catch (Exception) { Skip = "No SQL Server available."; }
    }
}

public static class SqlDb
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("QBM_TEST_SQL") ?? "Server=.;Database=tempdb;Trusted_Connection=True;TrustServerCertificate=True;";

    public sealed class Factory : ITenantConnectionFactory
    {
        public Task<SqlConnection> CreateAsync(CancellationToken ct = default) => Task.FromResult(new SqlConnection(ConnectionString));
    }
}

public sealed class QbmLoaderSqlTests : IAsyncLifetime
{
    private const long PlainApp = 7_001, EncryptedApp = 7_002, MissingApp = 7_999;
    private readonly long _tableId = 910_000 + Random.Shared.Next(80_000);
    private readonly SqlDb.Factory _factory = new();
    private readonly IEncryptionService _encryption = Substitute.For<IEncryptionService>();
    private QbmStateStore _state = null!;
    private QbmChunkLoader _loader = null!;
    private string DataTable => $"data.t_{_tableId}";

    private readonly List<AppField> _fields =
    [
        .. SystemFields(),
        Pb(6, "Text", "Name"),
        Pb(7, "Number", "Qty"),
        Pb(8, "Text", "Secret"),
        Pb(9, "Date", "When"),
    ];

    public async Task InitializeAsync()
    {
        _encryption.GenerateAndWrapDekAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns("wrapped-dek");
        _encryption.EncryptDataAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(ci => $"ENC({ci.ArgAt<string>(0)})");
        _state = new QbmStateStore(_factory);
        _loader = new QbmChunkLoader(_factory, _encryption);

        await using var c = new SqlConnection(SqlDb.ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync("""
            IF SCHEMA_ID('meta') IS NULL EXEC('CREATE SCHEMA meta');
            IF SCHEMA_ID('data') IS NULL EXEC('CREATE SCHEMA data');
            IF OBJECT_ID('meta.App') IS NULL CREATE TABLE meta.App (Id BIGINT PRIMARY KEY, IsDeleted BIT NOT NULL DEFAULT 0, SecurityOptions NVARCHAR(MAX) NULL, IsEncrypted BIT NOT NULL DEFAULT 0);
            IF OBJECT_ID('meta.AppTable') IS NULL CREATE TABLE meta.AppTable (Id BIGINT PRIMARY KEY, RecordCount INT NOT NULL DEFAULT 0);
            DELETE FROM meta.App WHERE Id IN (7001, 7002);
            INSERT INTO meta.App (Id, IsEncrypted) VALUES (7001, 0), (7002, 1);
            """);
        await _state.InitAsync(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "sql", "001_qbm_state.sql")));
        // Same layout SchemaEngineService creates (encrypted text columns are VARCHAR(MAX) holding Base64).
        await c.ExecuteAsync($"""
            INSERT INTO meta.AppTable (Id) VALUES ({_tableId});
            CREATE TABLE {DataTable} (
                Id BIGINT IDENTITY(1,1) NOT NULL, PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(), IsDeleted BIT NOT NULL DEFAULT 0,
                CreatedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(), CreatedBy BIGINT NOT NULL DEFAULT 0, ModifiedOn DATETIME2(3) NULL,
                ModifiedBy BIGINT NULL, RowVersion ROWVERSION NOT NULL, CONSTRAINT PK_t_{_tableId} PRIMARY KEY CLUSTERED (Id),
                f_6 NVARCHAR(MAX) NULL, f_7 DECIMAL(18,4) NULL, f_8 VARCHAR(MAX) NULL, f_9 DATE NULL);
            """);
    }

    public async Task DisposeAsync()
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        await c.ExecuteAsync($"""
            DROP TABLE IF EXISTS {DataTable};
            DELETE FROM meta.AppTable WHERE Id = {_tableId};
            DELETE FROM qbm.Chunk WHERE TableMapId = {_tableId};
            DELETE FROM meta.App WHERE Id IN (7001, 7002);
            """);
    }

    private AppTable Table(long appId) => new() { Id = _tableId, AppId = appId, Name = "T" };

    private async Task<long> ChunkAsync(long from, long to)
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        // TableMapId = the test table id keeps each test's ledger rows apart.
        return await c.ExecuteScalarAsync<long>(
            "INSERT INTO qbm.Chunk (RunId, TableMapId, FromRecordId, ToRecordId) OUTPUT INSERTED.Id VALUES (1, @t, @from, @to)",
            new { t = _tableId, from, to });
    }

    private static QbTransformedRow R(long id, string? name, decimal? qty = null, string? secret = null, DateTime? when = null) =>
        new(id, new DateTime(2020, 1, 1, 8, 0, 0), new DateTime(2021, 6, 1, 9, 30, 0), 41, 42,
            new Dictionary<long, object?> { [6] = name, [7] = qty, [8] = secret, [9] = when });

    private async Task<List<dynamic>> RowsAsync()
    {
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        return (await c.QueryAsync($"SELECT Id, PublicId, CreatedOn, CreatedBy, ModifiedOn, ModifiedBy, f_6, f_7, f_8, f_9 FROM {DataTable} ORDER BY Id")).ToList();
    }

    [SqlFact]
    public async Task Records_keep_their_quickbase_ids_system_columns_and_values()
    {
        var chunk = await ChunkAsync(1, 100);
        await _loader.LoadAsync(1, Table(PlainApp), _fields, chunk, new QbRange(1, 100),
            [R(3, "Zürich", 1.5m, null, new DateTime(1900, 1, 1)), R(57, "B", -2m), R(99, null)]);

        var rows = await RowsAsync();
        ((IEnumerable<dynamic>)rows).Select(r => (long)r.Id).Should().Equal(3, 57, 99);
        var first = rows[0];
        ((string)first.f_6).Should().Be("Zürich");
        ((decimal)first.f_7).Should().Be(1.5m);
        ((DateTime)first.f_9).Should().Be(new DateTime(1900, 1, 1));
        ((long)first.CreatedBy).Should().Be(41);
        ((long)first.ModifiedBy).Should().Be(42);
        ((DateTime)first.CreatedOn).Should().Be(new DateTime(2020, 1, 1, 8, 0, 0));
        ((Guid)first.PublicId).Should().Be(QbmChunkLoader.StablePublicId(_tableId, 3));
        ((object?)rows[2].f_6).Should().BeNull();
    }

    [SqlFact]
    public async Task Replaying_a_chunk_replaces_its_rows_without_duplicates_or_new_public_ids()
    {
        var chunk = await ChunkAsync(1, 100);
        await _loader.LoadAsync(1, Table(PlainApp), _fields, chunk, new QbRange(1, 100), [R(3, "old"), R(4, "x")]);
        var before = await RowsAsync();

        // A retry after a commit whose reply was lost: same range again, now with a changed value and one record gone.
        await _loader.LoadAsync(1, Table(PlainApp), _fields, chunk, new QbRange(1, 100), [R(3, "new")]);

        var after = await RowsAsync();
        after.Should().HaveCount(1);
        ((string)after[0].f_6).Should().Be("new");
        ((Guid)after[0].PublicId).Should().Be((Guid)before[0].PublicId);
    }

    [SqlFact]
    public async Task A_chunk_never_touches_rows_outside_its_range()
    {
        var a = await ChunkAsync(1, 10);
        var b = await ChunkAsync(10, 20);
        await _loader.LoadAsync(1, Table(PlainApp), _fields, b, new QbRange(10, 20), [R(10, "b1"), R(19, "b2")]);
        await _loader.LoadAsync(1, Table(PlainApp), _fields, a, new QbRange(1, 10), [R(9, "a1")]);
        await _loader.LoadAsync(1, Table(PlainApp), _fields, a, new QbRange(1, 10), [R(9, "a1 again")]);
        (await RowsAsync()).Select(r => (long)r.Id).Should().Equal(9, 10, 19);
    }

    [SqlFact]
    public async Task Done_in_the_ledger_is_set_in_the_same_transaction_as_the_rows()
    {
        var chunk = await ChunkAsync(1, 100);
        await _loader.LoadAsync(1, Table(PlainApp), _fields, chunk, new QbRange(1, 100), [R(5, "x")]);
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        var ledger = await c.QuerySingleAsync("SELECT Status, RowsWritten, Checksum FROM qbm.Chunk WHERE Id = @chunk", new { chunk });
        ((string)ledger.Status).Should().Be("done");
        ((int)ledger.RowsWritten).Should().Be(1);
        ((byte[])ledger.Checksum).Should().Equal(QbmChunkLoader.Checksum([R(5, "x")]));
    }

    [SqlFact]
    public async Task A_chunk_missing_from_the_ledger_writes_nothing()
    {
        var act = () => _loader.LoadAsync(1, Table(PlainApp), _fields, 999_999_999, new QbRange(1, 100), [R(5, "x")]);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not in the ledger*");
        (await RowsAsync()).Should().BeEmpty();
    }

    [SqlFact]
    public async Task A_row_outside_the_range_is_refused_before_anything_is_written()
    {
        var chunk = await ChunkAsync(1, 10);
        var act = () => _loader.LoadAsync(1, Table(PlainApp), _fields, chunk, new QbRange(1, 10), [R(5, "x"), R(10, "y")]);
        await act.Should().ThrowAsync<InvalidOperationException>();
        (await RowsAsync()).Should().BeEmpty();
    }

    [SqlFact]
    public async Task In_an_encrypted_app_text_is_encrypted_and_numbers_and_dates_are_not()
    {
        var chunk = await ChunkAsync(1, 100);
        await _loader.LoadAsync(1, Table(EncryptedApp), _fields, chunk, new QbRange(1, 100), [R(5, "Name", 3m, "pin 1234", new DateTime(2020, 2, 2))]);
        var row = (await RowsAsync()).Single();
        ((string)row.f_6).Should().Be("ENC(Name)");
        ((string)row.f_8).Should().Be("ENC(pin 1234)");
        ((decimal)row.f_7).Should().Be(3m);
        ((DateTime)row.f_9).Should().Be(new DateTime(2020, 2, 2));
    }

    [SqlFact]
    public async Task A_field_marked_encrypted_is_encrypted_in_a_plain_app()
    {
        var fields = _fields.Select(f => f.Fid == 8 ? new AppField { Id = 8, Fid = 8, TypeCode = "Text", Name = "Secret", IsEncrypted = true } : f).ToList();
        var chunk = await ChunkAsync(1, 100);
        await _loader.LoadAsync(1, Table(PlainApp), fields, chunk, new QbRange(1, 100), [R(5, "Name", null, "pin")]);
        var row = (await RowsAsync()).Single();
        ((string)row.f_6).Should().Be("Name");
        ((string)row.f_8).Should().Be("ENC(pin)");
    }

    [SqlFact]
    public async Task An_unknown_app_stops_the_load_instead_of_writing_plain_text()
    {
        var chunk = await ChunkAsync(1, 100);
        var act = () => _loader.LoadAsync(1, Table(MissingApp), _fields, chunk, new QbRange(1, 100), [R(5, "x")]);
        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*does not exist*");
        (await RowsAsync()).Should().BeEmpty();
    }

    [SqlFact]
    public async Task Finalize_sets_the_record_count_and_the_next_new_record_gets_an_id_above_every_migrated_one()
    {
        var chunk = await ChunkAsync(1, 1000);
        await _loader.LoadAsync(1, Table(PlainApp), _fields, chunk, new QbRange(1, 1000), [R(3, "a"), R(500, "b"), R(999, "c")]);

        var result = await _state.FinalizeTableAsync(_tableId);
        result.Rows.Should().Be(3);
        result.MaxId.Should().Be(999);
        result.IdentityAfter.Should().BeGreaterThanOrEqualTo(999);

        await using var c = new SqlConnection(SqlDb.ConnectionString);
        var newId = await c.ExecuteScalarAsync<long>($"INSERT INTO {DataTable} (f_6) OUTPUT INSERTED.Id VALUES ('made by a person')");
        newId.Should().Be(1000);
        (await c.ExecuteScalarAsync<int>("SELECT RecordCount FROM meta.AppTable WHERE Id = @id", new { id = _tableId })).Should().Be(3);
    }

    [SqlFact]
    public async Task A_destination_with_rows_is_reported_so_the_run_can_refuse_it()
    {
        (await _state.CountRowsAsync(_tableId)).Should().Be(0);
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        await c.ExecuteAsync($"INSERT INTO {DataTable} (f_6) VALUES ('existing')");
        (await _state.CountRowsAsync(_tableId)).Should().Be(1);
    }

    [SqlFact]
    public async Task Splitting_a_chunk_halves_it_and_adds_the_second_half_as_pending()
    {
        var chunk = await ChunkAsync(100, 200);
        (await _state.SplitChunkAsync(chunk)).Should().BeTrue();
        await using var c = new SqlConnection(SqlDb.ConnectionString);
        var rows = (await c.QueryAsync<(long From, long To, string Status)>(
            "SELECT FromRecordId, ToRecordId, Status FROM qbm.Chunk WHERE TableMapId = @t ORDER BY FromRecordId", new { t = _tableId })).ToList();
        rows.Should().Equal((100L, 150L, "pending"), (150L, 200L, "pending"));
    }

    [SqlFact]
    public async Task A_done_chunk_is_not_started_again()
    {
        var chunk = await ChunkAsync(1, 100);
        await _loader.LoadAsync(1, Table(PlainApp), _fields, chunk, new QbRange(1, 100), [R(5, "x")]);
        (await _state.StartChunkAsync(chunk)).Should().BeFalse();
    }
}
