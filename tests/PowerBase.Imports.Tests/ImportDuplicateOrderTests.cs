using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

/// <summary>"Remove duplicates" is the last row check: only rows that survived every other check compete, so a row that is rejected for another
/// reason never costs the row after it its place.</summary>
public class ImportDuplicateOrderTests
{
    private const long ContactsId = 12;
    private static readonly Guid ContactsPublicId = Guid.NewGuid();

    // Destination "Dst": Name (6, required, unique), Qty (7, number), Note (9, text). Source f_6 name, f_8 qty text, f_9 note.
    private static ImportDefinitionConfig Copy(params ImportColumnRule[] rules) => new()
    {
        Name = "Dedupe", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Copy, ColumnRules = [.. rules],
        Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 7, SourceFid = 8 }, new() { DestFid = 9, SourceFid = 9 }]
    };

    private static ImportColumnRule Dedupe(int destFid) => new() { DestFid = destFid, RemoveDuplicates = true };

    private static HarnessOptions Rows(params (string? Name, string Qty, string Note)[] rows) => new()
    {
        RowCount = rows.Length,
        Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = rows[id - 1].Name, ["f_8"] = rows[id - 1].Qty, ["f_9"] = rows[id - 1].Note }
    };

    private static List<string> Notes(ImportHarness h) => h.Store.Inserted.Select(r => (string)r[9]!).ToList();

    // ---- the case from the business ----

    [Fact]
    public async Task A_row_that_fails_a_later_check_does_not_cost_the_next_row_its_place()
    {
        // Row 1 has no Name (required), so it is rejected after the column rules. Row 2 has the same Note: it must be imported.
        var h = ImportHarness.Create(Copy(Dedupe(9)), Rows((null, "1", "same"), ("n2", "1", "same")));

        await h.RunAsync();

        h.Store.Inserted.Should().ContainSingle().Which[6].Should().Be("n2");
        h.Issues.Should().ContainSingle(i => i.SourceRowRef == 1 && i.ReasonCode == ImportReason.RequiredMissing);
        h.Issues.Should().NotContain(i => i.ReasonCode == ImportReason.DuplicateInRun, "row 2 was not a duplicate of anything that was imported");
        h.Accounted.Should().Be(2);
    }

    [Fact]
    public async Task A_row_the_destination_refuses_for_a_unique_value_does_not_cost_the_next_row_its_place()
    {
        var h = ImportHarness.Create(Copy(Dedupe(9)), Rows(("EXIST", "1", "same"), ("n2", "1", "same")));
        h.Store.Existing["EXIST"] = 900;

        await h.RunAsync();

        h.Store.Inserted.Should().ContainSingle().Which[6].Should().Be("n2");
        h.Issues.Should().Contain(i => i.SourceRowRef == 1 && i.ReasonCode == ImportReason.DuplicateInDestination);
    }

    [Fact]
    public async Task A_merge_row_without_its_key_does_not_cost_the_next_row_its_place()
    {
        var cfg = Copy(Dedupe(9));
        cfg.ImportType = ImportTypes.Merge;
        cfg.MergeKeyFid = 6;
        var h = ImportHarness.Create(cfg, Rows((null, "1", "same"), ("n2", "1", "same")));

        await h.RunAsync();

        h.Store.Inserted.Should().ContainSingle().Which[6].Should().Be("n2");
        h.Issues.Should().Contain(i => i.SourceRowRef == 1 && i.ReasonCode == ImportReason.MergeKeyMissing);
    }

    // ---- which row wins ----

    [Fact]
    public async Task The_first_row_that_survives_everything_is_the_one_kept()
    {
        var h = ImportHarness.Create(Copy(Dedupe(9)), Rows(("n1", "1", "same"), ("n2", "1", "same"), ("n3", "1", "other"), ("n4", "1", "same")));

        await h.RunAsync();

        h.Store.Inserted.Select(r => (string)r[6]!).Should().Equal("n1", "n3");
        h.Issues.Where(i => i.ReasonCode == ImportReason.DuplicateInRun).Select(i => i.SourceRowRef).Should().Equal(2L, 4L);
        h.Issues.Should().OnlyContain(i => i.Outcome == ImportOutcome.Skipped, "a duplicate is left out on purpose, not an error");
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Partial);
    }

    [Fact]
    public async Task The_first_survivor_wins_across_chunks_and_a_failed_first_row_hands_over_to_the_next()
    {
        // 5,000 rows is more than two reads of 2,000. All share one Note; the first row has no Name, so the second is the survivor.
        var h = ImportHarness.Create(Copy(Dedupe(9)), new HarnessOptions
        {
            RowCount = 5000,
            Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = id == 1 ? null : "n" + id, ["f_8"] = "1", ["f_9"] = "same" }
        });

        await h.RunAsync();

        h.Store.Inserted.Should().ContainSingle().Which[6].Should().Be("n2");
        h.Counters.Skipped.Should().Be(4998);
        h.Counters.Errored.Should().Be(1);
        h.Accounted.Should().Be(5000, "every row is in exactly one outcome");
    }

    [Fact]
    public async Task A_row_left_out_as_a_duplicate_of_one_column_does_not_use_up_its_other_values()
    {
        // Note and Qty both remove duplicates. Row 2 repeats Qty, so it is left out and its Note "b" is not taken: row 3 with "b" is imported.
        var h = ImportHarness.Create(Copy(Dedupe(9), Dedupe(7)), Rows(("n1", "1", "a"), ("n2", "1", "b"), ("n3", "2", "b")));

        await h.RunAsync();

        h.Store.Inserted.Select(r => (string)r[6]!).Should().Equal("n1", "n3");
        h.Issues.Should().ContainSingle(i => i.SourceRowRef == 2 && i.ReasonCode == ImportReason.DuplicateInRun);
    }

    [Fact]
    public async Task A_value_already_in_the_destination_still_leaves_the_row_out_for_a_copy()
    {
        var h = ImportHarness.Create(Copy(Dedupe(9)), Rows(("n1", "1", "old"), ("n2", "1", "new")));
        h.Store.ColumnValues[9] = ["old"];

        await h.RunAsync();

        h.Store.Inserted.Should().ContainSingle().Which[9].Should().Be("new");
        h.Issues.Should().ContainSingle(i => i.ReasonCode == ImportReason.DuplicateInDestination);
    }

    [Fact]
    public async Task A_merge_compares_within_the_run_only_and_the_key_can_have_the_rule()
    {
        var cfg = Copy(Dedupe(6));
        cfg.ImportType = ImportTypes.Merge;
        cfg.MergeKeyFid = 6;
        var h = ImportHarness.Create(cfg, Rows(("n1", "1", "a"), ("n1", "2", "b"), ("n3", "1", "c")));
        h.Store.Existing["n1"] = 500;

        await h.RunAsync();

        h.Store.Updated.Select(u => u.RecordId).Should().Equal(500L);
        h.Store.Inserted.Should().ContainSingle().Which[6].Should().Be("n3");
        h.Issues.Should().ContainSingle(i => i.SourceRowRef == 2 && i.ReasonCode == ImportReason.DuplicateInRun && i.Outcome == ImportOutcome.Skipped);
        h.Accounted.Should().Be(3);
    }

    [Fact]
    public async Task A_row_left_out_as_a_duplicate_gives_back_its_unique_values_for_the_next_row()
    {
        // Row 2 repeats row 1's Note, so it is left out. Its Name "n2" is unique: row 3 carries it too and must not be refused as a repeat of a row
        // that was never imported.
        var h = ImportHarness.Create(Copy(Dedupe(9)), Rows(("n1", "1", "same"), ("n2", "1", "same"), ("n2", "1", "other")));

        await h.RunAsync();

        h.Store.Inserted.Select(r => (string)r[9]!).Should().Equal("same", "other");
        h.Issues.Should().ContainSingle(i => i.SourceRowRef == 2 && i.ReasonCode == ImportReason.DuplicateInRun && i.Outcome == ImportOutcome.Skipped);
        h.Issues.Should().NotContain(i => i.SourceRowRef == 3);
    }

    [Fact]
    public async Task A_row_rejected_for_repeating_a_unique_value_does_not_use_up_its_duplicate_value()
    {
        // Row 2 repeats row 1's unique Name (an error). It must not take its Note "z": row 3 carries "z" too and is the first to survive.
        var h = ImportHarness.Create(Copy(Dedupe(9)), Rows(("n1", "1", "y"), ("n1", "1", "z"), ("n3", "1", "z")));

        await h.RunAsync();

        h.Store.Inserted.Select(r => (string)r[6]!).Should().Equal("n1", "n3");
        h.Issues.Should().ContainSingle(i => i.SourceRowRef == 2 && i.ReasonCode == ImportReason.DuplicateInRun && i.Outcome == ImportOutcome.Errored);
        h.Issues.Should().NotContain(i => i.SourceRowRef == 3);
    }

    [Fact]
    public async Task A_merge_row_left_out_as_a_duplicate_gives_back_its_key_for_the_next_row()
    {
        var cfg = Copy(Dedupe(9));
        cfg.ImportType = ImportTypes.Merge;
        cfg.MergeKeyFid = 6;
        var h = ImportHarness.Create(cfg, Rows(("n1", "1", "same"), ("n2", "1", "same"), ("n2", "1", "other")));
        h.Store.Existing["n2"] = 502;

        await h.RunAsync();

        h.Store.Updated.Select(u => u.RecordId).Should().Equal(502L);
        h.Issues.Should().ContainSingle(i => i.SourceRowRef == 2 && i.ReasonCode == ImportReason.DuplicateInRun && i.Outcome == ImportOutcome.Skipped);
        h.Issues.Should().NotContain(i => i.SourceRowRef == 3, "its key was never taken by a row that was imported");
    }

    // ---- what still comes first ----

    [Fact]
    public async Task Require_field_and_ignore_blanks_still_happen_before_a_row_is_matched_or_compared()
    {
        var rules = new[] { new ImportColumnRule { DestFid = 9, RequireField = true }, Dedupe(7) };
        var h = ImportHarness.Create(Copy(rules), Rows(("n1", "1", ""), ("n2", "1", "x")));

        await h.RunAsync();

        // Row 1 is skipped for its blank Note before the duplicate check, so its Qty "1" is free for row 2.
        h.Store.Inserted.Should().ContainSingle().Which[6].Should().Be("n2");
        h.Issues.Should().ContainSingle(i => i.SourceRowRef == 1 && i.ReasonCode == ImportReason.RequiredBlank);
    }

    // ---- policies and counts ----

    [Fact]
    public async Task Abort_if_any_issue_still_checks_then_writes_with_the_same_rows_kept()
    {
        var cfg = Copy(Dedupe(9));
        cfg.ConstraintPolicy = ImportConstraintPolicy.AbortIfAnyIssue;
        var h = ImportHarness.Create(cfg, Rows(("n1", "1", "same"), ("n2", "1", "same"), ("n3", "1", "other")));

        await h.RunAsync();

        // A duplicate is skipped, not an error, so the check passes and the write pass keeps the same rows the check did.
        h.Store.Inserted.Select(r => (string)r[6]!).Should().Equal("n1", "n3");
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Partial);
    }

    [Fact]
    public async Task Skip_duplicate_groups_is_not_changed_by_a_rule_on_another_column()
    {
        // Name is unique: n1 appears twice, so the whole group is left out. The rule on Note works on top of that.
        var cfg = Copy(Dedupe(9));
        cfg.ConstraintPolicy = ImportConstraintPolicy.ExcludeDuplicateGroups;
        var h = ImportHarness.Create(cfg, Rows(("n1", "1", "a"), ("n1", "1", "b"), ("n3", "1", "c"), ("n4", "1", "c")));

        await h.RunAsync();

        h.Store.Inserted.Select(r => (string)r[6]!).Should().Equal("n3");
        h.Accounted.Should().Be(4);
    }

    [Fact]
    public async Task The_details_file_names_the_row_left_out_and_why()
    {
        var h = ImportHarness.Create(Copy(Dedupe(9)), Rows(("n1", "1", "same"), ("n2", "1", "same")));

        await h.RunAsync();

        h.FeedbackCsv.Should().Contain("Skipped").And.Contain("Duplicate in this import");
        h.FeedbackCsv.Should().Contain(",Inserted,");
    }

    // ---- several tables ----

    [Fact]
    public async Task Each_table_removes_its_own_duplicates_among_the_rows_that_survived_its_own_checks()
    {
        var contacts = new HarnessDestination
        {
            Id = ContactsId, PublicId = ContactsPublicId, Name = "Contacts",
            Fields = [ImportHarness.RecordId(ContactsId), ImportHarness.Field(ContactsId, 6, "Title", "Text"), ImportHarness.Field(ContactsId, 8, "Code", "Text", required: true, unique: true)]
        };
        var cfg = Copy();
        cfg.AdditionalTargets =
        [
            new ImportTargetConfig
            {
                DestinationTableId = ContactsPublicId, ImportType = ImportTypes.Copy, ColumnRules = [Dedupe(6)],
                // Title takes the Note; Code takes the Name, which row 1 does not have: Contacts rejects row 1 (Code required).
                Mappings = [new() { DestFid = 6, SourceFid = 9 }, new() { DestFid = 8, SourceFid = 6 }]
            }
        ];
        var options = Rows((null, "1", "same"), ("n2", "1", "same"));
        options.ExtraDestinations = [contacts];
        var h = ImportHarness.Create(cfg, options);

        await h.RunAsync();

        h.Store.InsertedByTable[ContactsId].Should().ContainSingle().Which[8].Should().Be("n2");
        h.Store.InsertedByTable[11].Should().ContainSingle().Which[6].Should().Be("n2");
        h.TargetCounters[0].Should().Be((1L, 0L, 0L, 1L), "the first table rejects row 1 (no Name) and takes row 2");
        h.TargetCounters[1].Should().Be((1L, 0L, 0L, 1L), "so does Contacts: row 1 has no Code");
    }
}
