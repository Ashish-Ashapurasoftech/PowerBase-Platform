using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using NSubstitute;
using PowerBase.API.Services;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Search;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Search;

public class SearchOutboxTests
{
    private static SearchOutboxEntry Entry(long id, Guid record, IndexAction action, long version, long table = 7, long app = 2) =>
        new(id, table, app, record, action, version, 1);

    // ---- what the relay publishes

    [Fact]
    public void Several_changes_of_one_record_become_one_upsert_with_the_newest_version_and_no_values()
    {
        var record = Guid.NewGuid();
        var messages = SearchOutboxPlanner.ToMessages(5, new[]
        {
            Entry(1, record, IndexAction.Upsert, 100),
            Entry(2, record, IndexAction.Upsert, 300),
            Entry(3, record, IndexAction.Upsert, 200)
        });

        var m = messages.Should().ContainSingle().Subject;
        m.Action.Should().Be(IndexAction.Upsert);
        m.Version.Should().Be(300);
        m.TenantId.Should().Be(5);
        m.TableId.Should().Be(7);
        m.AppId.Should().Be(2);
        m.Payload.Should().BeNull("the indexer reads the record's current state from SQL, so a replay cannot write old values");
    }

    [Fact]
    public void A_delete_wins_over_earlier_upserts_and_each_record_gets_its_own_message()
    {
        var gone = Guid.NewGuid();
        var kept = Guid.NewGuid();
        var messages = SearchOutboxPlanner.ToMessages(5, new[]
        {
            Entry(1, gone, IndexAction.Upsert, 100),
            Entry(2, gone, IndexAction.Delete, 150),
            Entry(3, kept, IndexAction.Upsert, 120),
            Entry(4, kept, IndexAction.Upsert, 90, table: 8)
        });

        messages.Should().HaveCount(3, "the same record id in two tables is two records");
        messages.Single(m => m.RecordPublicId == gone).Action.Should().Be(IndexAction.Delete);
        messages.Single(m => m.RecordPublicId == gone).Version.Should().Be(150);
        messages.Where(m => m.RecordPublicId == kept).Should().OnlyContain(m => m.Action == IndexAction.Upsert);
    }

    [Fact]
    public void The_daily_check_runs_at_the_configured_hour_and_never_in_the_past()
    {
        var beforeTwo = new DateTime(2026, 10, 26, 1, 30, 0, DateTimeKind.Utc);
        var afterTwo = new DateTime(2026, 10, 26, 3, 0, 0, DateTimeKind.Utc);

        SearchOutboxPlanner.NextSweepUtc(beforeTwo, 2).Should().Be(new DateTime(2026, 10, 26, 2, 0, 0, DateTimeKind.Utc));
        SearchOutboxPlanner.NextSweepUtc(afterTwo, 2).Should().Be(new DateTime(2026, 10, 27, 2, 0, 0, DateTimeKind.Utc));
        SearchOutboxPlanner.NextSweepUtc(afterTwo, 99).Should().Be(new DateTime(2026, 10, 26, 23, 0, 0, DateTimeKind.Utc), "an hour outside 0-23 is clamped to 23");
    }

    [Fact]
    public void The_relay_has_sensible_defaults()
    {
        var o = new SearchOutboxOptions();
        o.Enabled.Should().BeTrue();
        o.GraceSeconds.Should().BeGreaterThan(o.RelayIntervalSeconds, "the normal path gets a chance before the relay steps in");
        o.MaxAttempts.Should().Be(10);
    }

    // ---- deleted fields

    private static AppField F(long id, int fid, string type, string? slot = null, string? end = null) =>
        new() { Id = id, Fid = fid, TypeCode = type, IsFilterable = true, IsSearchable = true, SearchSlot = slot, SearchSlotEnd = end };

    [Fact]
    public void Deleting_a_field_that_was_indexed_clears_the_columns_it_used_including_a_ranges_second_column()
    {
        var table = new AppTable { Id = 9, AppId = 3 };
        var text = F(1, 6, "Text", "str_01");
        var range = F(2, 7, "DateRange", "date_01", "date_02");
        var neverIndexed = new AppField { Id = 3, Fid = 8, TypeCode = "Text" };
        var all = new List<AppField> { text, range, neverIndexed };

        var messages = FieldDeleteIndexCleanup.Capture(4, table, all, new[] { text, range, neverIndexed });

        messages.Should().HaveCount(2, "a field that was never indexed has nothing to clear");
        messages.Should().OnlyContain(m => m.Action == IndexAction.NullifyField && m.TenantId == 4 && m.TableId == 9 && m.AppId == 3);
        messages.Single(m => m.FieldId == 6).Slot.Should().Be("str_01");
        messages.Single(m => m.FieldId == 7).Slot.Should().Be("date_01");
        messages.Single(m => m.FieldId == 7).EndSlot.Should().Be("date_02");
    }

    [Fact]
    public async Task Publishing_the_cleanup_never_fails_the_delete()
    {
        var publisher = Substitute.For<IMessagePublisher>();
        publisher.PublishBatchAsync(Arg.Any<IEnumerable<SearchIndexMessage>>(), Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("bus down"));

        var act = () => FieldDeleteIndexCleanup.PublishAsync(publisher, new[] { new SearchIndexMessage { Action = IndexAction.NullifyField } }, CancellationToken.None);

        await act.Should().NotThrowAsync();
        await FieldDeleteIndexCleanup.PublishAsync(null, new[] { new SearchIndexMessage() }, CancellationToken.None);
    }

    [Fact]
    public void A_deleted_field_is_cleared_in_the_tables_own_index_too_while_it_is_moving()
    {
        var fields = Enumerable.Range(1, SearchSlotResolver.WideTableThreshold + 1).Select(i => F(i, 5 + i, "Text", i <= 60 ? $"str_{i:D2}" : null)).ToList();
        var info = SearchTableSlotInfo.From(77, fields);
        var record = new SearchIndexDocument(1, 2, 77, Guid.NewGuid(), new Dictionary<long, object?>());

        var docs = SearchIndexDocumentBuilder.BuildExplicitClear(info, record, 6, "str_01", null);

        docs.Should().HaveCount(2);
        docs[0].SlotValues!.Should().ContainKey("str_01").WhoseValue.Should().BeNull();
        docs[1].CustomIndexName.Should().Be("pb-tbl-77");
        docs[1].SlotValues!.Should().ContainKey("f_6");
    }

    [Fact]
    public async Task A_column_conversion_pushes_the_field_to_the_index_only_if_the_field_is_indexed()
    {
        var publisher = Substitute.For<IMessagePublisher>();
        var context = Substitute.For<IQueryContext>();
        context.TenantId.Returns(4);
        var table = new AppTable { Id = 9, AppId = 3 };

        await SearchFieldReindex.RequestAsync(publisher, context, table, new AppField { Fid = 6, IsFilterable = true }, CancellationToken.None);
        await publisher.Received(1).PublishAsync(
            Arg.Is<SearchIndexMessage>(m => m.Action == IndexAction.BackfillField && m.FieldId == 6 && m.TableId == 9 && m.TenantId == 4), Arg.Any<CancellationToken>());

        publisher.ClearReceivedCalls();
        await SearchFieldReindex.RequestAsync(publisher, context, table, new AppField { Fid = 7 }, CancellationToken.None);
        await publisher.DidNotReceive().PublishAsync(Arg.Any<SearchIndexMessage>(), Arg.Any<CancellationToken>());
    }

    // ---- global search over several indexes

    [Fact]
    public void Results_from_several_indexes_are_ranked_together_and_paged_as_one_list()
    {
        var shared = new List<(double, string)> { (9.0, "s1"), (5.0, "s2"), (1.0, "s3") };
        var own = new List<(double, string)> { (8.0, "w1"), (6.0, "w2"), (0.5, "w3") };

        SearchResultMerger.MergeByScore(new[] { (IReadOnlyList<(double, string)>)shared, own }, 0, 4).Should().Equal("s1", "w1", "w2", "s2");
        SearchResultMerger.MergeByScore(new[] { (IReadOnlyList<(double, string)>)shared, own }, 4, 4).Should().Equal("s3", "w3");
    }

    // ---- health

    private static SearchPipelineHealthCheck Check(SearchPipelineState state, IAzureSearchService search, Dictionary<string, string?>? settings = null) =>
        new(state, search, new ConfigurationBuilder().AddInMemoryCollection(settings ?? new Dictionary<string, string?>()).Build());

    private static IAzureSearchService SearchService(bool enabled, bool reachable)
    {
        var s = Substitute.For<IAzureSearchService>();
        s.IsEnabled.Returns(enabled);
        s.IsHealthyAsync(Arg.Any<CancellationToken>()).Returns(reachable);
        return s;
    }

    [Fact]
    public async Task A_running_pipeline_is_healthy()
    {
        var state = new SearchPipelineState();
        state.MarkWorkerLoop("SearchIndexerWorker");
        state.SetOutbox(0, 0, null);

        var result = await Check(state, SearchService(true, true)).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Healthy);
    }

    [Fact]
    public async Task An_unreachable_search_service_is_unhealthy()
    {
        var state = new SearchPipelineState();
        state.MarkWorkerLoop("SearchIndexerWorker");

        var result = await Check(state, SearchService(true, false)).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
    }

    [Fact]
    public async Task A_worker_that_stopped_looking_at_its_queue_is_unhealthy()
    {
        var state = new SearchPipelineState();
        state.WorkerLoopsForTest("SearchIndexerWorker", DateTime.UtcNow.AddMinutes(-10));

        var result = await Check(state, SearchService(true, true)).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().Be(HealthStatus.Unhealthy);
        result.Description.Should().Contain("SearchIndexerWorker");
    }

    [Fact]
    public async Task Changes_waiting_too_long_or_rows_that_gave_up_make_it_degraded()
    {
        var state = new SearchPipelineState();
        state.MarkWorkerLoop("SearchIndexerWorker");
        state.SetOutbox(12, 0, DateTime.UtcNow.AddHours(-2));
        (await Check(state, SearchService(true, true)).CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Degraded);

        state.SetOutbox(0, 3, null);
        (await Check(state, SearchService(true, true)).CheckHealthAsync(new HealthCheckContext())).Status.Should().Be(HealthStatus.Degraded);
    }

    [Fact]
    public async Task Search_that_is_not_configured_is_not_an_error()
    {
        var result = await Check(new SearchPipelineState(), SearchService(false, false)).CheckHealthAsync(new HealthCheckContext());

        result.Status.Should().NotBe(HealthStatus.Unhealthy);
    }

    // ---- credentials

    [Theory]
    [InlineData(null, null, false)]
    [InlineData("key", null, true)]
    [InlineData(null, "true", true)]
    public void Search_is_enabled_with_a_key_or_with_a_managed_identity(string? key, string? managedIdentity, bool expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureAiSearch:Endpoint"] = "https://example.search.windows.net",
            ["AzureAiSearch:ApiKey"] = key,
            ["AzureAiSearch:UseManagedIdentity"] = managedIdentity
        }).Build();

        new AzureSearchService(config).IsEnabled.Should().Be(expected);
    }

    [Fact]
    public async Task A_disabled_search_service_has_no_room_for_a_table_index()
    {
        var config = new ConfigurationBuilder().Build();
        (await new AzureSearchService(config).CanUseWideIndexAsync(5)).Should().BeFalse();
    }

    [Theory]
    [InlineData(null, null, false)]
    [InlineData(null, "Endpoint=sb://x.servicebus.windows.net/;SharedAccessKeyName=k;SharedAccessKey=v", true)]
    [InlineData("x.servicebus.windows.net", null, true)]
    [InlineData("<placeholder>", "<placeholder>", false)]
    public void Service_Bus_uses_a_namespace_identity_or_a_connection_string(string? ns, string? connection, bool configured)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["AzureServiceBus:FullyQualifiedNamespace"] = ns,
            ["AzureServiceBus:ConnectionString"] = connection
        }).Build();

        using var _ = new DisposableClient(ServiceBusRetry.CreateClient(config, out var isConfigured));

        isConfigured.Should().Be(configured);
    }

    private sealed class DisposableClient(Azure.Messaging.ServiceBus.ServiceBusClient client) : IDisposable
    {
        public void Dispose() => client.DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}

internal static class SearchPipelineStateTestExtensions
{
    /// <summary>Sets a worker's last-loop time directly (the state only exposes "now" through MarkWorkerLoop).</summary>
    public static void WorkerLoopsForTest(this SearchPipelineState state, string worker, DateTime lastLoopUtc) =>
        ((System.Collections.Concurrent.ConcurrentDictionary<string, DateTime>)typeof(SearchPipelineState)
            .GetField("_workerLoopUtc", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(state)!)[worker] = lastLoopUtc;
}
