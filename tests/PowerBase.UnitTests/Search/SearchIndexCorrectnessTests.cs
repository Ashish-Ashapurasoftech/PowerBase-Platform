using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Search;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Search;

/// <summary>
/// What reaches the index stays equal to the database: quick-search text is rebuilt from the whole record (an edit of one field
/// never drops the others, cleared or switched-off text is removed), a field change rewrites every record and empties the columns the
/// field no longer uses, a field job is never lost when the first page fails, fields past a column family's slots move the table to
/// its own index, and every change gets its own queue message id.
/// </summary>
public class SearchIndexCorrectnessTests
{
    private static AppField F(long id, int fid, string type, bool searchable = true, bool filterable = true, string? slot = null) =>
        new() { Id = id, Fid = fid, TypeCode = type, IsSearchable = searchable, IsFilterable = filterable, SearchSlot = slot };

    private static readonly IReadOnlySet<string> SharedColumns = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "search_content", "src_version", "str_01", "str_02", "str_03", "str_05", "num_01", "num_02"
    };

    private static object? SearchContent(SearchIndexDocument doc)
    {
        var sent = AzureSearchService.ToSearchDocument(doc, "pb-shard-001", SharedColumns);
        return sent.TryGetValue("search_content", out var text) ? text : "<not sent>";
    }

    // ---- bug 1: an edit of one field keeps the record's other text in quick search

    [Fact]
    public void A_record_read_in_full_rebuilds_quick_search_text_from_every_searchable_field()
    {
        var info = SearchTableSlotInfo.From(10, [F(1, 6, "Text", slot: "str_01"), F(2, 7, "Text", slot: "str_02"), F(3, 8, "Text", slot: "str_03")]);
        // The grid edit changed only the city; the worker reads the whole record after the edit committed.
        var row = new Dictionary<long, object?> { [6] = "John Smith", [7] = "London", [8] = "VIP customer" };

        var doc = SearchIndexDocumentBuilder.Build(info, 1, 2, 10, Guid.NewGuid(), row, 5, complete: true).Single();

        SearchContent(doc).Should().Be("John Smith London VIP customer", "the name and notes stay findable after the city is edited");
    }

    // ---- bug 14: no field value in the queue

    [Fact]
    public void A_queue_message_carries_ids_only_never_field_values()
    {
        var message = new SearchIndexMessage
        {
            Action = IndexAction.Upsert, TenantId = 1, TableId = 2, RecordPublicId = Guid.NewGuid(),
            Payload = new Dictionary<string, object?> { ["6"] = "123-45-6789" } // a decrypted SSN
        };

        var body = System.Text.Json.JsonSerializer.Serialize(message);

        body.Should().NotContain("123-45-6789").And.NotContain("Payload");
        body.Should().Contain(message.RecordPublicId.ToString());
        System.Text.Json.JsonSerializer.Deserialize<SearchIndexMessage>(body)!.Payload.Should().BeNull();
    }

    [Fact]
    public void A_message_queued_by_an_older_version_with_values_is_read_without_them()
    {
        var id = Guid.NewGuid();
        var oldBody = "{\"Action\":0,\"TenantId\":1,\"TableId\":2,\"RecordPublicId\":\"" + id + "\",\"Version\":5,\"Payload\":{\"6\":\"secret\"}}";

        var message = System.Text.Json.JsonSerializer.Deserialize<SearchIndexMessage>(oldBody)!;

        message.RecordPublicId.Should().Be(id);
        message.Payload.Should().BeNull("the worker reads the record from the database instead");
    }

    // ---- bug 14: a write's message is sent only once the write is committed

    private sealed class CountingSend
    {
        public int Sent;
        public Task Send() { Sent++; return Task.CompletedTask; }
    }

    [Fact]
    public void A_write_without_a_transaction_has_committed_and_is_sent_at_once()
    {
        var send = new CountingSend();

        PowerBase.Infrastructure.Repositories.RecordRepository.SendWhenCommitted(null, hasOutbox: true, null, send.Send);

        send.Sent.Should().Be(1);
    }

    [Fact]
    public async Task A_write_in_the_requests_unit_of_work_is_sent_only_after_it_commits()
    {
        var inner = Substitute.For<ITenantUnitOfWork>();
        var transaction = Substitute.For<System.Data.IDbTransaction>();
        inner.Transaction.Returns(transaction);
        var uow = new PowerBase.Infrastructure.UOW.TriggerPublishingTenantUnitOfWork(inner);
        var send = new CountingSend();

        PowerBase.Infrastructure.Repositories.RecordRepository.SendWhenCommitted(transaction, hasOutbox: true, uow, send.Send);
        send.Sent.Should().Be(0, "the transaction is still open: the indexer would read the record as it was");

        await uow.CommitAsync();
        send.Sent.Should().Be(1);
    }

    [Fact]
    public async Task A_write_in_a_unit_of_work_that_rolls_back_is_never_sent()
    {
        var inner = Substitute.For<ITenantUnitOfWork>();
        var transaction = Substitute.For<System.Data.IDbTransaction>();
        inner.Transaction.Returns(transaction);
        var uow = new PowerBase.Infrastructure.UOW.TriggerPublishingTenantUnitOfWork(inner);
        var send = new CountingSend();

        PowerBase.Infrastructure.Repositories.RecordRepository.SendWhenCommitted(transaction, hasOutbox: true, uow, send.Send);
        await uow.RollbackAsync();
        await uow.CommitAsync();

        send.Sent.Should().Be(0);
    }

    [Fact]
    public void A_write_in_another_transaction_is_left_to_the_outbox_relay()
    {
        var send = new CountingSend();

        PowerBase.Infrastructure.Repositories.RecordRepository.SendWhenCommitted(Substitute.For<System.Data.IDbTransaction>(), hasOutbox: true, null, send.Send);

        send.Sent.Should().Be(0, "nothing tells when that transaction commits; its outbox row is re-published once due");
    }

    // ---- bug 2: cleared or switched-off text leaves quick search

    [Fact]
    public void A_full_record_with_no_searchable_text_left_empties_quick_search_text()
    {
        var info = SearchTableSlotInfo.From(10, [F(1, 6, "Text", slot: "str_01")]);
        var row = new Dictionary<long, object?> { [6] = null }; // the user cleared Notes

        var doc = SearchIndexDocumentBuilder.Build(info, 1, 2, 10, Guid.NewGuid(), row, 5, complete: true).Single();

        var sent = AzureSearchService.ToSearchDocument(doc, "pb-shard-001", SharedColumns);
        sent.Should().ContainKey("search_content").WhoseValue.Should().BeNull("an explicit null clears the old text");
        sent.Should().ContainKey("str_01").WhoseValue.Should().BeNull();
    }

    [Fact]
    public void A_partial_document_never_empties_quick_search_text()
    {
        var info = SearchTableSlotInfo.From(10, [F(1, 6, "Text", slot: "str_01")]);

        var doc = SearchIndexDocumentBuilder.Build(info, 1, 2, 10, Guid.NewGuid(), new Dictionary<long, object?> { [6] = null }, 5).Single();

        SearchContent(doc).Should().Be("<not sent>", "part of a record cannot tell what text the rest still holds");
    }

    [Fact]
    public void Switching_searchable_off_removes_the_fields_text_from_quick_search()
    {
        // Field 6 is switched off (no longer eligible), so the database read no longer lists it.
        var fields = new List<AppField> { F(1, 6, "Text", searchable: false, filterable: false, slot: "str_01"), F(2, 7, "Text", slot: "str_02") };
        var plan = new FieldSlotSync.Plan(SearchTableSlotInfo.From(10, fields), 6, IsNullify: true);
        var record = new SearchIndexDocument(1, 2, 10, Guid.NewGuid(), new Dictionary<long, object?> { [7] = "Paris" });

        var doc = FieldSlotSync.ToDocuments([record], plan).Single();

        doc.SlotValues!.Should().ContainKey("str_01").WhoseValue.Should().BeNull();
        SearchContent(doc).Should().Be("Paris");
    }

    [Fact]
    public void Filterable_only_text_stays_out_of_quick_search_in_a_full_rebuild()
    {
        var info = SearchTableSlotInfo.From(10, [F(1, 6, "Text", slot: "str_01"), F(2, 7, "Text", searchable: false, slot: "str_02")]);

        var doc = SearchIndexDocumentBuilder.Build(info, 1, 2, 10, Guid.NewGuid(),
            new Dictionary<long, object?> { [6] = "visible", [7] = "internal code" }, null, complete: true).Single();

        SearchContent(doc).Should().Be("visible");
        doc.SlotValues!["str_02"].Should().Be("internal code", "it is still filterable");
    }

    [Fact]
    public void Merging_a_full_document_with_a_later_partial_one_stays_full()
    {
        var id = Guid.NewGuid();
        var full = new SearchIndexDocument(1, 2, 3, id, new Dictionary<long, object?>(), new Dictionary<string, object?> { ["str_01"] = "a", ["str_02"] = "b" },
            null, new HashSet<string> { "str_01", "str_02" }, 10, CompleteText: true);
        var partial = new SearchIndexDocument(1, 2, 3, id, new Dictionary<long, object?>(), new Dictionary<string, object?> { ["str_02"] = "c" },
            null, new HashSet<string> { "str_01", "str_02" }, 20);

        var merged = SearchIndexDocumentBuilder.MergeByRecord([full, partial]).Single();

        merged.CompleteText.Should().BeTrue();
        SearchContent(merged).Should().Be("a c");
    }

    // ---- bug 7: Searchable alone changing on a filterable field

    [Fact]
    public void Switching_searchable_on_for_a_filterable_field_adds_its_text_to_quick_search()
    {
        var fields = new List<AppField> { F(1, 6, "Text", searchable: true, slot: "str_01"), F(2, 7, "Text", slot: "str_02") };
        var plan = new FieldSlotSync.Plan(SearchTableSlotInfo.From(10, fields), 6, IsNullify: false);
        var record = new SearchIndexDocument(1, 2, 10, Guid.NewGuid(), new Dictionary<long, object?> { [6] = "Germany", [7] = "Berlin" });

        var doc = FieldSlotSync.ToDocuments([record], plan).Single();

        plan.Clear.Should().BeNull("a backfill clears nothing");
        SearchContent(doc).Should().Be("Germany Berlin");
    }

    // ---- bugs 8 and 9: a type change writes the new column and empties the old one

    [Fact]
    public void A_type_change_to_another_family_writes_the_new_column_and_empties_the_old_one()
    {
        var fields = new List<AppField> { F(1, 6, "Number", slot: "num_01") }; // was Text in str_05
        var plan = new FieldSlotSync.Plan(SearchTableSlotInfo.From(10, fields), 6, IsNullify: false, new SlotRef("str_05", "Text"));
        var record = new SearchIndexDocument(1, 2, 10, Guid.NewGuid(), new Dictionary<long, object?> { [6] = 42m });

        var doc = FieldSlotSync.ToDocuments([record], plan).Single();

        doc.SlotValues!["num_01"].Should().Be(42.0);
        doc.SlotValues.Should().ContainKey("str_05").WhoseValue.Should().BeNull("the old column held the old values in every document");
        doc.CompleteText.Should().BeTrue();
    }

    [Fact]
    public void A_type_change_within_the_family_keeps_the_column()
    {
        var fields = new List<AppField> { F(1, 6, "Currency", slot: "num_01") }; // Number to Currency: same column
        var plan = new FieldSlotSync.Plan(SearchTableSlotInfo.From(10, fields), 6, IsNullify: false, new SlotRef("num_01", "Number"));
        var record = new SearchIndexDocument(1, 2, 10, Guid.NewGuid(), new Dictionary<long, object?> { [6] = 7m });

        FieldSlotSync.ToDocuments([record], plan).Single().SlotValues!["num_01"].Should().Be(7.0);
    }

    [Fact]
    public void A_field_that_became_a_formula_has_its_column_emptied_even_though_it_keeps_the_slot()
    {
        // Formula_Number stays in the numeric family, but a formula has no stored value: the database read never lists it.
        var fields = new List<AppField> { F(1, 6, "Formula_Number", slot: "num_01") };
        var plan = new FieldSlotSync.Plan(SearchTableSlotInfo.From(10, fields), 6, IsNullify: true, new SlotRef("num_01", "Number"));
        var record = new SearchIndexDocument(1, 2, 10, Guid.NewGuid(), new Dictionary<long, object?>());

        FieldSlotSync.ToDocuments([record], plan).Single().SlotValues!.Should().ContainKey("num_01").WhoseValue.Should().BeNull();
    }

    [Fact]
    public void An_emptied_column_that_another_field_uses_now_keeps_that_fields_value()
    {
        // str_05 was freed by field 6's type change and has already been given to the new field 9.
        var fields = new List<AppField> { F(1, 6, "Number", slot: "num_01"), F(2, 9, "Text", slot: "str_05") };
        var plan = new FieldSlotSync.Plan(SearchTableSlotInfo.From(10, fields), 6, IsNullify: false, new SlotRef("str_05", "Text"));
        var record = new SearchIndexDocument(1, 2, 10, Guid.NewGuid(), new Dictionary<long, object?> { [6] = 1m, [9] = "North" });

        FieldSlotSync.ToDocuments([record], plan).Single().SlotValues!["str_05"].Should().Be("North");
    }

    [Fact]
    public void A_deleted_fields_text_leaves_quick_search_when_its_column_is_emptied()
    {
        // The deleted field is gone from the table; the message names its column.
        var fields = new List<AppField> { F(2, 7, "Text", slot: "str_02") };
        var plan = new FieldSlotSync.Plan(SearchTableSlotInfo.From(10, fields), 6, IsNullify: true, new SlotRef("str_01", string.Empty));
        var record = new SearchIndexDocument(1, 2, 10, Guid.NewGuid(), new Dictionary<long, object?> { [7] = "kept" });

        var doc = FieldSlotSync.ToDocuments([record], plan).Single();

        doc.SlotValues!.Should().ContainKey("str_01").WhoseValue.Should().BeNull();
        SearchContent(doc).Should().Be("kept");
    }

    // ---- bug 3: the worker's cached map knows which fields were eligible when it was loaded

    [Fact]
    public void Slot_info_lists_the_fields_that_were_eligible_when_it_was_loaded()
    {
        var info = SearchTableSlotInfo.From(10, [F(1, 6, "Text", slot: "str_01"), F(2, 7, "Text", searchable: false, filterable: false)]);

        info.EligibleFids.Should().BeEquivalentTo(new long[] { 6 });
        info.AllFids.Should().BeEquivalentTo(new long[] { 6, 7 }, "a known field is not necessarily an indexed one");
    }

    // ---- bug 10: fields past a column family's slots

    [Fact]
    public void More_searchable_text_fields_than_text_slots_moves_the_table_to_its_own_index()
    {
        var fields = Enumerable.Range(1, SearchSlotResolver.MaxStringSlots + 1).Select(i => F(i, 5 + i, "Text")).ToList();

        fields.Count.Should().BeLessThan(SearchSlotResolver.WideTableThreshold);
        SearchSlotResolver.ShouldBeWide(fields).Should().BeTrue("the 61st text field would otherwise never be found by quick search");
        SearchSlotResolver.ShouldBeWide(fields.Take(SearchSlotResolver.MaxStringSlots).ToList()).Should().BeFalse();
    }

    [Fact]
    public void A_range_field_counts_for_both_of_its_slots()
    {
        var fields = Enumerable.Range(1, SearchSlotResolver.MaxDateSlots / 2).Select(i => F(i, 5 + i, "DateRange")).ToList();
        SearchSlotResolver.ShouldBeWide(fields).Should().BeFalse();

        fields.Add(F(100, 200, "Date"));
        SearchSlotResolver.ShouldBeWide(fields).Should().BeTrue();
    }

    // ---- bug 13: one queue message id per change

    [Fact]
    public void Two_edits_of_one_record_get_different_message_ids_and_a_resend_keeps_its_id()
    {
        var record = Guid.NewGuid();
        var first = new SearchIndexMessage { Action = IndexAction.Upsert, TenantId = 1, TableId = 2, RecordPublicId = record, Version = 1000 };
        var second = new SearchIndexMessage { Action = IndexAction.Upsert, TenantId = 1, TableId = 2, RecordPublicId = record, Version = 2000 };
        var resend = new SearchIndexMessage { Action = IndexAction.Upsert, TenantId = 1, TableId = 2, RecordPublicId = record, Version = 1000 };

        ServiceBusPublisher.MessageIdFor(first).Should().NotBe(ServiceBusPublisher.MessageIdFor(second));
        ServiceBusPublisher.MessageIdFor(resend).Should().Be(ServiceBusPublisher.MessageIdFor(first));
    }

    // ---- bugs 4, 5 and 15: the field change sync

    private sealed class SyncFixture
    {
        public readonly IAppFieldRepository Fields = Substitute.For<IAppFieldRepository>();
        public readonly IRecordRepository Records = Substitute.For<IRecordRepository>();
        public readonly IAzureSearchService Search = Substitute.For<IAzureSearchService>();
        public readonly IMessagePublisher Publisher = Substitute.For<IMessagePublisher>();
        public readonly List<string> Errors = [];

        public SyncFixture(params AppField[] tableFields)
        {
            Fields.ListByTableAsync(10, Arg.Any<CancellationToken>()).Returns(tableFields.ToList());
        }

        public void FirstPageHas(int records, long lastId = 500) =>
            Records.GetTableIndexPageAsync(Arg.Is(1L), Arg.Is(10L), Arg.Is(0L), Arg.Is(SearchWideMigration.PageSize), Arg.Any<CancellationToken>(), Arg.Any<int>())
                .Returns((Enumerable.Range(0, records).Select(_ => new SearchIndexDocument(1, 2, 10, Guid.NewGuid(), new Dictionary<long, object?> { [6] = "x" })).ToList(), lastId));

        public Task RunAsync(bool isNullify = false, SlotRef? clearSlot = null) =>
            FieldSlotSync.SyncAsync(Fields, Records, Search, Publisher, 1, 2, 10, 6, isNullify, clearSlot, (_, m) => Errors.Add(m), CancellationToken.None);
    }

    [Fact]
    public async Task A_failed_first_page_still_queues_the_whole_job_from_the_start_and_does_not_throw()
    {
        var f = new SyncFixture(F(1, 6, "Text", slot: "str_01"));
        f.FirstPageHas(3);
        f.Search.BulkIndexRecordsAsync(Arg.Any<IEnumerable<SearchIndexDocument>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("search service busy"));

        await f.Invoking(x => x.RunAsync()).Should().NotThrowAsync("the field is already saved");

        await f.Publisher.Received(1).PublishAsync(
            Arg.Is<SearchIndexMessage>(m => m.Action == IndexAction.BackfillField && m.FieldId == 6 && m.AfterId == 0), Arg.Any<CancellationToken>());
        f.Errors.Should().ContainSingle(e => e.Contains("first page"));
    }

    [Fact]
    public async Task After_a_full_first_page_the_worker_continues_after_its_last_record()
    {
        var f = new SyncFixture(F(1, 6, "Text", slot: "str_01"));
        f.FirstPageHas(SearchWideMigration.PageSize, lastId: 731);

        await f.RunAsync();

        await f.Search.Received(1).BulkIndexRecordsAsync(
            Arg.Is<IEnumerable<SearchIndexDocument>>(d => d.All(x => x.CompleteText)), Arg.Any<CancellationToken>());
        await f.Publisher.Received(1).PublishAsync(Arg.Is<SearchIndexMessage>(m => m.AfterId == 731), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_table_that_fits_in_the_first_page_queues_nothing_more()
    {
        var f = new SyncFixture(F(1, 6, "Text", slot: "str_01"));
        f.FirstPageHas(12);

        await f.RunAsync();

        await f.Publisher.DidNotReceive().PublishAsync(Arg.Any<SearchIndexMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_field_with_no_column_writes_and_queues_nothing()
    {
        var f = new SyncFixture(F(1, 6, "Text")); // pool full: no slot

        await f.RunAsync();

        await f.Records.DidNotReceiveWithAnyArgs().GetTableIndexPageAsync(default, default, default, default, default, default);
        await f.Publisher.DidNotReceive().PublishAsync(Arg.Any<SearchIndexMessage>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_failed_send_is_retried_before_giving_up_with_an_error()
    {
        var f = new SyncFixture(F(1, 6, "Text", slot: "str_01"));
        f.FirstPageHas(SearchWideMigration.PageSize);
        f.Publisher.PublishAsync(Arg.Any<SearchIndexMessage>(), Arg.Any<CancellationToken>())
            .Returns(_ => throw new InvalidOperationException("bus down"), _ => Task.CompletedTask);

        await f.RunAsync();

        await f.Publisher.Received(2).PublishAsync(Arg.Any<SearchIndexMessage>(), Arg.Any<CancellationToken>());
        f.Errors.Should().BeEmpty("the second attempt went through");

        f.Publisher.ClearReceivedCalls();
        f.Publisher.PublishAsync(Arg.Any<SearchIndexMessage>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("bus down"));

        await f.Invoking(x => x.RunAsync()).Should().NotThrowAsync();
        await f.Publisher.Received(3).PublishAsync(Arg.Any<SearchIndexMessage>(), Arg.Any<CancellationToken>());
        f.Errors.Should().ContainSingle(e => e.Contains("could not be queued"));
    }

    [Fact]
    public async Task A_type_change_job_carries_the_old_column_to_empty()
    {
        var f = new SyncFixture(F(1, 6, "Number", slot: "num_01"));
        f.FirstPageHas(SearchWideMigration.PageSize);

        await f.RunAsync(clearSlot: new SlotRef("str_05", "Text"));

        await f.Search.Received(1).BulkIndexRecordsAsync(
            Arg.Is<IEnumerable<SearchIndexDocument>>(d => d.All(x => x.SlotValues!.ContainsKey("str_05") && x.SlotValues["str_05"] == null)),
            Arg.Any<CancellationToken>());
        await f.Publisher.Received(1).PublishAsync(
            Arg.Is<SearchIndexMessage>(m => m.Action == IndexAction.BackfillField && m.Slot == "str_05"), Arg.Any<CancellationToken>());
    }
}
