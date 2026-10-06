using System.Globalization;
using FluentAssertions;
using PowerBase.Application.Imports;

namespace PowerBase.Imports.Tests;

/// <summary>What happens to rows that break a unique rule: import the valid ones, leave out whole duplicate groups, or
/// stop and import nothing.</summary>
public class ImportConstraintPolicyTests
{
    private static readonly Guid Source = ImportHarness.SourceTableId;

    private static IReadOnlyDictionary<string, object?> Row(long id, string? name = null, string? note = null) => new Dictionary<string, object?>
    {
        ["Id"] = id, ["f_6"] = name ?? $"n{id}", ["f_8"] = id.ToString(CultureInfo.InvariantCulture), ["f_9"] = note ?? $"note{id}"
    };

    private static ImportDefinitionConfig Copy(string policy, params ImportColumnRule[] rules) => new()
    {
        Name = "Policy", SourceTableId = Source, ImportType = ImportTypes.Copy, ConstraintPolicy = policy, ColumnRules = [.. rules],
        Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 7, SourceFid = 8 }, new() { DestFid = 9, SourceFid = 9 }]
    };

    /// <summary>Rows 10 and 2400 (in different chunks) share a unique Name.</summary>
    private static HarnessOptions SharedNameAcrossChunks() => new() { Row = id => Row(id, name: id is 10 or 2400 ? "SHARED" : null) };

    // ---- Import valid rows (default): the first of a group is imported ----

    [Fact]
    public async Task By_default_the_first_row_of_a_duplicate_group_is_imported_and_the_rest_are_reported()
    {
        var h = ImportHarness.Create(Copy(ImportConstraintPolicy.ImportValid), SharedNameAcrossChunks());

        await h.RunAsync();

        h.Store.Inserted.Count(r => Equals(r[6], "SHARED")).Should().Be(1);
        h.Issues.Should().ContainSingle().Which.Should().Match<PowerBase.Domain.Entities.ImportRunIssue>(i =>
            i.SourceRowRef == 2400 && i.ReasonCode == ImportReason.DuplicateInRun);
    }

    // ---- Exclude duplicate groups ----

    [Fact]
    public async Task Excluding_duplicate_groups_imports_none_of_the_rows_that_share_a_value_even_across_chunks()
    {
        var h = ImportHarness.Create(Copy(ImportConstraintPolicy.ExcludeDuplicateGroups), SharedNameAcrossChunks());

        await h.RunAsync();

        h.Store.Inserted.Should().HaveCount(ImportHarness.RowCount - 2).And.NotContain(r => Equals(r[6], "SHARED"));
        h.Issues.Select(i => i.SourceRowRef).Should().BeEquivalentTo(new long?[] { 10, 2400 });
        h.Issues.Should().OnlyContain(i => i.Outcome == ImportOutcome.Errored && i.Message.Contains("none of those rows were imported"));
        h.Accounted.Should().Be(ImportHarness.RowCount);
    }

    [Fact]
    public async Task Excluding_duplicate_groups_leaves_alone_columns_whose_duplicates_a_rule_already_removes()
    {
        // Name has "Remove duplicates": the rule keeps the first row and skips the rest, so the group is not excluded.
        var h = ImportHarness.Create(Copy(ImportConstraintPolicy.ExcludeDuplicateGroups, new ImportColumnRule { DestFid = 6, RemoveDuplicates = true }),
            SharedNameAcrossChunks());

        await h.RunAsync();

        h.Store.Inserted.Count(r => Equals(r[6], "SHARED")).Should().Be(1);
        h.Issues.Should().ContainSingle().Which.Outcome.Should().Be(ImportOutcome.Skipped);
    }

    [Fact]
    public async Task Excluding_duplicate_groups_covers_a_repeated_merge_key()
    {
        var config = Copy(ImportConstraintPolicy.ExcludeDuplicateGroups);
        config.ImportType = ImportTypes.Merge;
        config.MergeKeyFid = 6;
        var h = ImportHarness.Create(config, SharedNameAcrossChunks());
        h.Store.Existing["SHARED"] = 777;

        await h.RunAsync();

        h.Store.Updated.Should().NotContain(u => u.RecordId == 777);
        h.Issues.Select(i => i.SourceRowRef).Should().BeEquivalentTo(new long?[] { 10, 2400 });
    }

    // ---- Abort if any issue ----

    [Fact]
    public async Task Aborting_on_any_issue_imports_nothing_and_reports_every_rejected_row()
    {
        var h = ImportHarness.Create(Copy(ImportConstraintPolicy.AbortIfAnyIssue), new HarnessOptions { Row = id => Row(id, name: id == 5 ? "" : null) });

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Store.RecordCountAdded.Should().Be(0);
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("Nothing was imported");
        h.Counters.Should().Be(((long)ImportHarness.RowCount, 0L, 0L, 0L, 1L));
        h.Issues.Should().ContainSingle().Which.SourceRowRef.Should().Be(5);
        h.FeedbackCsv.Should().Contain("Required value missing");
    }

    [Fact]
    public async Task The_pre_check_sees_duplicates_that_are_a_whole_chunk_apart_because_nothing_has_been_written_yet()
    {
        var h = ImportHarness.Create(Copy(ImportConstraintPolicy.AbortIfAnyIssue), SharedNameAcrossChunks());

        await h.RunAsync();

        h.Store.Inserted.Should().BeEmpty();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Issues.Should().ContainSingle().Which.SourceRowRef.Should().Be(2400);
    }

    [Fact]
    public async Task When_the_pre_check_passes_the_whole_import_runs_and_is_counted_once()
    {
        var h = ImportHarness.Create(Copy(ImportConstraintPolicy.AbortIfAnyIssue), new HarnessOptions { Row = id => Row(id) });

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.Store.Inserted.Should().HaveCount(ImportHarness.RowCount);
        h.Counters.Read.Should().Be(ImportHarness.RowCount); // the dry run does not add to the run's counters
        h.Accounted.Should().Be(ImportHarness.RowCount);
    }

    [Fact]
    public async Task Rows_a_rule_leaves_out_on_purpose_do_not_make_the_pre_check_abort()
    {
        var h = ImportHarness.Create(Copy(ImportConstraintPolicy.AbortIfAnyIssue, new ImportColumnRule { DestFid = 9, RequireField = true }),
            new HarnessOptions { Row = id => Row(id, note: id % 500 == 0 ? "" : null) });

        await h.RunAsync();

        h.Store.Inserted.Should().HaveCount(ImportHarness.RowCount - 5);
        h.Counters.Skipped.Should().Be(5);
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Partial);
        h.Issues.Should().HaveCount(5, "the skipped rows are recorded once, by the real pass, not by the dry run too");
    }

    [Fact]
    public async Task The_pre_check_does_not_count_a_remove_duplicates_rule_twice()
    {
        // The dry run and the real run each start with empty memory of seen values.
        var h = ImportHarness.Create(Copy(ImportConstraintPolicy.AbortIfAnyIssue, new ImportColumnRule { DestFid = 9, RemoveDuplicates = true }),
            new HarnessOptions { Row = id => Row(id, note: id is 1 or 2 ? "SAME" : null) });

        await h.RunAsync();

        h.Store.Inserted.Should().HaveCount(ImportHarness.RowCount - 1);
        h.Counters.Skipped.Should().Be(1);
    }

    [Fact]
    public async Task An_unknown_policy_is_rejected()
    {
        var h = ImportHarness.Create(Copy("nonsense"));

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("unique rule");
    }
}
