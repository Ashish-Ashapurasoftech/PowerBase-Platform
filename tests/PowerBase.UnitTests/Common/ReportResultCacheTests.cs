using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Common;

/// <summary>
/// Doc 23 §5: the report result cache. Keys keep tenants, users, versions and data apart; an answer is never stored while a write is
/// open (the rowversion counter only moves on the write, not on its commit); off by default, without Redis, and with read replicas.
/// </summary>
[Collection("ReportResultCache")]   // the not-cacheable list and the circuit are process-wide
public class ReportResultCacheTests
{
    private sealed class Tenant(string cs) : ITenantConnectionFactory
    {
        public Task<SqlConnection> CreateAsync(CancellationToken ct = default) => Task.FromResult(new SqlConnection(cs));
    }

    private static IConfiguration Config(bool replicas = false) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["ReadReplica:Enabled"] = replicas ? "true" : "false" }).Build();

    private static IMetadataCache Versions(string? stamp = "fields=1|userperms=3")
    {
        var m = Substitute.For<IMetadataCache>();
        m.VersionStampAsync(Arg.Any<long>()).Returns(stamp);
        return m;
    }

    private static IQueryContext User(long tenant, long user)
    {
        var qc = Substitute.For<IQueryContext>();
        qc.TenantId.Returns(tenant);
        qc.UserId.Returns(user);
        return qc;
    }

    private static ReportResultCache Cache(string cs, IDistributedCache? l2, IMetadataCache? versions = null, bool enabled = true,
        bool replicas = false, long tenant = 45, long user = 7) =>
        new(new Tenant(cs), User(tenant, user), versions ?? Versions(), new ResultCacheOptions { Enabled = enabled }, Config(replicas),
            NullLogger<ReportResultCache>.Instance, l2);

    private static MemoryDistributedCache Redis() => new(Options.Create(new MemoryDistributedCacheOptions()));

    [Theory]
    [InlineData(46, 7, "fields=1", 100)]   // another tenant
    [InlineData(45, 8, "fields=1", 100)]   // another user
    [InlineData(45, 7, "fields=2", 100)]   // a field or permission changed
    [InlineData(45, 7, "fields=1", 101)]   // any data write in the tenant database
    public void Every_part_of_the_question_is_in_the_key(long tenant, long user, string stamp, long dbts)
    {
        var report = Guid.Parse("00000000-0000-0000-0000-000000000009");
        var baseKey = ReportResultCache.KeyFor(45, 7, "fields=1", 100, report, "q");
        ReportResultCache.KeyFor(tenant, user, stamp, dbts, report, "q").Should().NotBe(baseKey);
        baseKey.Should().StartWith("rr:t45:u7:").And.NotContain("fields", "keys hold ids and hashes only");
    }

    [Fact]
    public void The_question_covers_runtime_filter_values()
    {
        var id = Guid.NewGuid();
        ReportsControllerQuestion(new RunReportQuery(id, 1, 20, [(6L, "Open", null)]))
            .Should().NotBe(ReportsControllerQuestion(new RunReportQuery(id, 1, 20, [(6L, "Closed", null)])));
    }

    private static string ReportsControllerQuestion(RunReportQuery q) => PowerBase.API.Controllers.ReportsController.ResultQuestion(q);

    [Fact]
    public async Task Off_by_default_without_Redis_with_replicas_or_without_versions()
    {
        var report = Guid.NewGuid();
        (await Cache("unused", Redis(), enabled: false).LookupAsync(report, "q")).Key.Should().BeNull();
        (await Cache("unused", l2: null).LookupAsync(report, "q")).Key.Should().BeNull();
        (await Cache("unused", Redis(), replicas: true).LookupAsync(report, "q")).Key.Should().BeNull();
        (await Cache("unused", Redis(), versions: Versions(stamp: null)).LookupAsync(report, "q")).Key.Should().BeNull();
    }

    [Fact]
    public async Task On_a_real_database_an_open_write_is_never_cached_and_its_commit_gives_a_new_key()
    {
        var server = Environment.GetEnvironmentVariable("POWERBASE_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(server)) return;
        ReportResultCache.ResetForTests();

        var dbName = "PbResultCacheTest_" + Guid.NewGuid().ToString("N")[..8];
        await using (var master = new SqlConnection(server)) await master.ExecuteAsync($"CREATE DATABASE [{dbName}]");
        var cs = new SqlConnectionStringBuilder(server) { InitialCatalog = dbName }.ConnectionString;
        try
        {
            await using var writer = new SqlConnection(cs);
            await writer.OpenAsync();
            await writer.ExecuteAsync("CREATE TABLE dbo.t_1 (Id INT IDENTITY PRIMARY KEY, Amount INT, IsDeleted BIT NOT NULL DEFAULT 0, RowVersion ROWVERSION); INSERT dbo.t_1 (Amount) VALUES (10), (20);");
            var redis = Redis();
            var report = Guid.NewGuid();

            // 1. Nothing open: a key, a miss; the answer is stored and the next lookup is a hit.
            var first = await Cache(cs, redis).LookupAsync(report, "sum");
            first.Key.Should().NotBeNull();
            first.Body.Should().BeNull();
            Cache(cs, redis).Store(first, "{\"total\":30}"u8.ToArray());
            for (var i = 0; i < 50 && await redis.GetAsync(first.Key!) is null; i++) await Task.Delay(20);
            (await Cache(cs, redis).LookupAsync(report, "sum")).Body.Should().NotBeNull();

            // 2. A write is open (not committed): no key at all, so the old answer cannot be stored under the new counter.
            await using var tx = await writer.BeginTransactionAsync();
            await writer.ExecuteAsync("UPDATE dbo.t_1 SET Amount = 99 WHERE Id = 1", transaction: tx);
            (await Cache(cs, redis).LookupAsync(report, "sum")).Key.Should().BeNull();

            // 3. Committed: the counter moved when the row changed, so the question has a new key and the old answer is not read.
            await tx.CommitAsync();
            var after = await Cache(cs, redis).LookupAsync(report, "sum");
            after.Key.Should().NotBe(first.Key);
            after.Body.Should().BeNull();

            // 4. A soft delete moves it too.
            await writer.ExecuteAsync("UPDATE dbo.t_1 SET IsDeleted = 1 WHERE Id = 2");
            (await Cache(cs, redis).LookupAsync(report, "sum")).Key.Should().NotBe(after.Key);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var master = new SqlConnection(server);
            await master.ExecuteAsync($"ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]");
        }
    }

    [Fact]
    public async Task A_report_known_not_to_be_cacheable_is_skipped()
    {
        var server = Environment.GetEnvironmentVariable("POWERBASE_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(server)) return;
        ReportResultCache.ResetForTests();
        var cache = Cache(server, Redis());
        var table = Guid.NewGuid();
        (await cache.LookupAsync(table, "page 1")).Key.Should().NotBeNull();
        cache.MarkNotCacheable(table);
        (await cache.LookupAsync(table, "page 1")).Key.Should().BeNull("a Table report: no Redis or SQL round trip for a while");
    }
}
