using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Azure.Search.Documents.Models;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Search;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Search;

/// <summary>
/// Slots are stored once and never recomputed from position; ranges get two slots and durations a numeric one; the wide-table switch
/// is stored; stale messages are recognised; filterable-only fields keep their value in the index without becoming free-text searchable;
/// indexing failures are classified.
/// </summary>
public class SearchSlotStabilityTests
{
    private static AppField F(long id, int fid, string type, bool searchable = true, bool filterable = true,
        string? slot = null, string? end = null, bool deleted = false) =>
        new() { Id = id, Fid = fid, TypeCode = type, IsSearchable = searchable, IsFilterable = filterable, SearchSlot = slot, SearchSlotEnd = end, IsDeleted = deleted };

    private static List<AppField> Wide(int count) =>
        Enumerable.Range(1, count).Select(i => F(i, 5 + i, "Text")).ToList();

    private static void MarkWide(List<AppField> fields)
    {
        foreach (var f in fields) f.SearchSlot = $"f_{f.Fid}";
    }

    // ---- stable slots

    [Fact]
    public void Switching_an_early_field_off_does_not_move_later_fields()
    {
        // Before: slots followed position, so turning A off made B take num_01 while old documents still held B in num_02.
        var a = F(1, 6, "Number", slot: "num_01");
        var b = F(2, 7, "Number", slot: "num_02");
        var fields = new List<AppField> { a, b };
        SearchSlotResolver.ResolveSlotName(b, fields).Should().Be("num_02");

        a.IsSearchable = false; a.IsFilterable = false;

        SearchSlotResolver.ResolveSlotName(b, fields).Should().Be("num_02");
        SearchSlotResolver.ResolveSlotName(a, fields).Should().Be("f_6", "a field that is switched off is not in a slot");
        a.SearchSlot.Should().Be("num_01", "its slot stays reserved so it can be cleared and is not handed to another field");
    }

    [Fact]
    public void A_new_field_takes_the_lowest_free_slot_and_leaves_existing_slots_alone()
    {
        var fields = new List<AppField>
        {
            F(1, 6, "Number", slot: "num_01"),
            F(2, 7, "Number", slot: "num_03"),
            F(3, 8, "Number"),
            F(4, 9, "Text")
        };

        var assigned = SearchSlotResolver.AllocateMissingSlots(fields).ToDictionary(x => x.FieldId, x => x.Slot);

        assigned[3].Should().Be("num_02");
        assigned[4].Should().Be("str_01");
        assigned.Should().NotContainKey(1);
        assigned.Should().NotContainKey(2);
    }

    [Fact]
    public void A_slot_held_by_a_deleted_field_is_never_given_to_another_field()
    {
        var fields = new List<AppField>
        {
            F(1, 6, "Number", slot: "num_01", deleted: true),
            F(2, 7, "Number")
        };

        SearchSlotResolver.AllocateMissingSlots(fields).Single().Slot.Should().Be("num_02");
    }

    [Fact]
    public void A_full_pool_leaves_the_field_without_a_slot()
    {
        var fields = Enumerable.Range(1, SearchSlotResolver.MaxBooleanSlots)
            .Select(i => F(i, 5 + i, "Boolean", slot: SearchSlotResolver.SlotName("bool", i))).ToList();
        var extra = F(100, 100, "Boolean");
        fields.Add(extra);

        SearchSlotResolver.AllocateMissingSlots(fields).Should().BeEmpty();
        SearchSlotResolver.ResolveSlotName(extra, fields).Should().Be("f_100", "no slot means it is answered from SQL");
    }

    [Fact]
    public void Allocation_gives_every_eligible_field_a_distinct_slot_of_its_own_type()
    {
        var fields = new List<AppField>();
        var id = 1;
        foreach (var type in new[] { "Number", "Date", "Boolean", "Text", "Currency", "DateTime", "Email", "Duration" })
            for (var i = 0; i < 5; i++) fields.Add(F(id++, 5 + id, type));

        var assigned = SearchSlotResolver.AllocateMissingSlots(fields);

        assigned.Should().HaveCount(fields.Count);
        assigned.Select(a => a.Slot).Should().OnlyHaveUniqueItems();
        foreach (var a in assigned)
            a.Slot.Should().StartWith(SearchSlotResolver.SlotPrefix(fields.Single(f => f.Id == a.FieldId).TypeCode) + "_");
    }

    // ---- ranges, durations, time of day

    [Fact]
    public void A_range_gets_two_slots_of_its_family_and_both_are_reserved()
    {
        var fields = new List<AppField> { F(1, 6, "DateRange"), F(2, 7, "Date"), F(3, 8, "NumericRange") };

        var assigned = SearchSlotResolver.AllocateMissingSlots(fields).ToDictionary(a => a.FieldId);

        assigned[1].Slot.Should().Be("date_01");
        assigned[1].EndSlot.Should().Be("date_02");
        assigned[2].Slot.Should().Be("date_03", "the range's second slot is not available to another date field");
        assigned[2].EndSlot.Should().BeNull();
        assigned[3].Slot.Should().Be("num_01");
        assigned[3].EndSlot.Should().Be("num_02");
    }

    [Fact]
    public void A_range_that_only_has_its_first_slot_gets_just_the_second()
    {
        // migration 084 gave ranges one slot; the second one is allocated later without moving the first
        var fields = new List<AppField> { F(1, 6, "NumericRange", slot: "num_01") };

        var a = SearchSlotResolver.AllocateMissingSlots(fields).Single();

        a.Slot.Should().Be("num_01");
        a.EndSlot.Should().Be("num_02");
    }

    [Fact]
    public void Without_the_end_column_ranges_are_left_alone()
    {
        var fields = new List<AppField> { F(1, 6, "NumericRange", slot: "num_01"), F(2, 7, "Number") };

        var assigned = SearchSlotResolver.AllocateMissingSlots(fields, supportsEndSlots: false);

        assigned.Should().ContainSingle().Which.FieldId.Should().Be(2);
    }

    [Fact]
    public void A_duration_is_numeric_and_moves_out_of_the_date_slot_it_was_given_before()
    {
        SearchSlotResolver.IsNumeric("Duration").Should().BeTrue("it is stored as whole minutes (INT)");
        SearchSlotResolver.IsDate("Duration").Should().BeFalse();

        var duration = F(1, 6, "Duration", slot: "date_01");
        var assigned = SearchSlotResolver.AllocateMissingSlots(new List<AppField> { duration }).Single();

        assigned.Slot.Should().Be("num_01");
        assigned.OldSlot.Should().Be("date_01");
        SearchSlotResolver.ResolveSlotName(duration, new List<AppField> { duration }).Should().Be("f_6", "a slot of the wrong family is not used");
    }

    [Fact]
    public void A_time_of_day_is_not_indexed_and_its_filters_stay_in_SQL()
    {
        var time = F(1, 6, "Time");
        SearchSlotResolver.IsSearchEligible(time).Should().BeFalse();
        SearchSlotResolver.AllocateMissingSlots(new List<AppField> { time }).Should().BeEmpty();
    }

    [Theory]
    [InlineData("NumericRange")]
    [InlineData("DateRange")]
    [InlineData("Duration")]
    [InlineData("Time")]
    public void Filters_on_ranges_durations_and_time_stay_in_SQL(string type) =>
        SearchSlotResolver.IsAiFilterableType(type).Should().BeFalse();

    [Theory]
    [InlineData("Text")]
    [InlineData("Number")]
    [InlineData("Date")]
    [InlineData("Boolean")]
    public void Ordinary_types_are_filtered_by_the_index(string type) =>
        SearchSlotResolver.IsAiFilterableType(type).Should().BeTrue();

    [Fact]
    public void A_range_value_is_split_from_every_shape_it_arrives_in()
    {
        SearchSlotResolver.SplitRange("""{"start":"2026-01-01","end":"2026-02-01"}""").Should().Be(((object?)"2026-01-01", (object?)"2026-02-01"));
        SearchSlotResolver.SplitRange(JsonDocument.Parse("""{"start":5,"end":9}""").RootElement.Clone()).Should().Be(((object?)"5", (object?)"9"));
        SearchSlotResolver.SplitRange(new Dictionary<string, object?> { ["start"] = 1, ["end"] = 2 }).Should().Be(((object?)1, (object?)2));
        SearchSlotResolver.SplitRange("""{"start":"x","end":""}""").Should().Be(((object?)"x", (object?)null));
        SearchSlotResolver.SplitRange(7).Should().Be(((object?)7, (object?)null));
        SearchSlotResolver.SplitRange(null).Should().Be(((object?)null, (object?)null));
        SearchSlotResolver.SplitRange("{not json").Start.Should().Be("{not json");
    }

    [Fact]
    public void A_range_writes_both_of_its_columns_and_clearing_it_clears_both()
    {
        var slot = new SlotRef("num_01", "NumericRange", "num_02");
        var values = new Dictionary<string, object?>();

        SearchSlotResolver.AddSlotValues(values, slot, """{"start":"10","end":"20.5"}""");
        values["num_01"].Should().Be(10.0);
        values["num_02"].Should().Be(20.5);

        SearchSlotResolver.AddClearedSlots(values, slot);
        values["num_01"].Should().BeNull();
        values["num_02"].Should().BeNull();
    }

    [Fact]
    public void ToSlotValue_converts_by_type_and_keeps_null_as_null()
    {
        SearchSlotResolver.ToSlotValue("Number", "12.5").Should().Be(12.5);
        SearchSlotResolver.ToSlotValue("Duration", 90).Should().Be(90.0);
        SearchSlotResolver.ToSlotValue("Number", null).Should().BeNull();
        SearchSlotResolver.ToSlotValue("Boolean", "true").Should().Be(true);
        SearchSlotResolver.ToSlotValue("Date", "2026-10-05T00:00:00Z").Should().BeOfType<DateTimeOffset>();
        SearchSlotResolver.ToSlotValue("Date", "not a date").Should().BeNull();
        SearchSlotResolver.ToSlotValue("Text", 42).Should().Be("42");
        SearchSlotResolver.ToSlotValue("Text", DBNull.Value).Should().BeNull();
    }

    // ---- where a table's fields live

    [Fact]
    public void Slot_info_keeps_filterable_only_text_out_of_free_text_but_in_the_map()
    {
        var searchable = F(1, 6, "Text", searchable: true, filterable: true, slot: "str_01");
        var filterOnly = F(2, 7, "Text", searchable: false, filterable: true, slot: "str_02");
        var amount = F(3, 8, "Number", searchable: false, filterable: true, slot: "num_01");

        var info = SearchTableSlotInfo.From(10, new List<AppField> { searchable, filterOnly, amount });

        info.SlotMap.Keys.Should().BeEquivalentTo(new long[] { 6, 7, 8 });
        info.SearchTextSlots.Should().BeEquivalentTo(new[] { "str_01" });
        info.SlotMap[8].Slot.Should().Be("num_01");
        info.TargetIndex.Should().BeNull();
        info.MigratingToWide.Should().BeFalse();
    }

    [Fact]
    public void Slot_info_remembers_the_slot_of_a_field_that_was_switched_off_so_it_can_be_cleared()
    {
        var off = F(1, 6, "Number", searchable: false, filterable: false, slot: "num_04");

        var info = SearchTableSlotInfo.From(10, new List<AppField> { off });

        info.SlotMap.Should().BeEmpty();
        info.StoredSlots[6].Slot.Should().Be("num_04");
        info.AllFids.Should().Contain(6);
    }

    [Fact]
    public void Slot_info_skips_unallocated_fields_and_carries_a_ranges_second_slot()
    {
        var unallocated = F(1, 6, "Text");
        var range = F(2, 7, "NumericRange", slot: "num_01", end: "num_02");

        var info = SearchTableSlotInfo.From(10, new List<AppField> { unallocated, range });

        info.SlotMap.Should().ContainKey(7);
        info.SlotMap.Should().NotContainKey(6);
        info.SlotMap[7].EndSlot.Should().Be("num_02");
    }

    [Fact]
    public void A_table_over_the_limit_keeps_using_the_shared_index_until_it_is_marked_wide()
    {
        var fields = Wide(SearchSlotResolver.WideTableThreshold + 1);

        SearchSlotResolver.IsWideTable(fields).Should().BeFalse("routing is stored, not recomputed from the field count");
        SearchSlotResolver.ShouldBeWide(fields).Should().BeTrue();

        var migrating = SearchTableSlotInfo.From(77, fields);
        migrating.IsWide.Should().BeFalse();
        migrating.TargetIndex.Should().BeNull();
        migrating.MigratingToWide.Should().BeTrue();
        migrating.WideIndexName(77).Should().Be("pb-tbl-77");
        migrating.WideSlotMap![6].Slot.Should().Be("f_6");

        MarkWide(fields);

        SearchSlotResolver.IsWideTable(fields).Should().BeTrue();
        SearchSlotResolver.ShouldBeWide(fields).Should().BeFalse();
        var wide = SearchTableSlotInfo.From(77, fields);
        wide.IsWide.Should().BeTrue();
        wide.TargetIndex.Should().Be("pb-tbl-77");
        wide.SlotMap[6].Slot.Should().Be("f_6");
        wide.MigratingToWide.Should().BeFalse();
    }

    [Fact]
    public void A_wide_table_gets_no_new_slots_and_ranges_use_f_columns()
    {
        var fields = Wide(5);
        fields.Add(F(99, 99, "DateRange"));
        MarkWide(fields);

        SearchSlotResolver.AllocateMissingSlots(fields).Should().BeEmpty();
        var info = SearchTableSlotInfo.From(5, fields);
        info.SlotMap[99].Slot.Should().Be("f_99");
        info.SlotMap[99].EndSlot.Should().Be("f_99_e");
    }

    // ---- documents

    [Fact]
    public void While_a_table_is_moving_every_record_is_written_to_both_indexes()
    {
        var fields = Wide(SearchSlotResolver.WideTableThreshold + 1);
        fields[0].SearchSlot = "str_01"; // already has a shared slot
        var info = SearchTableSlotInfo.From(77, fields);
        var id = Guid.NewGuid();

        var docs = SearchIndexDocumentBuilder.Build(info, 1, 2, 77, id, new Dictionary<long, object?> { [6] = "abc" }, 123);

        docs.Should().HaveCount(2);
        docs[0].CustomIndexName.Should().BeNull();
        docs[0].SlotValues!["str_01"].Should().Be("abc");
        docs[1].CustomIndexName.Should().Be("pb-tbl-77");
        docs[1].SlotValues!["f_6"].Should().Be("abc");
        docs.Should().OnlyContain(d => d.SourceVersion == 123);
    }

    [Fact]
    public void Clearing_a_range_field_clears_both_columns_in_the_table_it_was_stored_in()
    {
        var off = F(1, 6, "NumericRange", searchable: false, filterable: false, slot: "num_03", end: "num_04");
        var info = SearchTableSlotInfo.From(10, new List<AppField> { off });

        var docs = SearchIndexDocumentBuilder.BuildFieldChange(info, 1, 2, 10, Guid.NewGuid(), new Dictionary<long, object?>(), 6, isNullify: true);

        var slots = docs.Single().SlotValues!;
        slots.Should().ContainKey("num_03").WhoseValue.Should().BeNull();
        slots.Should().ContainKey("num_04").WhoseValue.Should().BeNull();
    }

    [Fact]
    public void Merging_documents_for_one_record_applies_them_oldest_first_and_keeps_the_highest_version()
    {
        var id = Guid.NewGuid();
        SearchIndexDocument Doc(string slot, object? value, long version, string? index = null) =>
            new(1, 2, 3, id, new Dictionary<long, object?> { [6] = value },
                new Dictionary<string, object?> { [slot] = value }, index, null, version);

        var merged = SearchIndexDocumentBuilder.MergeByRecord(new[]
        {
            Doc("str_01", "old", 10),
            Doc("str_02", "other field", 15),
            Doc("str_01", "new", 20),
            Doc("f_6", "in the wide index", 20, "pb-tbl-3")
        });

        merged.Should().HaveCount(2, "one document per record and index");
        var shared = merged.Single(d => d.CustomIndexName == null);
        shared.SlotValues!["str_01"].Should().Be("new");
        shared.SlotValues!["str_02"].Should().Be("other field", "a field only the older message set is not lost");
        shared.SourceVersion.Should().Be(20);
        merged.Single(d => d.CustomIndexName == "pb-tbl-3").SlotValues!["f_6"].Should().Be("in the wide index");
    }

    // ---- moving a table to its own index

    [Fact]
    public async Task The_move_starts_only_for_a_table_that_has_outgrown_the_shared_index()
    {
        var repo = Substitute.For<IAppFieldRepository>();
        var publisher = Substitute.For<IMessagePublisher>();
        var search = Substitute.For<IAzureSearchService>();
        search.CanUseWideIndexAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);

        repo.ListByTableAsync(7, Arg.Any<CancellationToken>()).Returns(Wide(SearchSlotResolver.WideTableThreshold + 1));
        (await SearchWideMigration.StartIfNeededAsync(repo, publisher, search, 1, 2, 7, CancellationToken.None)).Should().BeTrue();
        await publisher.Received(1).PublishAsync(
            Arg.Is<SearchIndexMessage>(m => m.Action == IndexAction.ReindexTable && m.TableId == 7 && m.TenantId == 1 && m.Page == 1), Arg.Any<CancellationToken>());

        publisher.ClearReceivedCalls();
        repo.ListByTableAsync(8, Arg.Any<CancellationToken>()).Returns(Wide(10));
        (await SearchWideMigration.StartIfNeededAsync(repo, publisher, search, 1, 2, 8, CancellationToken.None)).Should().BeFalse();
        await publisher.DidNotReceive().PublishAsync(Arg.Any<SearchIndexMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_move_ends_by_marking_the_table_wide_and_removing_the_old_documents_and_only_once()
    {
        var repo = Substitute.For<IAppFieldRepository>();
        var search = Substitute.For<IAzureSearchService>();
        search.CanUseWideIndexAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(true);
        var growing = Wide(SearchSlotResolver.WideTableThreshold + 1);
        repo.ListByTableAsync(7, Arg.Any<CancellationToken>()).Returns(growing);

        (await SearchWideMigration.FinalizeAsync(repo, search, 1, 7, CancellationToken.None)).Should().BeTrue();
        await repo.Received(1).MarkTableWideAsync(7, Arg.Any<CancellationToken>());
        await search.Received(1).PurgeTableFromSharedIndexesAsync(1, 7, Arg.Any<CancellationToken>());

        // a second, duplicate finalize message (the table is marked by now) must do nothing
        MarkWide(growing);
        repo.ClearReceivedCalls(); search.ClearReceivedCalls();
        (await SearchWideMigration.FinalizeAsync(repo, search, 1, 7, CancellationToken.None)).Should().BeFalse();
        await repo.DidNotReceive().MarkTableWideAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
        await search.DidNotReceive().PurgeTableFromSharedIndexesAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    // ---- field switched on/off

    [Fact]
    public async Task Switching_a_field_off_clears_the_slot_it_was_stored_in()
    {
        var repo = Substitute.For<IAppFieldRepository>();
        repo.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            F(1, 6, "Number", searchable: false, filterable: false, slot: "num_02")
        });

        var plan = await FieldSlotSync.PlanAsync(repo, 5, 6, isNullify: true, CancellationToken.None);
        var docs = FieldSlotSync.ToDocuments(
            new[] { new SearchIndexDocument(1, 2, 5, Guid.NewGuid(), new Dictionary<long, object?> { [6] = 10m }) }, plan);

        plan.HasTarget.Should().BeTrue();
        docs.Single().SlotValues!.Should().ContainKey("num_02").WhoseValue.Should().BeNull();
        await repo.Received().EnsureSearchSlotsAsync(5, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Switching_a_field_on_writes_typed_values_and_marks_only_searchable_text_for_free_text()
    {
        var repo = Substitute.For<IAppFieldRepository>();
        repo.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            F(1, 6, "Number", searchable: false, filterable: true, slot: "num_01"),
            F(2, 7, "Text", searchable: true, filterable: true, slot: "str_01")
        });

        var plan = await FieldSlotSync.PlanAsync(repo, 5, 6, isNullify: false, CancellationToken.None);
        var docs = FieldSlotSync.ToDocuments(
            new[] { new SearchIndexDocument(1, 2, 5, Guid.NewGuid(), new Dictionary<long, object?> { [6] = "10.50" }) }, plan);

        docs.Single().SlotValues!["num_01"].Should().Be(10.5);
        docs.Single().SearchTextSlots.Should().BeEquivalentTo(new[] { "str_01" });
    }

    [Fact]
    public async Task A_field_without_a_slot_writes_nothing()
    {
        var repo = Substitute.For<IAppFieldRepository>();
        repo.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(new List<AppField> { F(1, 6, "Number") });

        var plan = await FieldSlotSync.PlanAsync(repo, 5, 6, isNullify: false, CancellationToken.None);

        plan.HasTarget.Should().BeFalse();
        FieldSlotSync.ToDocuments(new[] { new SearchIndexDocument(1, 2, 5, Guid.NewGuid(), new Dictionary<long, object?>()) }, plan)
            .Should().BeEmpty();
    }

    [Fact]
    public async Task Switching_on_a_range_field_writes_both_ends()
    {
        var repo = Substitute.For<IAppFieldRepository>();
        repo.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            F(1, 6, "DateRange", slot: "date_01", end: "date_02")
        });

        var plan = await FieldSlotSync.PlanAsync(repo, 5, 6, isNullify: false, CancellationToken.None);
        var docs = FieldSlotSync.ToDocuments(new[]
        {
            new SearchIndexDocument(1, 2, 5, Guid.NewGuid(), new Dictionary<long, object?> { [6] = new Dictionary<string, object?> { ["start"] = "2026-01-01", ["end"] = "2026-03-01" } })
        }, plan);

        var slots = docs.Single().SlotValues!;
        ((DateTimeOffset)slots["date_01"]!).Month.Should().Be(1);
        ((DateTimeOffset)slots["date_02"]!).Month.Should().Be(3);
    }

    // ---- per-document results (D07)

    [Theory]
    [InlineData(400, false)]
    [InlineData(404, false)]
    [InlineData(409, true)]
    [InlineData(422, true)]
    [InlineData(429, true)]
    [InlineData(503, true)]
    public void Failed_documents_are_classified_as_retryable_or_permanent(int status, bool retryable)
    {
        var results = new[]
        {
            SearchModelFactory.IndexingResult("ok-1", null, true, 200),
            SearchModelFactory.IndexingResult("bad-1", "term too large", false, status)
        };

        var failures = IndexingResultClassifier.Classify(results);

        var failure = failures.Should().ContainSingle().Subject;
        failure.Key.Should().Be("bad-1");
        failure.Status.Should().Be(status);
        failure.Error.Should().Be("term too large");
        failure.Retryable.Should().Be(retryable);
    }

    [Fact]
    public void A_fully_successful_batch_has_no_failures()
    {
        IndexingResultClassifier.Classify(new[] { SearchModelFactory.IndexingResult("a", null, true, 201) }).Should().BeEmpty();
    }

    [Theory]
    [InlineData(429, true)]
    [InlineData(503, true)]
    [InlineData(409, false)]
    [InlineData(400, false)]
    public void Only_throttling_statuses_are_retried_within_the_same_call(int status, bool throttle) =>
        IndexingResultClassifier.IsThrottle(status).Should().Be(throttle);

    // ---- publisher and retries (D05)

    [Fact]
    public void Service_Bus_calls_are_retried_ten_times_with_growing_pauses()
    {
        var options = ServiceBusRetry.ClientOptions().RetryOptions;

        options.MaxRetries.Should().Be(10);
        options.Mode.Should().Be(Azure.Messaging.ServiceBus.ServiceBusRetryMode.Exponential);
        options.MaxDelay.Should().BeGreaterThan(options.Delay);
        ServiceBusRetry.MaxAttempts.Should().Be(10, "the indexer dead-letters after the same number of deliveries the broker allows by default");
    }

    [Fact]
    public void Publisher_flags_messages_over_256KB_and_describes_them_without_values()
    {
        ServiceBusPublisher.IsOversized(new byte[256 * 1024]).Should().BeFalse();
        ServiceBusPublisher.IsOversized(new byte[256 * 1024 + 1]).Should().BeTrue();

        var id = Guid.NewGuid();
        var text = ServiceBusPublisher.Describe(new SearchIndexMessage
        {
            Action = IndexAction.Upsert,
            TenantId = 7,
            TableId = 9,
            RecordPublicId = id,
            Payload = new Dictionary<string, object?> { ["6"] = "patient name" }
        });

        text.Should().Contain(id.ToString()).And.Contain("tenant 7").And.Contain("table 9");
        text.Should().NotContain("patient name");
    }
}
