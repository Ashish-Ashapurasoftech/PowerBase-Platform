using Dapper;
using FluentAssertions;
using Microsoft.Data.SqlClient;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Jobs;
using PowerBase.Infrastructure.Imports;
using PowerBase.Infrastructure.Persistence;
using Xunit;

namespace PowerBase.UnitTests.Import;

/// <summary>
/// The durable app-import queue on a real SQL Server (POWERBASE_TEST_SQL_CONNECTION): two instances never get the same job, a job
/// whose instance stopped is handed to another with a higher attempt count (the worker then marks it interrupted instead of running
/// it twice), and the identity travels with it. A throwaway database is created and dropped.
/// </summary>
public class AppImportDispatchQueueTests
{
    private sealed class Control(string cs) : IControlConnectionFactory
    {
        public string ConnectionString => cs;
        public SqlConnection Create() => new(cs);
    }

    [Fact]
    public async Task Two_instances_share_the_queue_without_running_a_job_twice()
    {
        var server = Environment.GetEnvironmentVariable("POWERBASE_TEST_SQL_CONNECTION");
        if (string.IsNullOrWhiteSpace(server)) return;

        var dbName = "PbAppImportQueueTest_" + Guid.NewGuid().ToString("N")[..8];
        await using (var master = new SqlConnection(server)) await master.ExecuteAsync($"CREATE DATABASE [{dbName}]");
        var cs = new SqlConnectionStringBuilder(server) { InitialCatalog = dbName }.ConnectionString;
        try
        {
            await using (var db = new SqlConnection(cs))
            {
                await db.ExecuteAsync("CREATE SCHEMA meta");
                var script = await File.ReadAllTextAsync(FindMigration("control", "020_create_app_import_queue.sql"));
                foreach (var batch in script.Split("\nGO", StringSplitOptions.RemoveEmptyEntries).Where(b => b.Trim().Length > 0))
                    await db.ExecuteAsync(batch);
            }
            var queue = new AppImportDispatchQueue(new Control(cs), Substitute.For<IQueryContext>());
            var job = Guid.NewGuid();
            var identity = new AppImportIdentity(42, false, "Asha", "asha@example.test", ["records.read", "apps.create"], "Administrator");

            await queue.EnqueueAsync(45, job, identity);

            var onA = await queue.ClaimAsync("instance-A", leaseSeconds: 60);
            var onB = await queue.ClaimAsync("instance-B", leaseSeconds: 60);
            onA.Should().NotBeNull();
            onA!.JobPublicId.Should().Be(job);
            onA.TenantId.Should().Be(45);
            onA.AttemptCount.Should().Be(1);
            AppImportIdentity.FromJson(onA.IdentityJson).Should().BeEquivalentTo(identity);
            onB.Should().BeNull("instance A holds the lease");

            (await queue.RenewLeaseAsync(onA.Id, "instance-B", onA.ClaimToken, 60)).Should().BeFalse("only the owner renews");
            (await queue.RenewLeaseAsync(onA.Id, "instance-A", onA.ClaimToken, 60)).Should().BeTrue();

            // Instance A stops: its lease runs out, instance B takes the job over with attempt 2 (→ marked interrupted, not re-run).
            await using (var db = new SqlConnection(cs))
                await db.ExecuteAsync("UPDATE meta.AppImportQueue SET LockedUntil = DATEADD(second, -1, SYSUTCDATETIME())");
            var takenOver = await queue.ClaimAsync("instance-B", leaseSeconds: 60);
            takenOver!.JobPublicId.Should().Be(job);
            takenOver.AttemptCount.Should().Be(2);

            await queue.CompleteAsync(takenOver.Id, "instance-A", onA.ClaimToken);   // the old owner's late completion is ignored
            (await queue.ClaimAsync("instance-C", 60)).Should().BeNull("B still holds it");
            await queue.CompleteAsync(takenOver.Id, "instance-B", takenOver.ClaimToken);
            await using (var db = new SqlConnection(cs))
                (await db.ExecuteScalarAsync<string>("SELECT Status FROM meta.AppImportQueue")).Should().Be("Done");
            (await queue.ClaimAsync("instance-C", 60)).Should().BeNull();
        }
        finally
        {
            SqlConnection.ClearAllPools();
            await using var master = new SqlConnection(server);
            await master.ExecuteAsync($"ALTER DATABASE [{dbName}] SET SINGLE_USER WITH ROLLBACK IMMEDIATE; DROP DATABASE [{dbName}]");
        }
    }

    private static string FindMigration(string folder, string file)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "database", "migrations", folder, file)))
            dir = dir.Parent;
        return Path.Combine(dir!.FullName, "database", "migrations", folder, file);
    }
}
