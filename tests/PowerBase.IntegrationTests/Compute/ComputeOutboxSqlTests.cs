using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Compute;
using PowerBase.Infrastructure.Compute;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Repositories;
using Testcontainers.MsSql;
using Xunit;

namespace PowerBase.IntegrationTests.Compute;

/// <summary>
/// A throwaway SQL Server with the tenant migrations of the stored-columns feature (096, 097, 098, 099). Testcontainers by default; where
/// Docker is not available, POWERBASE_TEST_SQL_CONNECTION points at a THROWAWAY server (for example LocalDB) and a uniquely named
/// database is created there and dropped afterwards (same switch as PowerBaseWebApplicationFactory).
/// </summary>
public sealed class ComputeSqlFixture : IAsyncLifetime
{
    private readonly string? _external = Environment.GetEnvironmentVariable("POWERBASE_TEST_SQL_CONNECTION");
    private MsSqlContainer? _container;
    private string? _database;

    public string ConnectionString { get; private set; } = string.Empty;

    public async Task InitializeAsync()
    {
        string server;
        if (string.IsNullOrWhiteSpace(_external))
        {
            _container = new MsSqlBuilder("mcr.microsoft.com/mssql/server:2022-latest").Build();
            await _container.StartAsync();
            server = _container.GetConnectionString();
        }
        else server = _external;

        _database = $"pbcompute_{Guid.NewGuid():N}";
        await using (var master = new SqlConnection(server))
        {
            await master.OpenAsync();
            await master.ExecuteAsync($"CREATE DATABASE [{_database}]");
        }
        ConnectionString = new SqlConnectionStringBuilder(server) { InitialCatalog = _database }.ToString();

        await using var conn = new SqlConnection(ConnectionString);
        await conn.OpenAsync();
        await conn.ExecuteAsync("EXEC('CREATE SCHEMA meta'); EXEC('CREATE SCHEMA data');");
        // The slice of meta.AppField 097 alters, and a data table with a Reference column (f_7) for the OUTPUT test.
        // The slice of the real metadata the compute worker reads (core.FieldType, meta.App, meta.AppTable, meta.AppField).
        await conn.ExecuteAsync("EXEC('CREATE SCHEMA core');");
        await conn.ExecuteAsync("""
            CREATE TABLE core.FieldType (Id INT PRIMARY KEY, Code NVARCHAR(50) NOT NULL);
            INSERT INTO core.FieldType (Id, Code) VALUES (1, 'Text'), (2, 'Number'), (3, 'Reference'), (4, 'Lookup'), (5, 'Summary');
            CREATE TABLE meta.App (Id BIGINT PRIMARY KEY, IsEncrypted BIT NOT NULL DEFAULT 0, IsDeleted BIT NOT NULL DEFAULT 0);
            CREATE TABLE meta.AppTable (Id BIGINT PRIMARY KEY, AppId BIGINT NOT NULL, IsDeleted BIT NOT NULL DEFAULT 0);
            CREATE TABLE meta.AppField (
                Id BIGINT IDENTITY PRIMARY KEY, AppTableId BIGINT NOT NULL DEFAULT 0, FieldTypeId INT NOT NULL DEFAULT 1, Fid INT NULL,
                Name NVARCHAR(100) NULL, Settings NVARCHAR(MAX) NULL, IsEncrypted BIT NOT NULL DEFAULT 0, IsSystem BIT NOT NULL DEFAULT 0,
                PhysicalColumnName NVARCHAR(128) NULL, IsDeleted BIT NOT NULL DEFAULT 0, Label NVARCHAR(200) NULL,
                IsUnique BIT NOT NULL DEFAULT 0, IsSearchable BIT NOT NULL DEFAULT 0, IsFilterable BIT NOT NULL DEFAULT 0);
            CREATE TABLE meta.Report (Id BIGINT IDENTITY PRIMARY KEY, AppTableId BIGINT NOT NULL, Definition NVARCHAR(MAX) NULL, IsDeleted BIT NOT NULL DEFAULT 0);
            CREATE TABLE meta.AppRoleRecordFilter (Id BIGINT IDENTITY PRIMARY KEY, AppRoleId BIGINT NOT NULL, AppTableId BIGINT NOT NULL,
                Conjunction NVARCHAR(10) NULL, FilterJson NVARCHAR(MAX) NULL, Kind NVARCHAR(20) NOT NULL DEFAULT 'View');
            """);
        await conn.ExecuteAsync("""
            CREATE TABLE data.t_5 (
                Id BIGINT IDENTITY PRIMARY KEY, PublicId UNIQUEIDENTIFIER NOT NULL DEFAULT NEWID(), IsDeleted BIT NOT NULL DEFAULT 0,
                ModifiedOn DATETIME2(3) NULL, ModifiedBy BIGINT NULL, f_7 BIGINT NULL, f_9 NVARCHAR(100) NULL)
            """);

        foreach (var script in StoredColumnMigrations())
            await RunScriptAsync(conn, script);
    }

    public static IEnumerable<string> StoredColumnMigrations()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "database", "migrations", "tenant"))) dir = dir.Parent;
        if (dir == null) throw new InvalidOperationException("database/migrations/tenant not found above the test output folder.");
        var tenant = Path.Combine(dir.FullName, "database", "migrations", "tenant");
        return new[] { "089_create_search_field_job.sql", "096_create_compute_outbox.sql", "097_appfield_add_materialize.sql", "098_create_replica_heartbeat.sql", "099_appfield_add_materialized_column_name.sql", "100_create_compute_build_checkpoint.sql",
                "101_appfield_add_search_index_state.sql", "103_appfield_add_search_index_requested_on.sql" }
            .Select(f => Path.Combine(tenant, f));
    }

    public static async Task RunScriptAsync(SqlConnection conn, string path)
    {
        var sql = await File.ReadAllTextAsync(path);
        foreach (var batch in Regex.Split(sql, @"^\s*GO\s*$", RegexOptions.Multiline | RegexOptions.IgnoreCase))
            if (!string.IsNullOrWhiteSpace(batch)) await conn.ExecuteAsync(batch);
    }

    public async Task DisposeAsync()
    {
        if (_container != null)
        {
            await _container.DisposeAsync();
            return;
        }
        SqlConnection.ClearAllPools();
        await using var master = new SqlConnection(_external);
        await master.OpenAsync();
        await master.ExecuteAsync($"ALTER DATABASE [{_database}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{_database}];");
    }
}

public class ComputeOutboxSqlTests : IClassFixture<ComputeSqlFixture>
{
    private readonly ComputeSqlFixture _db;
    // A tenant id of its own per test instance: the repository remembers per tenant whether its table exists.
    private readonly long _tenantId = Random.Shared.NextInt64(1_000_000, long.MaxValue);

    public ComputeOutboxSqlTests(ComputeSqlFixture db) => _db = db;

    private sealed class Factory(string cs) : ITenantConnectionFactory
    {
        public Task<SqlConnection> CreateAsync(CancellationToken ct = default) => Task.FromResult(new SqlConnection(cs));
    }

    private ComputeOutboxRepository Outbox(bool enabled)
    {
        var context = Substitute.For<IQueryContext>();
        context.TenantId.Returns(_tenantId);
        var settings = Substitute.For<IComputeSettings>();
        settings.IsComputeEnabledAsync(_tenantId, Arg.Any<CancellationToken>()).Returns(enabled);
        return new ComputeOutboxRepository(new Factory(_db.ConnectionString), context, settings);
    }

    private async Task<long> RowsAsync(long tableId)
    {
        await using var conn = new SqlConnection(_db.ConnectionString);
        return await conn.ExecuteScalarAsync<long>("SELECT COUNT_BIG(*) FROM meta.ComputeOutbox WHERE TableId = @tableId", new { tableId });
    }

    private static long NewTableId() => Random.Shared.NextInt64(100_000, long.MaxValue);

    [Fact]
    public async Task The_row_commits_with_the_write_and_disappears_with_a_rollback()
    {
        var outbox = Outbox(enabled: true);
        var committedTable = NewTableId();
        var rolledBackTable = NewTableId();

        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        await using (var tx = conn.BeginTransaction())
        {
            (await outbox.AddAsync([new ComputeChange(rolledBackTable, ComputeChangeReason.Create, RecordId: 1)], tx)).Should().BeTrue();
            tx.Rollback();
        }
        await using (var tx = conn.BeginTransaction())
        {
            await outbox.AddAsync([new ComputeChange(committedTable, ComputeChangeReason.Update, RecordPublicId: Guid.NewGuid(),
                ChangedFids: [7], OldRefs: new Dictionary<int, long?> { [7] = 3 })], tx);
            tx.Commit();
        }

        (await RowsAsync(rolledBackTable)).Should().Be(0);
        (await RowsAsync(committedTable)).Should().Be(1);
        var row = await conn.QuerySingleAsync<(byte Reason, string ChangedFids, string OldRefs)>(
            "SELECT Reason, ChangedFids, OldRefs FROM meta.ComputeOutbox WHERE TableId = @committedTable", new { committedTable });
        row.Reason.Should().Be((byte)ComputeChangeReason.Update);
        row.ChangedFids.Should().Be("[7]");
        ComputeOutboxJson.ParseOldRefs(row.OldRefs)[7].Should().Be(3);
    }

    [Fact]
    public async Task A_switched_off_tenant_writes_nothing()
    {
        var tableId = NewTableId();
        (await Outbox(enabled: false).AddAsync([ComputeChange.TableWide(tableId)], null)).Should().BeFalse();
        (await RowsAsync(tableId)).Should().Be(0);
    }

    [Fact]
    public async Task Counts_claims_and_purges()
    {
        var outbox = Outbox(enabled: true);
        var tableId = NewTableId();
        await outbox.AddAsync(Enumerable.Range(1, 3).Select(i => new ComputeChange(tableId, ComputeChangeReason.Delete, RecordId: i)).ToList(), null);

        (await outbox.CountPendingAsync(tableId)).Should().Be(3);
        (await outbox.CountPendingByTableAsync())[tableId].Should().Be(3);

        // Younger than the grace period: nothing is due yet.
        (await outbox.ClaimDueAsync(100, TimeSpan.FromMinutes(5), TimeSpan.Zero, 10)).Should().NotContain(e => e.TableId == tableId);
        // No grace: claimed once, and the attempt is counted so a second claim within retryAfter takes nothing.
        var claimed = (await outbox.ClaimDueAsync(1000, TimeSpan.Zero, TimeSpan.FromMinutes(5), 10)).Where(e => e.TableId == tableId).ToList();
        claimed.Should().HaveCount(3).And.OnlyContain(e => e.Attempts == 1);
        (await outbox.ClaimDueAsync(1000, TimeSpan.Zero, TimeSpan.FromMinutes(5), 10)).Should().NotContain(e => e.TableId == tableId);

        await using (var conn = new SqlConnection(_db.ConnectionString))
            await conn.ExecuteAsync("UPDATE meta.ComputeOutbox SET CreatedOn = DATEADD(DAY, -20, CreatedOn) WHERE TableId = @tableId", new { tableId });
        (await outbox.PurgeOlderThanAsync(TimeSpan.FromDays(14))).Should().BeGreaterThanOrEqualTo(3);
        (await outbox.CountPendingAsync(tableId)).Should().Be(0);
    }

    [Fact]
    public async Task The_update_returns_the_old_parent_from_the_same_statement()
    {
        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        var publicId = await conn.ExecuteScalarAsync<Guid>("INSERT INTO data.t_5 (f_7, f_9) OUTPUT inserted.PublicId VALUES (41, N'a')");

        var sql = RecordRepository.BuildUpdateByPublicIdSql("data.t_5", ["f_7 = @f_7", "f_9 = @f_9"], [7]);
        var rows = (await conn.QueryAsync(sql, new { f_7 = 99L, f_9 = "b", publicId, modifiedBy = 1L })).Cast<object>().ToList();

        rows.Should().HaveCount(1);
        RecordRepository.ReadOldRefs(rows[0], [7])[7].Should().Be(41);
        (await conn.ExecuteScalarAsync<long>("SELECT f_7 FROM data.t_5 WHERE PublicId = @publicId", new { publicId })).Should().Be(99);
    }

    [Fact]
    public async Task The_migrations_are_idempotent_and_default_to_off()
    {
        await using var conn = new SqlConnection(_db.ConnectionString);
        await conn.OpenAsync();
        foreach (var script in ComputeSqlFixture.StoredColumnMigrations())
            await ComputeSqlFixture.RunScriptAsync(conn, script);   // second run: no error

        var fieldId = await conn.ExecuteScalarAsync<long>("INSERT INTO meta.AppField (Name) OUTPUT inserted.Id VALUES (N'x')");
        var field = await conn.QuerySingleAsync<(byte Mode, byte State, int Version, DateTime? On)>(
            "SELECT MaterializeMode, MaterializeState, MaterializeVersion, MaterializedOn FROM meta.AppField WHERE Id = @fieldId", new { fieldId });
        field.Should().Be(((byte)0, (byte)0, 0, (DateTime?)null));
        (await conn.ExecuteScalarAsync<int>("SELECT COUNT(*) FROM meta.ReplicaHeartbeat")).Should().Be(1);
    }
}
