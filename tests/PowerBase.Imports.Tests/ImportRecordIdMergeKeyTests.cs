using FluentAssertions;
using PowerBase.Application.Imports;

namespace PowerBase.Imports.Tests;

/// <summary>A merge matches records on the destination's Record ID#, read from a source column that is mapped to it: the id only finds the record, it is never
/// written, and a row naming an id that is not there is an error, not a new record.</summary>
public class ImportRecordIdMergeKeyTests
{
    private const int Rows = 10;
    private const int Existing = ImportHarness.RowCount;

    private static ImportDefinitionConfig ById() => new()
    {
        Name = "By id", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Merge, MergeKeyFid = 3,
        Mappings = [new() { DestFid = 3, SourceFid = 8 }, new() { DestFid = 9, SourceFid = 9 }]
    };

    private static HarnessOptions WithIds(Func<long, string?> idOf) => new()
    {
        RowCount = Rows,
        Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = $"n{id}", ["f_8"] = idOf(id), ["f_9"] = $"note{id}" }
    };

    [Fact]
    public async Task Rows_are_matched_on_the_record_id_and_only_the_other_fields_change()
    {
        var h = ImportHarness.Create(ById(), WithIds(id => id.ToString()));

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Store.Updated.Select(u => u.RecordId).Should().BeEquivalentTo(Enumerable.Range(1, Rows).Select(i => (long)i));
        h.Store.Updated.Should().OnlyContain(u => u.Values.Count == 1 && u.Values.ContainsKey(9));
        h.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task A_record_id_that_is_not_in_the_table_is_reported_and_never_inserted()
    {
        var missing = Existing + 500;
        var h = ImportHarness.Create(ById(), WithIds(id => id <= 3 ? id.ToString() : missing.ToString()));

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Store.Updated.Should().HaveCount(3);
        h.Issues.Should().HaveCount(Rows - 3).And.OnlyContain(i => i.ReasonCode == ImportReason.RecordIdNotFound);
        h.Accounted.Should().Be(Rows);
    }

    [Fact]
    public async Task A_blank_or_non_numeric_record_id_is_an_error_row()
    {
        var h = ImportHarness.Create(ById(), WithIds(id => id switch { 1 => "1", 2 => "", 3 => "abc", _ => "2" }));

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Issues.Select(i => (i.SourceRowRef, i.ReasonCode)).Should().Contain([(2L, ImportReason.MergeKeyMissing)]);
        h.Issues.Should().Contain(i => i.SourceRowRef == 3 && i.Message.Contains("abc"));
        h.Accounted.Should().Be(Rows);
    }

    [Fact]
    public async Task A_record_id_repeated_in_the_file_is_reported_for_the_repeat()
    {
        var h = ImportHarness.Create(ById(), WithIds(id => id <= 5 ? "1" : id.ToString()));

        await h.RunAsync();

        h.Store.Updated.Select(u => u.RecordId).Where(i => i == 1).Should().HaveCount(1);
        h.Issues.Should().HaveCountGreaterThanOrEqualTo(4);
        h.Accounted.Should().Be(Rows);
    }

    [Fact]
    public async Task Another_merge_key_still_inserts_rows_that_match_nothing()
    {
        var h = ImportHarness.Create(ImportRunProcessorTests.MergeOnName());

        await h.RunAsync();

        h.Store.Inserted.Should().NotBeEmpty();
        h.Issues.Should().NotContain(i => i.ReasonCode == ImportReason.RecordIdNotFound);
    }

    [Fact]
    public async Task The_record_id_is_refused_as_a_destination_in_a_copy()
    {
        var cfg = ById();
        cfg.ImportType = ImportTypes.Copy;
        cfg.MergeKeyFid = null;
        var h = ImportHarness.Create(cfg, WithIds(id => id.ToString()));

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Store.Updated.Should().BeEmpty();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
    }
}
