using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Imports;
using PowerBase.Application.Reports;

namespace PowerBase.Imports.Tests;

/// <summary>Runs the import engine end to end against in-memory fakes: chunked keyset reading, mapping, the per-row
/// checks, merge, bulk-write bisecting and run bookkeeping.</summary>
public class ImportRunProcessorTests
{
    private const int RowCount = ImportHarness.RowCount;
    private static readonly Guid Source = ImportHarness.SourceTableId;

    internal static ImportDefinitionConfig MergeOnName() => new()
    {
        Name = "Merge", SourceTableId = Source, ImportType = ImportTypes.Merge, MergeKeyFid = 6,
        Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 7, SourceFid = 8 }]
    };

    [Fact]
    public async Task Copy_imports_good_rows_reports_bad_rows_and_never_stops_at_a_bad_row()
    {
        var h = ImportHarness.Create();
        h.Store.Existing["EXIST"] = 900;

        await h.RunAsync();

        h.Store.Inserted.Should().HaveCount(RowCount - 5);
        h.Store.RecordCountAdded.Should().Be(RowCount - 5);
        h.Issues.Select(i => (i.SourceRowRef, i.ReasonCode)).Should().BeEquivalentTo(new (long?, string)[]
        {
            (5, ImportReason.RequiredMissing), (7, ImportReason.DuplicateInRun), (9, ImportReason.DuplicateInDestination),
            (11, ImportReason.WriteFailed), (13, ImportReason.TypeMismatch)
        });
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Partial);
    }

    [Fact]
    public async Task Every_source_row_ends_up_in_exactly_one_outcome()
    {
        var h = ImportHarness.Create();
        h.Store.Existing["EXIST"] = 900;

        await h.RunAsync();

        h.Counters.Read.Should().Be(RowCount);
        h.Accounted.Should().Be(RowCount);
        h.Counters.Should().Be(((long)RowCount, (long)(RowCount - 5), 0L, 0L, 5L));
    }

    [Fact]
    public async Task The_source_is_read_in_chunks_and_the_cursor_advances_with_each_chunk()
    {
        var h = ImportHarness.Create();

        await h.RunAsync();

        await h.Runs.Received(1).AdvanceAsync(h.Run.Id,
            Arg.Is<ImportChunkResult>(c => c.LastSourceId == ImportSourceReader.ChunkSize && c.RowsRead == ImportSourceReader.ChunkSize),
            Arg.Any<byte>(), Arg.Any<CancellationToken>());
        await h.Runs.Received(1).AdvanceAsync(h.Run.Id,
            Arg.Is<ImportChunkResult>(c => c.LastSourceId == RowCount && c.RowsRead == RowCount - ImportSourceReader.ChunkSize),
            Arg.Any<byte>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Text_is_converted_to_a_number_and_values_are_keyed_by_destination_field()
    {
        var h = ImportHarness.Create();

        await h.RunAsync();

        h.Store.Inserted.First(r => Equals(r[6], "n1"))[7].Should().Be(1m);
    }

    [Fact]
    public async Task An_empty_text_cell_mapped_into_a_number_is_a_blank_not_a_bad_number()
    {
        var h = ImportHarness.Create(options: new HarnessOptions
        {
            RowCount = 10, Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = $"n{id}", ["f_8"] = id % 2 == 0 ? "" : "  ", ["f_9"] = "x" }
        });

        await h.RunAsync();

        h.Issues.Should().BeEmpty();
        h.Store.Inserted.Should().HaveCount(10).And.OnlyContain(r => r[7] == null);
    }

    [Fact]
    public async Task Conditions_on_fields_sql_cannot_evaluate_are_applied_per_row_and_the_cursor_still_advances()
    {
        // The encrypted Name column cannot be compared in SQL, so this condition runs on each row read.
        var h = ImportHarness.Create(options: new HarnessOptions
        {
            EncryptedSourceName = true,
            Conditions = new FilterGroup { Nodes = [new() { Condition = new() { FieldId = 6, Operator = "startsWith", Value = "n2" } }] }
        });

        await h.RunAsync();

        var expected = Enumerable.Range(1, RowCount).Count(i => $"n{i}".StartsWith("n2") && i is not (5 or 7 or 9 or 11));
        h.Store.Inserted.Should().HaveCount(expected).And.OnlyContain(r => ((string)r[6]!).StartsWith("n2"));
        await h.Runs.Received(1).AdvanceAsync(h.Run.Id, Arg.Is<ImportChunkResult>(c => c.LastSourceId == RowCount), Arg.Any<byte>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rows_left_out_by_the_conditions_are_not_counted_as_read()
    {
        var h = ImportHarness.Create(options: new HarnessOptions
        {
            EncryptedSourceName = true,
            Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = $"n{id}", ["f_8"] = "1", ["f_9"] = "x" },
            Conditions = new FilterGroup { Nodes = [new() { Condition = new() { FieldId = 6, Operator = "eq", Value = "n7" } }] }
        });

        await h.RunAsync();

        h.Counters.Read.Should().Be(1);
        h.Accounted.Should().Be(1);
    }

    [Fact]
    public async Task Merge_updates_the_matching_record_and_inserts_the_rest()
    {
        var h = ImportHarness.Create(MergeOnName());
        h.Store.Existing["n1"] = 100;
        h.Store.Existing["n2"] = 101;

        await h.RunAsync();

        h.Store.Updated.Select(u => u.RecordId).Should().BeEquivalentTo(new long[] { 100, 101 });
        // The key identifies the record and is not rewritten; only the other mapped fields change.
        h.Store.Updated.Should().OnlyContain(u => u.Values.Count == 1 && u.Values.ContainsKey(7));
        h.Store.Inserted.Should().HaveCount(RowCount - 2 - 4);
        h.Issues.Select(i => (i.SourceRowRef, i.ReasonCode)).Should().BeEquivalentTo(new (long?, string)[]
        {
            (5, ImportReason.MergeKeyMissing), (7, ImportReason.DuplicateInRun), (11, ImportReason.WriteFailed), (13, ImportReason.TypeMismatch)
        });
        h.Accounted.Should().Be(RowCount);
        h.Counters.Updated.Should().Be(2);
    }

    [Fact]
    public async Task Same_table_merge_on_record_id_copies_one_column_into_another_on_the_same_record()
    {
        var h = ImportHarness.Create(new ImportDefinitionConfig
        {
            Name = "Snapshot", SourceTableId = Source, ImportType = ImportTypes.Merge, MergeKeyFid = 3,
            Mappings = [new() { DestFid = 3, SourceFid = 3 }, new() { DestFid = 7, SourceFid = 6 }]
        }, new HarnessOptions { SameTable = true });

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Store.Updated.Should().HaveCount(RowCount);
        h.Store.Updated.Single(u => u.RecordId == 1).Values.Should().ContainSingle().Which.Should().Be(new KeyValuePair<long, object?>(7, "n1"));
    }

    [Fact]
    public async Task Merge_on_record_id_needs_no_edit_right_on_the_record_id_itself()
    {
        var h = ImportHarness.Create(new ImportDefinitionConfig
        {
            Name = "ById", SourceTableId = Source, ImportType = ImportTypes.Merge, MergeKeyFid = 3,
            Mappings = [new() { DestFid = 3, SourceFid = 3 }, new() { DestFid = 7, SourceFid = 8 }]
        }, new HarnessOptions { LimitedDestination = true });

        await h.RunAsync();

        h.Store.Updated.Should().NotBeEmpty();
        h.Completion!.Value.Status.Should().NotBe(ImportRunStatus.Failed);
    }

    private static async Task AssertRunFailsWith(ImportDefinitionConfig definition, string message, HarnessOptions? options = null)
    {
        var h = ImportHarness.Create(definition, options);

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Store.Updated.Should().BeEmpty();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain(message);
    }

    [Fact]
    public Task Merge_key_must_be_unique() => AssertRunFailsWith(new ImportDefinitionConfig
    {
        Name = "Merge", SourceTableId = Source, ImportType = ImportTypes.Merge, MergeKeyFid = 7, // Qty is not unique
        Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 7, SourceFid = 8 }]
    }, "not unique");

    [Fact]
    public Task Merge_key_must_be_mapped() => AssertRunFailsWith(new ImportDefinitionConfig
    {
        Name = "Merge", SourceTableId = Source, ImportType = ImportTypes.Merge, MergeKeyFid = 6,
        Mappings = [new() { DestFid = 7, SourceFid = 8 }]
    }, "Map a source field");

    [Fact]
    public Task Record_id_can_only_be_mapped_as_a_merge_key() => AssertRunFailsWith(new ImportDefinitionConfig
    {
        Name = "Copy", SourceTableId = Source, ImportType = ImportTypes.Copy,
        Mappings = [new() { DestFid = 3, SourceFid = 3 }, new() { DestFid = 6, SourceFid = 6 }]
    }, "cannot be imported into");

    [Fact]
    public Task A_destination_with_duplicate_key_values_blocks_the_merge_before_any_write() =>
        AssertRunFailsWith(MergeOnName(), "duplicate values", new HarnessOptions { DuplicateKeyValues = true });

    [Fact]
    public async Task A_run_that_was_already_running_is_failed_as_interrupted_instead_of_resumed()
    {
        var h = ImportHarness.Create();
        h.Run.Status = ImportRunStatus.Running;
        h.Run.RowsRead = 2000;

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("interrupted");
    }

    [Fact]
    public async Task A_run_with_nothing_to_import_succeeds_and_its_details_file_has_only_the_headings()
    {
        var h = ImportHarness.Create(options: new HarnessOptions { RowCount = 0 });

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.Completion!.Value.FeedbackPath.Should().NotBeNull();
        h.FeedbackCsv!.Trim().Should().StartWith("Source row,Result,Record ID#").And.NotContain("\n");
    }
}
