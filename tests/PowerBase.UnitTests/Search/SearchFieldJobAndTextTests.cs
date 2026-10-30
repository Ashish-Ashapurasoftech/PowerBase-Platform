using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.API.Workers;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Search;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Search;

/// <summary>
/// Quick search reads every Searchable field the same way, numbers and dates included, and a check made without the index agrees with
/// it; a record that changed while it was being indexed is indexed again; a field job is recorded before it is sent, moves forward page
/// by page, and is resumed (not lost) when a send fails.
/// </summary>
public class SearchFieldJobAndTextTests
{
    private static AppField F(long id, int fid, string type, bool searchable = true, bool filterable = true, string? slot = null) =>
        new() { Id = id, Fid = fid, TypeCode = type, IsSearchable = searchable, IsFilterable = filterable, SearchSlot = slot };

    // ---- the text quick search reads

    [Theory]
    [InlineData("Number", 12.5000, "12.5")]
    [InlineData("Currency", 1000, "1000")]
    [InlineData("Text", "  Acme Ltd ", "Acme Ltd")]
    [InlineData("Boolean", true, null)]
    [InlineData("Text", "", null)]
    public void A_value_reads_as_the_text_a_user_would_type(string type, object value, string? expected) =>
        SearchText.Format(type, value is double d ? (decimal)d : value).Should().Be(expected);

    [Fact]
    public void A_date_reads_as_yyyy_mm_dd_with_its_time_only_when_it_has_one()
    {
        SearchText.Format("Date", new DateTime(2026, 3, 5)).Should().Be("2026-03-05");
        SearchText.Format("DateTime", new DateTime(2026, 3, 5, 14, 30, 0)).Should().Be("2026-03-05 14:30:00");
    }

    [Theory]
    [InlineData("acm", true)]            // a word of three letters or more matches the start of a word
    [InlineData("ltd acme", true)]       // every word, in any order
    [InlineData("cme", false)]           // not the middle of a word
    [InlineData("ac", false)]            // a word of one or two letters must be a whole word
    [InlineData("\"acme ltd\"", true)]   // a phrase in quotes as written
    [InlineData("\"ltd acme\"", false)]
    [InlineData("o'brien", true)]
    [InlineData("12.5", true)]
    [InlineData("acme bogus", false)]
    public void A_match_without_the_index_follows_the_indexs_rules(string search, bool expected) =>
        SearchText.Matches(search, ["Acme Ltd", "Pat O'Brien", "12.5"]).Should().Be(expected);

    [Fact]
    public void Quick_search_text_holds_every_searchable_field_numbers_dates_and_fields_without_a_column_included()
    {
        var fields = new List<AppField>
        {
            F(1, 6, "Text", slot: "str_01"),
            F(2, 7, "Number", slot: "num_01"),
            F(3, 8, "Date", slot: "date_01"),
            F(4, 9, "Text"),                                  // its column pool is full: no slot
            F(5, 10, "Text", searchable: false, slot: "str_02"), // filterable only
            F(6, 11, "Boolean", slot: "bool_01"),
        };
        var info = SearchTableSlotInfo.From(10, fields);
        var values = new Dictionary<long, object?> { [6] = "Acme", [7] = 42m, [8] = new DateTime(2026, 1, 2), [9] = "Overflow", [10] = "internal", [11] = true };

        var doc = SearchIndexDocumentBuilder.Build(info, 1, 2, 10, Guid.NewGuid(), values, null, complete: true).Single();

        doc.SearchText.Should().Be("Acme 42 2026-01-02 Overflow");
        AzureSearchService.ToSearchDocument(doc, "pb-shard-001", new HashSet<string> { "search_content" })["search_content"]
            .Should().Be("Acme 42 2026-01-02 Overflow");
    }

    [Fact]
    public void A_table_moving_to_its_own_index_gets_the_same_quick_search_text_in_both()
    {
        var fields = Enumerable.Range(1, SearchSlotResolver.WideTableThreshold + 1).Select(i => F(i, 5 + i, i == 1 ? "Number" : "Text")).ToList();
        var info = SearchTableSlotInfo.From(77, fields);

        var docs = SearchIndexDocumentBuilder.Build(info, 1, 2, 77, Guid.NewGuid(), new Dictionary<long, object?> { [6] = 7m, [7] = "seven" }, null, complete: true);

        docs.Should().HaveCount(2);
        docs.Select(d => d.SearchText).Should().AllBe("7 seven", "a searchable number reads the same in the shared index and the table's own one");
    }

    // ---- two workers on one record

    [Fact]
    public void A_record_changed_between_two_version_reads_when_its_version_moved_or_it_appeared_or_vanished()
    {
        var a = Guid.NewGuid();
        var before = new Dictionary<Guid, long> { [a] = 5 };

        SearchIndexerWorker.RowChanged(before, new Dictionary<Guid, long> { [a] = 5 }, a).Should().BeFalse();
        SearchIndexerWorker.RowChanged(before, new Dictionary<Guid, long> { [a] = 6 }, a).Should().BeTrue();
        SearchIndexerWorker.RowChanged(before, new Dictionary<Guid, long>(), a).Should().BeTrue();
        SearchIndexerWorker.RowChanged(new Dictionary<Guid, long>(), new Dictionary<Guid, long> { [a] = 1 }, a).Should().BeTrue();
        SearchIndexerWorker.RowChanged(new Dictionary<Guid, long>(), new Dictionary<Guid, long>(), a).Should().BeFalse();
    }

    // ---- recorded field jobs

    private sealed class SyncFixture
    {
        public readonly IAppFieldRepository Fields = Substitute.For<IAppFieldRepository>();
        public readonly IRecordRepository Records = Substitute.For<IRecordRepository>();
        public readonly IAzureSearchService Search = Substitute.For<IAzureSearchService>();
        public readonly IMessagePublisher Publisher = Substitute.For<IMessagePublisher>();
        public readonly ISearchFieldJobs Jobs = Substitute.For<ISearchFieldJobs>();

        public SyncFixture()
        {
            Fields.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(new List<AppField> { F(1, 6, "Text", slot: "str_01") });
            Jobs.StartAsync(Arg.Any<SearchIndexMessage>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<long?>(55));
            Jobs.AdvanceAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);
        }

        public void FirstPageHas(int records, long lastId = 500) =>
            Records.GetTableIndexPageAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Is(0L), Arg.Any<int>(), Arg.Any<CancellationToken>(), Arg.Any<int>())
                .Returns((Enumerable.Range(0, records).Select(_ => new SearchIndexDocument(1, 2, 10, Guid.NewGuid(), new Dictionary<long, object?> { [6] = "x" })).ToList(), lastId));

        public Task RunAsync() =>
            FieldSlotSync.SyncAsync(Fields, Records, Search, Publisher, 1, 2, 10, 6, false, null, null, CancellationToken.None, Jobs);
    }

    [Fact]
    public async Task A_field_job_is_recorded_before_anything_is_written_and_moved_past_the_first_page()
    {
        var f = new SyncFixture();
        f.FirstPageHas(SearchWideMigration.PageSize, lastId: 731);

        await f.RunAsync();

        Received.InOrder(() =>
        {
            f.Jobs.StartAsync(Arg.Is<SearchIndexMessage>(m => m.FieldId == 6 && m.AfterId == 0), Arg.Any<CancellationToken>());
            f.Search.BulkIndexRecordsAsync(Arg.Any<IEnumerable<SearchIndexDocument>>(), Arg.Any<CancellationToken>());
            f.Jobs.AdvanceAsync(55, 0, 731, Arg.Any<CancellationToken>());
            f.Publisher.PublishAsync(Arg.Is<SearchIndexMessage>(m => m.JobId == 55 && m.AfterId == 731), Arg.Any<CancellationToken>());
        });
    }

    [Fact]
    public async Task A_table_done_in_the_first_page_ends_its_job()
    {
        var f = new SyncFixture();
        f.FirstPageHas(3);

        await f.RunAsync();

        await f.Jobs.Received(1).CompleteAsync(55, 0, Arg.Any<CancellationToken>());
        await f.Publisher.DidNotReceive().PublishAsync(Arg.Any<SearchIndexMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_job_whose_send_fails_stays_recorded_for_the_relay_to_resend()
    {
        var f = new SyncFixture();
        f.FirstPageHas(SearchWideMigration.PageSize);
        f.Publisher.PublishAsync(Arg.Any<SearchIndexMessage>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("bus down"));

        await f.Invoking(x => x.RunAsync()).Should().NotThrowAsync();

        await f.Jobs.DidNotReceiveWithAnyArgs().CompleteAsync(default, default, default);
        await f.Publisher.Received(3).PublishAsync(Arg.Any<SearchIndexMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task When_the_progress_cannot_be_recorded_the_queued_job_redoes_the_first_page()
    {
        var f = new SyncFixture();
        f.FirstPageHas(SearchWideMigration.PageSize, lastId: 731);
        f.Jobs.AdvanceAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(false);

        await f.RunAsync();

        await f.Publisher.Received(1).PublishAsync(Arg.Is<SearchIndexMessage>(m => m.JobId == 55 && m.AfterId == 0), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_deleted_fields_cleanup_and_a_column_conversion_are_recorded_jobs_too()
    {
        var jobs = Substitute.For<ISearchFieldJobs>();
        jobs.StartAsync(Arg.Any<SearchIndexMessage>(), Arg.Any<CancellationToken>()).Returns(Task.FromResult<long?>(7));
        var publisher = Substitute.For<IMessagePublisher>();
        var cleanup = new SearchIndexMessage { Action = IndexAction.NullifyField, TenantId = 1, TableId = 10, FieldId = 6, Slot = "str_01", AfterId = 0 };

        await FieldDeleteIndexCleanup.PublishAsync(publisher, [cleanup], CancellationToken.None, jobs);

        cleanup.JobId.Should().Be(7);
        await publisher.Received(1).PublishBatchAsync(Arg.Is<IEnumerable<SearchIndexMessage>>(m => m.Single().JobId == 7), Arg.Any<CancellationToken>());

        var queryContext = Substitute.For<IQueryContext>();
        queryContext.TenantId.Returns(1);
        await SearchFieldReindex.RequestAsync(publisher, queryContext, new AppTable { Id = 10, AppId = 2 }, F(1, 6, "Text"), CancellationToken.None, jobs);

        await publisher.Received(1).PublishAsync(Arg.Is<SearchIndexMessage>(m => m.Action == IndexAction.BackfillField && m.JobId == 7 && m.AfterId == 0),
            Arg.Any<CancellationToken>());
    }
}
