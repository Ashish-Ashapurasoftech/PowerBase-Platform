using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Azure.Search.Documents.Models;
using FluentAssertions;
using NSubstitute;
using PowerBase.API.Workers;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Search;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Search;

/// <summary>
/// Slots are stored once and never recomputed from position; ranges/durations are answered from SQL; filterable-only
/// fields keep their value in the index without becoming free-text searchable; indexing failures are classified.
/// </summary>
public class SearchSlotStabilityTests
{
    private static AppField F(long id, int fid, string type, bool searchable = true, bool filterable = true, string? slot = null, bool deleted = false) =>
        new() { Id = id, Fid = fid, TypeCode = type, IsSearchable = searchable, IsFilterable = filterable, SearchSlot = slot, IsDeleted = deleted };

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
        foreach (var type in new[] { "Number", "Date", "Boolean", "Text", "Currency", "DateTime", "Email" })
            for (var i = 0; i < 5; i++) fields.Add(F(id++, 5 + id, type));

        var assigned = SearchSlotResolver.AllocateMissingSlots(fields);

        assigned.Should().HaveCount(fields.Count);
        assigned.Select(a => a.Slot).Should().OnlyHaveUniqueItems();
        foreach (var (fieldId, slot) in assigned)
            slot.Should().StartWith(SearchSlotResolver.SlotPrefix(fields.Single(f => f.Id == fieldId).TypeCode) + "_");
    }

    [Fact]
    public void Wide_tables_get_no_slots()
    {
        var fields = Enumerable.Range(1, SearchSlotResolver.WideTableThreshold + 1).Select(i => F(i, 5 + i, "Text")).ToList();
        SearchSlotResolver.AllocateMissingSlots(fields).Should().BeEmpty();
    }

    [Theory]
    [InlineData("NumericRange")]
    [InlineData("DateRange")]
    [InlineData("Duration")]
    public void Ranges_and_durations_are_not_indexed_and_fall_back_to_SQL(string type)
    {
        var field = F(1, 6, type, slot: "date_01");
        var fields = new List<AppField> { field };

        SearchSlotResolver.IsSearchEligible(field).Should().BeFalse();
        SearchSlotResolver.ResolveSlotName(field, fields).Should().Be("f_6");
        SearchSlotResolver.AllocateMissingSlots(new List<AppField> { F(2, 7, type) }).Should().BeEmpty();
    }

    [Fact]
    public void ToSlotValue_converts_by_type_and_keeps_null_as_null()
    {
        SearchSlotResolver.ToSlotValue("Number", "12.5").Should().Be(12.5);
        SearchSlotResolver.ToSlotValue("Number", null).Should().BeNull();
        SearchSlotResolver.ToSlotValue("Boolean", "true").Should().Be(true);
        SearchSlotResolver.ToSlotValue("Date", "2026-10-05T00:00:00Z").Should().BeOfType<DateTimeOffset>();
        SearchSlotResolver.ToSlotValue("Date", "not a date").Should().BeNull();
        SearchSlotResolver.ToSlotValue("Text", 42).Should().Be("42");
        SearchSlotResolver.ToSlotValue("Text", DBNull.Value).Should().BeNull();
    }

    // ---- worker slot map

    [Fact]
    public void Slot_info_keeps_filterable_only_text_out_of_free_text_but_in_the_map()
    {
        var searchable = F(1, 6, "Text", searchable: true, filterable: true, slot: "str_01");
        var filterOnly = F(2, 7, "Text", searchable: false, filterable: true, slot: "str_02");
        var amount = F(3, 8, "Number", searchable: false, filterable: true, slot: "num_01");

        var info = SearchIndexerWorker.BuildSlotInfo(10, new List<AppField> { searchable, filterOnly, amount });

        info.SlotMap.Keys.Should().BeEquivalentTo(new long[] { 6, 7, 8 });
        info.SearchTextSlots.Should().BeEquivalentTo(new[] { "str_01" });
        info.SlotMap[8].Slot.Should().Be("num_01");
        info.TargetIndex.Should().BeNull();
    }

    [Fact]
    public void Slot_info_remembers_the_slot_of_a_field_that_was_switched_off_so_it_can_be_cleared()
    {
        var off = F(1, 6, "Number", searchable: false, filterable: false, slot: "num_04");

        var info = SearchIndexerWorker.BuildSlotInfo(10, new List<AppField> { off });

        info.SlotMap.Should().BeEmpty();
        info.StoredSlots[6].Slot.Should().Be("num_04");
        info.AllFids.Should().Contain(6);
    }

    [Fact]
    public void Slot_info_skips_unallocated_and_non_indexable_fields()
    {
        var unallocated = F(1, 6, "Text");
        var range = F(2, 7, "NumericRange", slot: "num_01");

        var info = SearchIndexerWorker.BuildSlotInfo(10, new List<AppField> { unallocated, range });

        info.SlotMap.Should().BeEmpty();
    }

    [Fact]
    public void Wide_table_slot_info_targets_the_tables_own_index()
    {
        var fields = Enumerable.Range(1, SearchSlotResolver.WideTableThreshold + 1).Select(i => F(i, 5 + i, "Text")).ToList();

        var info = SearchIndexerWorker.BuildSlotInfo(77, fields);

        info.IsWide.Should().BeTrue();
        info.TargetIndex.Should().Be("pb-tbl-77");
        info.SlotMap[6].Slot.Should().Be("f_6");
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
            new[] { new SearchIndexDocument(1, 2, 5, Guid.NewGuid(), new Dictionary<long, object?> { [6] = 10m }) }, plan, 6, isNullify: true);

        plan.Slot.Should().Be("num_02");
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
            new[] { new SearchIndexDocument(1, 2, 5, Guid.NewGuid(), new Dictionary<long, object?> { [6] = "10.50" }) }, plan, 6, isNullify: false);

        plan.Slot.Should().Be("num_01");
        docs.Single().SlotValues!["num_01"].Should().Be(10.5);
        docs.Single().SearchTextSlots.Should().BeEquivalentTo(new[] { "str_01" });
    }

    [Fact]
    public async Task A_field_without_a_slot_writes_nothing()
    {
        var repo = Substitute.For<IAppFieldRepository>();
        repo.ListByTableAsync(5, Arg.Any<CancellationToken>()).Returns(new List<AppField> { F(1, 6, "Number") });

        var plan = await FieldSlotSync.PlanAsync(repo, 5, 6, isNullify: false, CancellationToken.None);

        plan.Slot.Should().BeNull();
        FieldSlotSync.ToDocuments(new[] { new SearchIndexDocument(1, 2, 5, Guid.NewGuid(), new Dictionary<long, object?>()) }, plan, 6, false)
            .Should().BeEmpty();
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

    // ---- publisher (D05)

    [Fact]
    public void Publisher_flags_messages_over_256KB_and_describes_them_without_values()
    {
        ServiceBusPublisher.IsOversized(new byte[256 * 1024]).Should().BeFalse();
        ServiceBusPublisher.IsOversized(new byte[256 * 1024 + 1]).Should().BeTrue();

        var id = Guid.NewGuid();
        var text = ServiceBusPublisher.Describe(new PowerBase.Application.Common.Models.SearchIndexMessage
        {
            Action = PowerBase.Application.Common.Models.IndexAction.Upsert,
            TenantId = 7,
            TableId = 9,
            RecordPublicId = id,
            Payload = new Dictionary<string, object?> { ["6"] = "patient name" }
        });

        text.Should().Contain(id.ToString()).And.Contain("tenant 7").And.Contain("table 9");
        text.Should().NotContain("patient name");
    }
}
