using FluentAssertions;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Caching;
using PowerBase.Application.Common.Configurations;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Common;

/// <summary>Doc 34 §4 / doc 23 §3: the cache counts where each lookup was answered, and how often a report question repeats.</summary>
public class CacheMetricsTests
{
    private static long Total(string key) => CacheMetrics.Snapshot().FirstOrDefault(m => m.Key == key).Value;

    [Fact]
    public async Task Lookups_are_counted_by_where_they_were_answered()
    {
        // A group of its own, so other tests running at the same time do not move these counts.
        var group = "metrics-" + Guid.NewGuid().ToString("N")[..6];
        var cache = new MetadataCache(new MemoryCache(new MemoryCacheOptions()), Options.Create(new MetadataCacheOptions { UseInMemoryCache = true }),
            NullLogger<MetadataCache>.Instance);

        await cache.GetOrAddAsync(1, group, "k", () => Task.FromResult("v"));
        await cache.GetOrAddAsync(1, group, "k", () => Task.FromResult("v"));
        await cache.GetOrAddAsync(1, group, "k", () => Task.FromResult("v"));

        Total($"lookup:{group}:sql").Should().Be(1);
        Total($"lookup:{group}:l1").Should().Be(2);
    }

    [Fact]
    public async Task L2_hits_and_written_bytes_are_counted()
    {
        var versions = Substitute.For<ICacheVersionStore>();
        versions.ReadAsync(Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyDictionary<string, long>?>(new Dictionary<string, long>()));
        var l2 = new MemoryDistributedCache(Options.Create(new MemoryDistributedCacheOptions()));
        var options = Options.Create(new MetadataCacheOptions { TransactionQuarantineSeconds = 0, WriteQuarantineSeconds = 0 });
        var tenant = Random.Shared.NextInt64(1_000_000, 9_000_000);
        var hitsBefore = Total($"lookup:{MetadataCacheGroups.Tables}:l2");
        var bytesBefore = Total($"l2bytes:{MetadataCacheGroups.Tables}");

        AppTable Load() => new() { Id = 5, Name = "Projects" };
        await new MetadataCache(new MemoryCache(new MemoryCacheOptions()), options, NullLogger<MetadataCache>.Instance, versions, l2)
            .GetOrAddAsync(tenant, MetadataCacheGroups.Tables, "id:5", () => Task.FromResult(Load()));
        for (var i = 0; i < 50 && Total($"l2bytes:{MetadataCacheGroups.Tables}") == bytesBefore; i++) await Task.Delay(20);
        await new MetadataCache(new MemoryCache(new MemoryCacheOptions()), options, NullLogger<MetadataCache>.Instance, versions, l2)
            .GetOrAddAsync(tenant, MetadataCacheGroups.Tables, "id:5", () => Task.FromResult(Load()));

        Total($"l2bytes:{MetadataCacheGroups.Tables}").Should().BeGreaterThan(bytesBefore);
        Total($"lookup:{MetadataCacheGroups.Tables}:l2").Should().BeGreaterThan(hitsBefore);
    }

    [Fact]
    public void The_same_question_within_60_seconds_is_a_repeat_and_another_is_not()
    {
        var hash = Guid.NewGuid().ToString("N");
        ReportRepeatProbe.IsRepeat(hash).Should().BeFalse();
        ReportRepeatProbe.IsRepeat(hash).Should().BeTrue();
        ReportRepeatProbe.IsRepeat(Guid.NewGuid().ToString("N")).Should().BeFalse();
    }

    [Fact]
    public void The_question_hash_tells_users_and_run_parameters_apart()
    {
        var report = new Report { Id = 9, PublicId = Guid.NewGuid(), Definition = """{"columns":[6]}""" };
        string Hash(long userId, string filterValue, int page = 1)
        {
            var qc = Substitute.For<IQueryContext>();
            qc.TenantId.Returns(45);
            qc.UserId.Returns(userId);
            var handler = new RunReportQueryHandler(Substitute.For<IReportRepository>(), Substitute.For<IAppTableRepository>(),
                Substitute.For<IAppFieldRepository>(), Substitute.For<IRecordRepository>(), Substitute.For<IRolePermissionEnforcer>(),
                Substitute.For<IUserRepository>(), Substitute.For<IFormulaProjector>(),
                Substitute.For<PowerBase.Application.Relationships.IRelationalProjector>(), Substitute.For<IAzureSearchService>(),
                Substitute.For<IAppUserRepository>(), qc, Substitute.For<ILogger<RunReportQueryHandler>>());
            return handler.QuestionHash(new RunReportQuery(report.PublicId, page, 20, [(6L, filterValue, null)]), report);
        }

        Hash(1, "Open").Should().Be(Hash(1, "Open"));
        Hash(2, "Open").Should().NotBe(Hash(1, "Open"), "another user may see other records");
        Hash(1, "Closed").Should().NotBe(Hash(1, "Open"), "runtime filter values count (tuples are written out by hand)");
        Hash(1, "Open", page: 2).Should().NotBe(Hash(1, "Open"));
    }
}
