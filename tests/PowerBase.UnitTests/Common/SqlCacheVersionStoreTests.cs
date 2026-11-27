using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Caching;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Infrastructure.Persistence;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Common;

/// <summary>
/// The real meta.CacheVersion on a real SQL Server (POWERBASE_TEST_SQL_CONNECTION, e.g. LocalDB): two API instances — two caches, two
/// stores, separate connections — see each other's writes on their next request. A throwaway database is created and dropped.
/// </summary>
public class SqlCacheVersionStoreTests
{
    private const long Tenant = 7;

    private sealed class Control(string cs) : IControlConnectionFactory
    {
        public string ConnectionString => cs;
        public SqlConnection Create() => new(cs);
    }

    private sealed class Tenants(string cs) : ITenantConnectionResolver
    {
        public Task<string> ResolveAsync(long tenantId, CancellationToken ct = default) => Task.FromResult(cs);
        public void Invalidate(long tenantId) { }
    }

    private static MetadataCache Instance(string cs) =>
        new(new MemoryCache(new MemoryCacheOptions()),
            Options.Create(new MetadataCacheOptions { TransactionQuarantineSeconds = 0, WriteQuarantineSeconds = 0 }),
            NullLogger<MetadataCache>.Instance,
            new SqlCacheVersionStore(new Control(cs), new Tenants(cs), NullLogger<SqlCacheVersionStore>.Instance));

    [Fact]
    public async Task Two_instances_share_invalidations_through_meta_CacheVersion()
    {
        var server = Environment.GetEnvironmentVariable("POWERBASE_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(server)) return;

        var dbName = "PbCacheVersionTest_" + Guid.NewGuid().ToString("N")[..8];
        await using (var master = new SqlConnection(server))
            await master.ExecuteAsync($"CREATE DATABASE [{dbName}]");
        var cs = new SqlConnectionStringBuilder(server) { InitialCatalog = dbName }.ConnectionString;
        try
        {
            await using (var db = new SqlConnection(cs))
            {
                await db.ExecuteAsync("CREATE SCHEMA meta");
                var script = await File.ReadAllTextAsync(FindMigration("tenant", "104_create_cache_version.sql"));
                foreach (var batch in script.Split("\nGO", StringSplitOptions.RemoveEmptyEntries).Where(b => b.Trim().Length > 0))
                    await db.ExecuteAsync(batch);
            }

            var a = Instance(cs);
            var b = Instance(cs);
            var permission = "can-delete";
            async Task<string> RequestOn(MetadataCache instance)
            {
                using var _ = CacheVersionScope.Begin();
                return await instance.GetOrAddAsync(Tenant, MetadataCacheGroups.Access, "role-5", () => Task.FromResult(permission));
            }

            (await RequestOn(b)).Should().Be("can-delete");
            a.Invalidate(Tenant, MetadataCacheGroups.Access);   // bump committed in SQL before the write
            permission = "read-only";
            (await RequestOn(b)).Should().Be("read-only");

            await using var check = new SqlConnection(cs);
            (await check.ExecuteScalarAsync<long>("SELECT V FROM meta.CacheVersion WHERE Grp = 'access'")).Should().Be(1);

            // The age the shared L2 waits on (database clock): just bumped → young; moved back 40 s → older than the 30 s quarantine.
            var store = new SqlCacheVersionStore(new Control(cs), new Tenants(cs), NullLogger<SqlCacheVersionStore>.Instance);
            var young = (await store.ReadWithAgeAsync(Tenant))!["access"];
            young.Version.Should().Be(1);
            young.AgeMs.Should().BeLessThan(10_000);
            await check.ExecuteAsync("UPDATE meta.CacheVersion SET ChangedOn = DATEADD(SECOND, -40, SYSUTCDATETIME()) WHERE Grp = 'access'");
            (await store.ReadWithAgeAsync(Tenant))!["access"].AgeMs.Should().BeGreaterThanOrEqualTo(40_000);
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var master = new SqlConnection(server);
            await master.ExecuteAsync($"ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]");
        }
    }

    [Fact]
    public async Task Without_the_table_reads_report_unavailable_and_bumps_do_not_throw()
    {
        var server = Environment.GetEnvironmentVariable("POWERBASE_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(server)) return;

        var store = new SqlCacheVersionStore(new Control(server), new Tenants(server), NullLogger<SqlCacheVersionStore>.Instance);
        (await store.ReadAsync(Tenant)).Should().BeNull("no meta.CacheVersion in master: security groups then bypass the cache");
        store.Bump(Tenant, MetadataCacheGroups.Access).Should().BeFalse();
    }

    private static string FindMigration(string folder, string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "database", "migrations", folder, file)))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "database", "migrations", folder, file);
    }
}
