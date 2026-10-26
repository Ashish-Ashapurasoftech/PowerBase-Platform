using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using PowerBase.API.Workers;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Search;
using PowerBase.Domain.Entities;
using Xunit;

namespace PowerBase.UnitTests.Search;

public class SearchRoutingTests
{
    private static readonly IReadOnlySet<long> NoDedicated = new HashSet<long>();

    // ---- tenant -> index is a pure function

    [Fact]
    public void Without_sharding_every_tenant_uses_the_configured_index()
    {
        foreach (var tenant in new long[] { 0, 1, 7, 12345 })
            SearchIndexRouting.TenantIndex(tenant, "powerbase-records-index", 1, false, NoDedicated).Should().Be("powerbase-records-index");
    }

    [Fact]
    public void With_sharding_a_tenant_always_maps_to_the_same_shard_whatever_process_asks()
    {
        // the previous design kept the shard list in memory, so two instances (or one before and after a restart) disagreed
        var first = Enumerable.Range(1, 200).Select(t => SearchIndexRouting.TenantIndex(t, "d", 4, false, NoDedicated)).ToList();
        var second = Enumerable.Range(1, 200).Select(t => SearchIndexRouting.TenantIndex(t, "d", 4, false, NoDedicated)).ToList();

        second.Should().Equal(first);
        first.Distinct().Should().BeEquivalentTo("pb-shard-001", "pb-shard-002", "pb-shard-003", "pb-shard-004");
        SearchIndexRouting.TenantIndex(5, "d", 4, false, NoDedicated).Should().Be("pb-shard-002");
    }

    [Fact]
    public void A_tenants_documents_are_never_split_over_several_indexes()
    {
        // one tenant -> exactly one index name for writes and for reads, so no query ever pages across indexes
        SearchIndexRouting.TenantIndex(42, "d", 8, false, NoDedicated).Should().Be(SearchIndexRouting.TenantIndex(42, "d", 8, false, NoDedicated));
    }

    [Fact]
    public void A_dedicated_tenant_gets_its_own_index_only_when_per_tenant_indexes_are_on()
    {
        var dedicated = new HashSet<long> { 9 };

        SearchIndexRouting.TenantIndex(9, "d", 4, true, dedicated).Should().Be("pb-tenant-9");
        SearchIndexRouting.TenantIndex(9, "d", 4, false, dedicated).Should().Be("pb-shard-002");
        SearchIndexRouting.TenantIndex(10, "d", 4, true, dedicated).Should().Be("pb-shard-003");
    }

    // ---- index budget

    [Fact]
    public void A_table_may_have_its_own_index_while_the_budget_has_room_or_if_it_already_has_one()
    {
        var existing = new[] { "powerbase-records-index", "pb-tbl-1", "pb-tbl-2" };

        SearchIndexRouting.WithinWideIndexBudget(existing, "pb-tbl-3", 3).Should().BeTrue();
        SearchIndexRouting.WithinWideIndexBudget(existing, "pb-tbl-3", 2).Should().BeFalse("two such indexes already exist");
        SearchIndexRouting.WithinWideIndexBudget(existing, "pb-tbl-2", 2).Should().BeTrue("it already exists, so it takes nothing new");
        SearchIndexRouting.WithinWideIndexBudget(existing, "pb-tbl-9", 0).Should().BeFalse("a budget of zero switches the feature off");
    }

    [Fact]
    public async Task A_table_is_not_moved_when_the_search_service_has_no_room_for_another_index()
    {
        var repo = Substitute.For<IAppFieldRepository>();
        var publisher = Substitute.For<IMessagePublisher>();
        var search = Substitute.For<IAzureSearchService>();
        search.CanUseWideIndexAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(false);
        repo.ListByTableAsync(7, Arg.Any<CancellationToken>()).Returns(
            Enumerable.Range(1, SearchSlotResolver.WideTableThreshold + 1).Select(i => new AppField { Id = i, Fid = 5 + i, TypeCode = "Text", IsFilterable = true }).ToList());

        (await SearchWideMigration.StartIfNeededAsync(repo, publisher, search, 1, 2, 7, CancellationToken.None)).Should().BeFalse();
        await publisher.DidNotReceive().PublishAsync(Arg.Any<SearchIndexMessage>(), Arg.Any<CancellationToken>());

        (await SearchWideMigration.FinalizeAsync(repo, search, 1, 7, CancellationToken.None)).Should().BeFalse();
        await repo.DidNotReceive().MarkTableWideAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ---- dropping the live index

    [Fact]
    public void Dropping_an_index_is_refused_unless_allowed_and_confirmed_by_name()
    {
        SearchIndexSafety.RefuseDestructive("powerbase-records-index", "powerbase-records-index", allowedByConfiguration: false)
            .Should().Contain("AllowDestructiveAdmin");
        SearchIndexSafety.RefuseDestructive("powerbase-records-index", null, allowedByConfiguration: true)
            .Should().Contain("confirm");
        SearchIndexSafety.RefuseDestructive("powerbase-records-index", "other-index", allowedByConfiguration: true)
            .Should().NotBeNull();
        SearchIndexSafety.RefuseDestructive("powerbase-records-index", "POWERBASE-RECORDS-INDEX", allowedByConfiguration: true)
            .Should().NotBeNull("the name must match exactly");
        SearchIndexSafety.RefuseDestructive("powerbase-records-index", "powerbase-records-index", allowedByConfiguration: true)
            .Should().BeNull();
    }

    // ---- priority queue

    [Theory]
    [InlineData(null, true)]
    [InlineData("true", true)]
    [InlineData("false", false)]
    [InlineData("FALSE", false)]
    public void The_priority_worker_is_on_unless_switched_off(string? setting, bool expected)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(setting == null
                ? new Dictionary<string, string?>()
                : new Dictionary<string, string?> { ["AzureServiceBus:SearchIndexPriorityQueueEnabled"] = setting })
            .Build();

        SearchIndexerPriorityWorker.IsEnabled(config).Should().Be(expected);
    }
}
