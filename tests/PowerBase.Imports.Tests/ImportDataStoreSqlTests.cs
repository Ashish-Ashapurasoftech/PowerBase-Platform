using FluentAssertions;
using Dapper;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Imports;
using PowerBase.Infrastructure.Persistence;

namespace PowerBase.Imports.Tests;

/// <summary>Runs a fact only when a SQL Server is reachable (set IMPORT_TEST_SQL, or a local default instance is used).
/// The tests work in <c>tempdb</c> with a throw-away table, never in a real tenant database.</summary>
public sealed class SqlFactAttribute : FactAttribute
{
    public SqlFactAttribute()
    {
        try { using var c = new SqlConnection(SqlFixture.ConnectionString); c.Open(); }
        catch (Exception) { Skip = "No SQL Server available for the data store tests."; }
    }
}

public sealed class SqlTheoryAttribute : TheoryAttribute
{
    public SqlTheoryAttribute()
    {
        try { using var c = new SqlConnection(SqlFixture.ConnectionString); c.Open(); }
        catch (Exception) { Skip = "No SQL Server available for the data store tests."; }
    }
}

public sealed class SqlFixture : IAsyncLifetime
{
    public static string ConnectionString =>
        Environment.GetEnvironmentVariable("IMPORT_TEST_SQL") ?? "Server=.;Database=tempdb;Trusted_Connection=True;TrustServerCertificate=True;";

    private sealed class Factory : ITenantConnectionFactory
    {
        public Task<SqlConnection> CreateAsync(CancellationToken ct = default) => Task.FromResult(new SqlConnection(ConnectionString));
    }

    // A table id no real tenant uses, so concurrent runs and leftovers cannot collide.
    public long TableId { get; } = 900_000 + Random.Shared.Next(99_000);
    public AppTable Table => new() { Id = TableId, AppId = 1, PublicId = Guid.NewGuid(), Name = "T" };
    public List<AppField> Fields { get; } =
    [
        new() { Id = 1, AppTableId = 0, Fid = 3, Name = "Record ID#", TypeCode = "Number", IsSystem = true, PhysicalColumnName = "Id" },
        new() { Id = 2, AppTableId = 0, Fid = 6, Name = "Name", Label = "Name", TypeCode = "Text", IsUnique = true },
        new() { Id = 3, AppTableId = 0, Fid = 7, Name = "Qty", Label = "Qty", TypeCode = "Number" },
        new() { Id = 4, AppTableId = 0, Fid = 9, Name = "Note", Label = "Note", TypeCode = "Text" },
    ];
    public ImportDataStore Store { get; private set; } = null!;
    private string DataTable => $"data.t_{TableId}";

    public async Task InitializeAsync()
    {
        await using var c = new SqlConnection(ConnectionString);
        await c.OpenAsync();
        // The pieces of a tenant database the store touches, created only if missing (and left in place: they are in tempdb).
        await c.ExecuteAsync("""
            IF SCHEMA_ID('meta') IS NULL EXEC('CREATE SCHEMA meta');
            IF SCHEMA_ID('data') IS NULL EXEC('CREATE SCHEMA data');
            IF OBJECT_ID('meta.App') IS NULL CREATE TABLE meta.App (Id BIGINT PRIMARY KEY, IsDeleted BIT NOT NULL DEFAULT 0, SecurityOptions NVARCHAR(MAX) NULL, IsEncrypted BIT NOT NULL DEFAULT 0);
            IF NOT EXISTS (SELECT 1 FROM meta.App WHERE Id = 1) INSERT INTO meta.App (Id) VALUES (1);
            IF OBJECT_ID('meta.AppTable') IS NULL CREATE TABLE meta.AppTable (Id BIGINT PRIMARY KEY, RecordCount INT NOT NULL DEFAULT 0);
            """);
        // Same layout SchemaEngineService creates, with the digest column and unique index it adds for a long-text unique field.
        await c.ExecuteAsync($"""
            INSERT INTO meta.AppTable (Id) VALUES ({TableId});
            CREATE TABLE {DataTable} (
                Id BIGINT IDENTITY(1,1) NOT NULL, PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWSEQUENTIALID(), IsDeleted BIT NOT NULL DEFAULT 0,
                CreatedOn DATETIME2(3) NOT NULL DEFAULT SYSUTCDATETIME(), CreatedBy BIGINT NOT NULL DEFAULT 0, ModifiedOn DATETIME2(3) NULL,
                ModifiedBy BIGINT NULL, RowVersion ROWVERSION NOT NULL, CONSTRAINT PK_t_{TableId} PRIMARY KEY CLUSTERED (Id),
                f_6 NVARCHAR(MAX) NULL, f_7 DECIMAL(18,4) NULL, f_9 NVARCHAR(MAX) NULL);
            ALTER TABLE {DataTable} ADD f_6_unique_hash AS
                (CASE WHEN f_6 IS NULL OR IsDeleted = 1 THEN HASHBYTES('SHA2_256', 0x00 + CONVERT(VARBINARY(8), Id))
                      ELSE HASHBYTES('SHA2_256', 0x01 + CONVERT(VARBINARY(MAX), f_6)) END) PERSISTED;
            CREATE UNIQUE NONCLUSTERED INDEX UX_t_{TableId}_f_6 ON {DataTable}(f_6_unique_hash);
            """);

        var context = Substitute.For<IQueryContext>();
        context.TenantId.Returns(1);
        context.UserId.Returns(5);
        Store = new ImportDataStore(new Factory(), context, Substitute.For<IEncryptionService>(), Substitute.For<IMessagePublisher>(),
            NullLogger<ImportDataStore>.Instance);
    }

    public async Task DisposeAsync()
    {
        await using var c = new SqlConnection(ConnectionString);
        await c.OpenAsync();
        await c.ExecuteAsync($"DROP TABLE IF EXISTS {DataTable}; DELETE FROM meta.AppTable WHERE Id = {TableId};");
    }

    public async Task<List<dynamic>> RowsAsync()
    {
        await using var c = new SqlConnection(ConnectionString);
        return (await c.QueryAsync($"SELECT Id, f_6, f_7, f_9, CreatedBy, ModifiedBy, IsDeleted FROM {DataTable} ORDER BY Id")).ToList();
    }

    public async Task ExecuteAsync(string sql)
    {
        await using var c = new SqlConnection(ConnectionString);
        await c.ExecuteAsync(sql.Replace("{T}", DataTable));
    }
}

/// <summary>The data store's SQL, run against a real SQL Server: bulk insert and update, the masked update that keeps a stored
/// value, lookups by digest and by Record ID#, and rollback when a row is rejected.</summary>
public class ImportDataStoreSqlTests : IClassFixture<SqlFixture>
{
    private readonly SqlFixture _db;
    public ImportDataStoreSqlTests(SqlFixture db) => _db = db;

    private static IReadOnlyDictionary<long, object?> Values(string? name, decimal? qty, string? note) =>
        new Dictionary<long, object?> { [6] = name, [7] = qty, [9] = note };

    [SqlFact]
    public async Task The_store_inserts_updates_looks_up_and_streams_correctly_in_one_scenario()
    {
        // ---- bulk insert; a field a row does not set is stored as NULL ----
        await _db.Store.InsertAsync(_db.Table, _db.Fields, new List<IReadOnlyDictionary<long, object?>>
        {
            Values("Alpha", 1.5m, "first"),
            Values("Beta", 2m, "second"),
            new Dictionary<long, object?> { [6] = "Gamma", [7] = 3m }, // no Note
        }, createdBy: 5);
        var rows = await _db.RowsAsync();
        rows.Should().HaveCount(3);
        ((object)rows[2].f_9).Should().BeNull("the third row did not set Note");
        ((long)rows[0].CreatedBy).Should().Be(5);
        var id = rows.ToDictionary(r => (string)r.f_6, r => (long)r.Id);

        // ---- lookups: by unique long-text value (digest index) and by Record ID# ----
        var byName = await _db.Store.FindRecordIdsAsync(_db.Table, _db.Fields[1], ["Alpha", "Gamma", "Missing"]);
        byName.Should().BeEquivalentTo(new Dictionary<string, long> { ["Alpha"] = id["Alpha"], ["Gamma"] = id["Gamma"] });
        var byId = await _db.Store.FindRecordIdsAsync(_db.Table, _db.Fields[0], [id["Beta"], 424242m]);
        byId.Should().BeEquivalentTo(new Dictionary<string, long> { [id["Beta"].ToString()] = id["Beta"] });

        // ---- masked update: row 1 sets Qty and Note, row 2 sets Qty only, so Beta keeps its stored Note ----
        await _db.Store.UpdateAsync(_db.Table, _db.Fields, new List<ImportUpdateRow>
        {
            new(id["Alpha"], new Dictionary<long, object?> { [7] = 10m, [9] = "changed" }),
            new(id["Beta"], new Dictionary<long, object?> { [7] = 20m }),
        }, modifiedBy: 7);
        rows = await _db.RowsAsync();
        var alpha = rows.Single(r => r.f_6 == "Alpha");
        var beta = rows.Single(r => r.f_6 == "Beta");
        ((decimal)alpha.f_7).Should().Be(10m);
        ((string)alpha.f_9).Should().Be("changed");
        ((decimal)beta.f_7).Should().Be(20m);
        ((string)beta.f_9).Should().Be("second", "a field the update leaves out keeps its stored value");
        ((long)beta.ModifiedBy).Should().Be(7);

        // ---- a rejected update changes nothing (one transaction) ----
        var clash = async () => await _db.Store.UpdateAsync(_db.Table, _db.Fields, new List<ImportUpdateRow>
        {
            new(id["Alpha"], new Dictionary<long, object?> { [6] = "NewName", [7] = 99m }),
            new(id["Beta"], new Dictionary<long, object?> { [6] = "Gamma", [7] = 99m }), // Gamma is taken
        }, modifiedBy: 7);
        await clash.Should().ThrowAsync<ImportRowRejectedException>();
        rows = await _db.RowsAsync();
        ((string)rows.Single(r => r.Id == id["Alpha"]).f_6).Should().Be("Alpha");
        ((decimal)rows.Single(r => r.Id == id["Alpha"]).f_7).Should().Be(10m);

        // ---- a rejected insert adds nothing ----
        var duplicate = async () => await _db.Store.InsertAsync(_db.Table, _db.Fields, new List<IReadOnlyDictionary<long, object?>>
        {
            Values("Delta", 4m, "x"), Values("Alpha", 5m, "dup")
        }, createdBy: 5);
        await duplicate.Should().ThrowAsync<ImportRowRejectedException>();
        (await _db.RowsAsync()).Should().HaveCount(3);

        // ---- streaming: non-blank values only, deleted rows excluded ----
        await _db.ExecuteAsync($"UPDATE {{T}} SET IsDeleted = 1 WHERE Id = {id["Gamma"]}");
        var streamed = new List<object>();
        await foreach (var v in _db.Store.StreamColumnValuesAsync(_db.Table, _db.Fields[3])) streamed.Add(v);
        streamed.Should().BeEquivalentTo(new object[] { "changed", "second" });

        // ---- misc ----
        (await _db.Store.GetMaxRecordIdAsync(_db.Table)).Should().Be(id["Beta"]);
        await _db.Store.AddRecordCountAsync(_db.TableId, 3);
        await using var c = new SqlConnection(SqlFixture.ConnectionString);
        (await c.ExecuteScalarAsync<int>("SELECT RecordCount FROM meta.AppTable WHERE Id = @id", new { id = _db.TableId })).Should().Be(3);
    }
}
