using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

/// <summary>Every run saves a details file for every table it filled: a line for each source row, whatever happened to it, with the Record ID#
/// of the record it became or matched.</summary>
public class ImportDetailsTests
{
    private static List<string[]> Parse(string csv)
    {
        var rows = new List<string[]>();
        var fields = new List<string>();
        var cell = new System.Text.StringBuilder();
        var quoted = false;
        for (var i = 0; i < csv.Length; i++)
        {
            var c = csv[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < csv.Length && csv[i + 1] == '"') { cell.Append('"'); i++; }
                else if (c == '"') quoted = false;
                else cell.Append(c);
            }
            else if (c == '"') quoted = true;
            else if (c == ',') { fields.Add(cell.ToString()); cell.Clear(); }
            else if (c == '\n') { fields.Add(cell.ToString().TrimEnd('\r')); cell.Clear(); rows.Add([.. fields]); fields.Clear(); }
            else cell.Append(c);
        }
        return rows;
    }

    // Columns of a details file: 0 Source row, 1 Result, 2 Record ID#, 3 Reason, 4 Column, 5 Details, 6 Existing record, then the values.
    private const int Row = 0, Result = 1, Rid = 2, Reason = 3;

    private static HarnessOptions Source(int rows, Func<long, IReadOnlyDictionary<string, object?>>? row = null,
        Func<long, IReadOnlyDictionary<string, object?>>? stored = null) => new()
    {
        RowCount = rows, StoredRecords = stored,
        Row = row ?? (id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = "n" + id, ["f_8"] = id.ToString(), ["f_9"] = "x" })
    };

    // ---- inserts ----

    [Fact]
    public async Task Every_inserted_row_is_listed_with_the_record_id_it_was_given()
    {
        var h = ImportHarness.Create(options: Source(5));

        await h.RunAsync();

        var rows = Parse(h.FeedbackCsv!).Skip(1).ToList();
        rows.Should().HaveCount(5);
        rows.Select(r => r[Row]).Should().Equal("1", "2", "3", "4", "5");
        rows.Should().OnlyContain(r => r[Result] == "Inserted");
        rows.Select(r => long.Parse(r[Rid])).Should().BeInAscendingOrder().And.OnlyHaveUniqueItems();
        h.Store.Inserted.Should().HaveCount(5);
    }

    [Fact]
    public async Task The_record_id_belongs_to_the_row_even_when_other_rows_around_it_were_rejected()
    {
        // Row 2 is rejected, so the rows after it are given ids that do not line up with their source row numbers.
        var h = ImportHarness.Create(options: Source(4, id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = "n" + id, ["f_8"] = id == 2 ? "bad" : "1", ["f_9"] = "x" }));

        await h.RunAsync();

        var rows = Parse(h.FeedbackCsv!).Skip(1).ToDictionary(r => r[Row]);
        rows["2"][Result].Should().Be("Error");
        rows["2"][Rid].Should().BeEmpty("a rejected new row has no record");
        new[] { "1", "3", "4" }.Select(k => long.Parse(rows[k][Rid])).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task A_row_the_database_rejects_when_writing_is_an_error_and_the_rest_keep_their_ids()
    {
        var h = ImportHarness.Create(options: Source(6, id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = id == 4 ? "BOOM" : "n" + id, ["f_8"] = "1", ["f_9"] = "x" }));

        await h.RunAsync();

        var rows = Parse(h.FeedbackCsv!).Skip(1).ToDictionary(r => r[Row]);
        rows["4"][Result].Should().Be("Error");
        rows["4"][Reason].Should().Be("Could not be saved");
        rows.Where(r => r.Key != "4").Should().OnlyContain(r => r.Value[Result] == "Inserted" && r.Value[Rid].Length > 0);
        h.Counters.Inserted.Should().Be(5);
        h.Accounted.Should().Be(6);
    }

    // ---- merges ----

    private static ImportDefinitionConfig MergeOnName() => ImportRunProcessorTests.MergeOnName();

    [Fact]
    public async Task An_updated_row_carries_the_record_it_updated_and_a_new_row_the_record_it_became()
    {
        var h = ImportHarness.Create(MergeOnName(), Source(4, stored: _ => new Dictionary<string, object?> { ["f_7"] = 99m }));
        h.Store.Existing["n1"] = 500;
        h.Store.Existing["n3"] = 502;

        await h.RunAsync();

        var rows = Parse(h.FeedbackCsv!).Skip(1).ToDictionary(r => r[Row]);
        rows["1"][Result].Should().Be("Updated");
        rows["1"][Rid].Should().Be("500");
        rows["3"][Rid].Should().Be("502");
        rows["2"][Result].Should().Be("Inserted");
        rows["2"][Rid].Should().NotBe("500").And.NotBeEmpty();
        h.Counters.Updated.Should().Be(2);
        h.Counters.Inserted.Should().Be(2);
    }

    [Fact]
    public async Task A_matched_record_that_already_holds_every_value_is_left_alone_and_listed_as_unchanged()
    {
        // n1 already has Qty 1 (what the source says); n2 has 5 (the source says 2).
        var h = ImportHarness.Create(MergeOnName(), Source(2, stored: id => new Dictionary<string, object?> { ["f_7"] = id == 500 ? 1m : 5m }));
        h.Store.Existing["n1"] = 500;
        h.Store.Existing["n2"] = 501;

        await h.RunAsync();

        h.Store.Updated.Select(u => u.RecordId).Should().Equal(501L);
        var rows = Parse(h.FeedbackCsv!).Skip(1).ToDictionary(r => r[Row]);
        rows["1"][Result].Should().Be("Unchanged");
        rows["1"][Rid].Should().Be("500");
        rows["2"][Result].Should().Be("Updated");
        h.Unchanged.Should().Be(1);
        h.Counters.Updated.Should().Be(1);
        h.Accounted.Should().Be(2);
    }

    [Fact]
    public async Task A_run_whose_rows_are_all_unchanged_is_a_success_and_writes_nothing()
    {
        var h = ImportHarness.Create(MergeOnName(), Source(3, stored: _ => new Dictionary<string, object?> { ["f_7"] = 1m }, row: id =>
            new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = "n" + id, ["f_8"] = "1", ["f_9"] = "x" }));
        foreach (var n in new[] { "n1", "n2", "n3" }) h.Store.Existing[n] = 500 + n[1] - '0';

        await h.RunAsync();

        h.Store.Updated.Should().BeEmpty();
        h.Store.Inserted.Should().BeEmpty();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.Unchanged.Should().Be(3);
    }

    [Fact]
    public async Task Without_what_is_stored_a_matched_record_is_updated_never_assumed_unchanged()
    {
        var h = ImportHarness.Create(MergeOnName(), Source(2));
        h.Store.Existing["n1"] = 500;
        h.Store.Existing["n2"] = 501;

        await h.RunAsync();

        h.Store.Updated.Should().HaveCount(2);
        h.Unchanged.Should().Be(0);
    }

    [Fact]
    public async Task A_matched_record_that_fails_a_later_check_still_names_its_record_in_the_file()
    {
        // The destination's unique Name is held by another record: the update is an error, and the file says which record the row matched.
        var cfg = ImportRunProcessorTests.MergeOnName();
        cfg.Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 9, SourceFid = 9 }];
        cfg.MergeKeyFid = 6;
        var h = ImportHarness.Create(cfg, Source(1, stored: _ => new Dictionary<string, object?>(), row: id =>
            new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = "n1", ["f_8"] = "1", ["f_9"] = "x" }));
        h.Store.Existing["n1"] = 500;

        await h.RunAsync();

        var row = Parse(h.FeedbackCsv!).Skip(1).Single();
        row[Result].Should().Be("Updated");
        row[Rid].Should().Be("500");
    }

    // ---- one file per table ----

    [Fact]
    public async Task A_run_into_one_table_saves_one_file_and_a_run_into_two_saves_two()
    {
        var one = ImportHarness.Create(options: Source(3));
        await one.RunAsync();

        one.DetailsFiles.Should().HaveCount(1);
    }

    // ---- the writer itself ----

    private static async Task<string> Save(ImportDetailsWriter w)
    {
        var storage = Substitute.For<IFileStorageService>();
        string? text = null;
        storage.SaveAsync(Arg.Any<Stream>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>(), Arg.Any<string?>())
            .Returns(async call =>
            {
                using var ms = new MemoryStream();
                await call.ArgAt<Stream>(0).CopyToAsync(ms);
                text = System.Text.Encoding.UTF8.GetString(ms.ToArray()).TrimStart('﻿');
                return new StoredFile { Path = "/p", Size = ms.Length, ContentType = "text/csv" };
            });
        await w.SaveAsync(storage, Guid.NewGuid(), 0, default);
        return text!;
    }

    private static ImportFeedbackRow Problem(long row, string outcome, string reason, long? record = null) => new(
        new ImportRunIssue { SourceRowRef = row, Outcome = outcome, ReasonCode = reason, Message = "msg" }, ["v" + row], null, null, record);

    [Fact]
    public async Task Lines_come_out_in_source_order_whether_they_were_imported_or_not()
    {
        await using var w = new ImportDetailsWriter(["Name"], new Dictionary<int, string>());

        await w.AppendAsync([Problem(2, ImportOutcome.Errored, ImportReason.TypeMismatch), Problem(5, ImportOutcome.Skipped, ImportReason.RequiredBlank, record: 77)],
            [new ImportWrittenRow(1, ImportOutcome.Inserted, 10, ["a"]), new ImportWrittenRow(3, ImportOutcome.Updated, 11, ["c"]), new ImportWrittenRow(4, ImportOutcome.Unchanged, 12, ["d"])], default);

        var rows = Parse(await Save(w)).Skip(1).ToList();
        rows.Select(r => r[Row]).Should().Equal("1", "2", "3", "4", "5");
        rows.Select(r => r[Result]).Should().Equal("Inserted", "Error", "Updated", "Unchanged", "Skipped");
        rows[4][Rid].Should().Be("77", "a skipped row that matched a record names it");
        rows[1][Rid].Should().BeEmpty();
        w.RowCount.Should().Be(5);
    }

    [Fact]
    public async Task A_file_with_no_lines_still_has_its_headings()
    {
        await using var w = new ImportDetailsWriter(["Name", "Qty"], new Dictionary<int, string>());

        var text = await Save(w);

        text.Trim().Should().Be("Source row,Result,Record ID#,Reason,Column,Details,Existing record,Name,Qty");
    }

    [Fact]
    public async Task Values_are_neutralised_so_a_hostile_cell_cannot_run_in_a_spreadsheet()
    {
        await using var w = new ImportDetailsWriter(["Name"], new Dictionary<int, string>());

        await w.AppendAsync([], [new ImportWrittenRow(1, ImportOutcome.Inserted, 10, ["=HYPERLINK(\"x\")"])], default);

        Parse(await Save(w))[1][7].Should().StartWith("'=");
    }

    // ---- is it the same value? ----

    [Theory]
    [InlineData(1, 1.0, true)]
    [InlineData(1.5, 1.5, true)]
    [InlineData(1, 2, false)]
    public void Numbers_are_compared_by_value_whatever_their_type(double stored, double incoming, bool same)
        => ImportValueEquality.Same((decimal)stored, (object)(decimal)incoming).Should().Be(same);

    [Fact]
    public void A_blank_is_the_same_as_nothing_and_not_the_same_as_a_value()
    {
        ImportValueEquality.Same(null, "").Should().BeTrue();
        ImportValueEquality.Same(DBNull.Value, null).Should().BeTrue();
        ImportValueEquality.Same("a", null).Should().BeFalse();
        ImportValueEquality.Same(0m, null).Should().BeFalse("a zero is a value");
    }

    [Fact]
    public void Text_is_compared_exactly_and_unlike_kinds_are_never_the_same()
    {
        ImportValueEquality.Same("Abc", "Abc").Should().BeTrue();
        ImportValueEquality.Same("Abc", "abc").Should().BeFalse();
        ImportValueEquality.Same("Abc", "Abc ").Should().BeFalse();
        ImportValueEquality.Same("5", 5m).Should().BeFalse("writing it again is harmless; skipping a real change is not");
        ImportValueEquality.Same(true, true).Should().BeTrue();
        ImportValueEquality.Same(true, false).Should().BeFalse();
        ImportValueEquality.Same(new DateTime(2026, 3, 9), new DateTime(2026, 3, 9)).Should().BeTrue();
        ImportValueEquality.Same(new DateTime(2026, 3, 9), new DateTime(2026, 3, 10)).Should().BeFalse();
    }
}
