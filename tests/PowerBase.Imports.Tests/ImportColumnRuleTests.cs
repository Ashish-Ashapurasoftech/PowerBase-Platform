using System.Globalization;
using FluentAssertions;
using PowerBase.Application.Imports;

namespace PowerBase.Imports.Tests;

/// <summary>Remove duplicates, Require field and Ignore blanks: each rule, on its own column, in a Copy and in a Merge.</summary>
public class ImportColumnRuleTests
{
    private static readonly Guid Source = ImportHarness.SourceTableId;

    /// <summary>Rows with no problems of their own, so only the rule under test decides what happens to a row.</summary>
    private static IReadOnlyDictionary<string, object?> Clean(long id, string? note = null, string? qty = null, string? name = null) => new Dictionary<string, object?>
    {
        ["Id"] = id, ["f_6"] = name ?? $"n{id}", ["f_8"] = qty ?? id.ToString(CultureInfo.InvariantCulture), ["f_9"] = note ?? $"note{id}"
    };

    private static ImportDefinitionConfig Copy(params ImportColumnRule[] rules) => new()
    {
        Name = "Rules", SourceTableId = Source, ImportType = ImportTypes.Copy, ColumnRules = [.. rules],
        Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 7, SourceFid = 8 }, new() { DestFid = 9, SourceFid = 9 }]
    };

    private static ImportDefinitionConfig Merge(params ImportColumnRule[] rules)
    {
        var config = Copy(rules);
        config.ImportType = ImportTypes.Merge;
        config.MergeKeyFid = 6;
        return config;
    }

    private static ImportColumnRule Rule(int fid, bool removeDuplicates = false, bool requireField = false, bool ignoreBlanks = false) =>
        new() { DestFid = fid, RemoveDuplicates = removeDuplicates, RequireField = requireField, IgnoreBlanks = ignoreBlanks };

    // ---- Require field ----

    [Fact]
    public async Task Require_field_leaves_out_rows_with_a_blank_and_reports_them_as_skipped_not_errors()
    {
        var h = ImportHarness.Create(Copy(Rule(9, requireField: true)),
            new HarnessOptions { Row = id => Clean(id, note: id % 100 == 0 ? (id % 200 == 0 ? "" : "   ") : null) });

        await h.RunAsync();

        h.Counters.Skipped.Should().Be(25);
        h.Counters.Errored.Should().Be(0);
        h.Store.Inserted.Should().HaveCount(ImportHarness.RowCount - 25);
        h.Issues.Should().HaveCount(25).And.OnlyContain(i => i.Outcome == ImportOutcome.Skipped && i.ReasonCode == ImportReason.RequiredBlank && i.ColumnFid == 9);
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Partial);
        h.Accounted.Should().Be(ImportHarness.RowCount);
    }

    [Fact]
    public async Task A_false_checkbox_is_a_value_not_a_blank()
    {
        var h = ImportHarness.Create(new ImportDefinitionConfig
        {
            Name = "Flags", SourceTableId = Source, ImportType = ImportTypes.Copy,
            ColumnRules = [Rule(10, requireField: true)],
            Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 10, SourceFid = 10 }]
        }, new HarnessOptions
        {
            RowCount = 4,
            Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = $"n{id}", ["f_10"] = id % 2 == 0 },
            ConfigureDestination = d => d.Add(ImportHarness.Field(11, 10, "Flag", "Boolean")),
            ConfigureSource = s => s.Add(ImportHarness.Field(10, 10, "Flag", "Boolean"))
        });

        await h.RunAsync();

        h.Issues.Should().BeEmpty();
        h.Store.Inserted.Should().HaveCount(4);
    }

    // ---- Ignore blanks ----

    [Fact]
    public async Task Ignore_blanks_imports_the_row_without_writing_the_blank()
    {
        var h = ImportHarness.Create(Copy(Rule(9, ignoreBlanks: true)), new HarnessOptions { Row = id => Clean(id, note: id % 2 == 0 ? "" : null) });

        await h.RunAsync();

        h.Issues.Should().BeEmpty();
        h.Store.Inserted.Should().HaveCount(ImportHarness.RowCount);
        h.Store.Inserted.Where(r => !r.ContainsKey(9)).Should().HaveCount(ImportHarness.RowCount / 2);
    }

    [Fact]
    public async Task Ignore_blanks_gives_a_new_record_the_fields_default_instead_of_a_blank()
    {
        var h = ImportHarness.Create(Copy(Rule(9, ignoreBlanks: true)), new HarnessOptions
        {
            RowCount = 4, Row = id => Clean(id, note: id % 2 == 0 ? "" : null),
            ConfigureDestination = d => d.Single(f => f.Fid == 9).DefaultValue = "N/A"
        });

        await h.RunAsync();

        h.Store.Inserted.Single(r => Equals(r[6], "n2"))[9].Should().Be("N/A");
        h.Store.Inserted.Single(r => Equals(r[6], "n1"))[9].Should().Be("note1");
    }

    [Fact]
    public async Task Ignore_blanks_on_a_required_field_without_a_default_is_still_a_missing_required_value()
    {
        var h = ImportHarness.Create(Copy(Rule(6, ignoreBlanks: true)), new HarnessOptions
        {
            RowCount = 3, Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = id == 2 ? "" : $"n{id}", ["f_8"] = "1", ["f_9"] = "x" }
        });

        await h.RunAsync();

        h.Issues.Should().ContainSingle().Which.Should().Match<PowerBase.Domain.Entities.ImportRunIssue>(i =>
            i.SourceRowRef == 2 && i.ReasonCode == ImportReason.RequiredMissing && i.Outcome == ImportOutcome.Errored);
    }

    [Fact]
    public async Task In_a_merge_ignore_blanks_keeps_the_stored_value_by_leaving_the_column_out_of_that_update()
    {
        var h = ImportHarness.Create(Merge(Rule(9, ignoreBlanks: true)),
            new HarnessOptions { RowCount = 4, Row = id => Clean(id, note: id == 2 ? "" : null) });
        foreach (var id in new[] { 1, 2, 3, 4 }) h.Store.Existing[$"n{id}"] = 100 + id;

        await h.RunAsync();

        h.Store.Updated.Should().HaveCount(4);
        h.Store.Updated.Single(u => u.RecordId == 102).Values.Keys.Should().BeEquivalentTo(new long[] { 7 }); // Qty only: Note kept
        h.Store.Updated.Single(u => u.RecordId == 101).Values.Keys.Should().BeEquivalentTo(new long[] { 7, 9 });
    }

    [Fact]
    public async Task A_merged_record_whose_every_value_is_an_ignored_blank_is_skipped_as_nothing_to_update()
    {
        var h = ImportHarness.Create(new ImportDefinitionConfig
        {
            Name = "NoChange", SourceTableId = Source, ImportType = ImportTypes.Merge, MergeKeyFid = 6, ColumnRules = [Rule(9, ignoreBlanks: true)],
            Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 9, SourceFid = 9 }]
        }, new HarnessOptions { RowCount = 5, Row = id => Clean(id, note: "") });
        foreach (var id in Enumerable.Range(1, 5)) h.Store.Existing[$"n{id}"] = 100 + id;

        await h.RunAsync();

        h.Store.Updated.Should().BeEmpty();
        h.Counters.Skipped.Should().Be(5);
        h.Issues.Should().OnlyContain(i => i.ReasonCode == ImportReason.NoChanges && i.Outcome == ImportOutcome.Skipped);
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Partial); // not a failure: nothing was wrong with the data
    }

    // ---- Remove duplicates ----

    [Fact]
    public async Task Remove_duplicates_works_across_the_whole_run_not_just_within_one_chunk()
    {
        // Rows 1 and 2400 sit in different chunks and carry the same note.
        var h = ImportHarness.Create(Copy(Rule(9, removeDuplicates: true)),
            new HarnessOptions { Row = id => Clean(id, note: id is 1 or 2400 ? "SHARED" : null) });

        await h.RunAsync();

        h.Issues.Should().ContainSingle().Which.Should().Match<PowerBase.Domain.Entities.ImportRunIssue>(i =>
            i.SourceRowRef == 2400 && i.Outcome == ImportOutcome.Skipped && i.ReasonCode == ImportReason.DuplicateInRun);
        h.Store.Inserted.Should().HaveCount(ImportHarness.RowCount - 1);
        h.Counters.Errored.Should().Be(0);
    }

    [Fact]
    public async Task Remove_duplicates_ignores_case_and_surrounding_spaces_like_the_database_does()
    {
        var h = ImportHarness.Create(Copy(Rule(9, removeDuplicates: true)), new HarnessOptions
        {
            RowCount = 3, Row = id => Clean(id, note: id switch { 1 => "Alpha", 2 => " alpha ", _ => "ALPHA" })
        });

        await h.RunAsync();

        h.Store.Inserted.Should().ContainSingle();
        h.Counters.Skipped.Should().Be(2);
    }

    [Fact]
    public async Task Blank_values_are_never_duplicates_of_each_other()
    {
        var h = ImportHarness.Create(Copy(Rule(9, removeDuplicates: true)), new HarnessOptions { RowCount = 6, Row = id => Clean(id, note: "") });

        await h.RunAsync();

        h.Issues.Should().BeEmpty();
        h.Store.Inserted.Should().HaveCount(6);
    }

    [Fact]
    public async Task In_a_copy_remove_duplicates_also_skips_values_already_in_the_destination()
    {
        var h = ImportHarness.Create(Copy(Rule(9, removeDuplicates: true)), new HarnessOptions { RowCount = 10, Row = id => Clean(id) });
        h.Store.ColumnValues[9] = ["note3", "NOTE7"];

        await h.RunAsync();

        h.Issues.Select(i => (i.SourceRowRef, i.ReasonCode, i.Outcome)).Should().BeEquivalentTo(new (long?, string, string)[]
        {
            (3, ImportReason.DuplicateInDestination, ImportOutcome.Skipped), (7, ImportReason.DuplicateInDestination, ImportOutcome.Skipped)
        });
        h.Store.Inserted.Should().HaveCount(8);
    }

    [Fact]
    public async Task In_a_merge_remove_duplicates_compares_within_the_run_only_because_an_existing_value_is_just_the_record_being_updated()
    {
        var h = ImportHarness.Create(Merge(Rule(9, removeDuplicates: true)), new HarnessOptions { RowCount = 3, Row = id => Clean(id) });
        h.Store.ColumnValues[9] = ["note1"]; // would be skipped in a Copy
        foreach (var id in Enumerable.Range(1, 3)) h.Store.Existing[$"n{id}"] = 100 + id;

        await h.RunAsync();

        h.Issues.Should().BeEmpty();
        h.Store.Updated.Should().HaveCount(3);
    }

    [Fact]
    public async Task A_value_only_counts_as_seen_once_its_row_has_passed_every_rule()
    {
        // Row 1 is rejected by the Require rule on Qty, so its note was never "used": row 2 may keep the same note.
        var h = ImportHarness.Create(Copy(Rule(9, removeDuplicates: true), Rule(7, requireField: true)), new HarnessOptions
        {
            RowCount = 2, Row = id => Clean(id, note: "SAME", qty: id == 1 ? "" : "5")
        });

        await h.RunAsync();

        h.Issues.Should().ContainSingle().Which.ReasonCode.Should().Be(ImportReason.RequiredBlank);
        h.Store.Inserted.Should().ContainSingle().Which[6].Should().Be("n2");
    }

    [Fact]
    public async Task A_column_with_remove_duplicates_skips_a_repeated_unique_value_instead_of_reporting_an_error()
    {
        // Name is unique in the destination. With the rule it is a quiet skip; without it the repeat is an error.
        var rows = new HarnessOptions { RowCount = 3, Row = id => Clean(id, name: id == 3 ? "n1" : null) };
        var withRule = ImportHarness.Create(Copy(Rule(6, removeDuplicates: true)), rows);
        var withoutRule = ImportHarness.Create(Copy(), new HarnessOptions { RowCount = 3, Row = id => Clean(id, name: id == 3 ? "n1" : null) });

        await withRule.RunAsync();
        await withoutRule.RunAsync();

        withRule.Issues.Should().ContainSingle().Which.Outcome.Should().Be(ImportOutcome.Skipped);
        withoutRule.Issues.Should().ContainSingle().Which.Outcome.Should().Be(ImportOutcome.Errored);
    }

    // ---- Several rules together ----

    [Fact]
    public async Task Each_column_applies_its_own_rules_independently()
    {
        // Note: require. Qty: ignore blanks. Name: remove duplicates.
        var h = ImportHarness.Create(Copy(Rule(9, requireField: true), Rule(7, ignoreBlanks: true), Rule(6, removeDuplicates: true)), new HarnessOptions
        {
            RowCount = 4,
            Row = id => id switch
            {
                1 => Clean(1),                                  // clean
                2 => Clean(2, note: ""),                        // blank note -> skipped
                3 => Clean(3, qty: ""),                         // blank qty -> imported without qty
                _ => Clean(4, name: "n1")                      // name n1 again -> skipped duplicate
            }
        });

        await h.RunAsync();

        h.Store.Inserted.Should().HaveCount(2);
        h.Store.Inserted.Single(r => Equals(r[6], "n3")).ContainsKey(7).Should().BeFalse();
        h.Issues.Select(i => (i.SourceRowRef, i.ReasonCode)).Should().BeEquivalentTo(new (long?, string)[]
        {
            (2, ImportReason.RequiredBlank), (4, ImportReason.DuplicateInRun)
        });
        h.Accounted.Should().Be(4);
    }

    // ---- Validation of the rules themselves ----

    private static async Task AssertRejected(ImportDefinitionConfig config, string message, HarnessOptions? options = null)
    {
        var h = ImportHarness.Create(config, options);
        await h.RunAsync();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain(message);
        h.Store.Inserted.Should().BeEmpty();
    }

    [Fact]
    public Task A_rule_on_a_field_that_is_not_mapped_is_rejected() =>
        AssertRejected(new ImportDefinitionConfig
        {
            Name = "x", SourceTableId = Source, ImportType = ImportTypes.Copy, ColumnRules = [Rule(9, requireField: true)],
            Mappings = [new() { DestFid = 6, SourceFid = 6 }]
        }, "not mapped");

    [Fact]
    public Task Require_field_and_ignore_blanks_contradict_each_other() =>
        AssertRejected(Copy(Rule(9, requireField: true, ignoreBlanks: true)), "contradict");

    [Fact]
    public Task Two_rule_sets_for_one_column_are_rejected() =>
        AssertRejected(Copy(Rule(9, requireField: true), Rule(9, removeDuplicates: true)), "more than one set of rules");

    [Fact]
    public Task Ignoring_blanks_on_the_merge_key_is_rejected() =>
        AssertRejected(Merge(Rule(6, ignoreBlanks: true)), "cannot be ignored");

    [Fact]
    public async Task Remove_duplicates_on_an_encrypted_field_compares_with_the_decrypted_values_already_in_the_table()
    {
        // The stand-in destination lists the same records as the source, so each source row repeats a value that already exists there.
        // Found by decrypting what is stored (comparing ciphertext could never match), every one is left out as a duplicate.
        Func<long, IReadOnlyDictionary<string, object?>> rows = id =>
            new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = "n" + id, ["f_8"] = "1", ["f_9"] = "Note " + id };
        var encrypted = ImportHarness.Create(Copy(Rule(9, removeDuplicates: true)), new HarnessOptions
        {
            RowCount = 6, Row = rows, ConfigureDestination = d => d.Single(f => f.Fid == 9).IsEncrypted = true
        });
        var plain = ImportHarness.Create(Copy(Rule(9, removeDuplicates: true)), new HarnessOptions { RowCount = 6, Row = rows });

        await encrypted.RunAsync();
        await plain.RunAsync();

        encrypted.Counters.Should().Be((6L, 0L, 0L, 6L, 0L));
        encrypted.Issues.Should().OnlyContain(i => i.ReasonCode == ImportReason.DuplicateInDestination);
        plain.Counters.Should().Be((6L, 6L, 0L, 0L, 0L), "nothing is stored in the stand-in table's own column, so nothing repeats");
    }

    [Fact]
    public async Task A_rule_with_nothing_switched_on_is_simply_ignored()
    {
        var h = ImportHarness.Create(Copy(Rule(9)), new HarnessOptions { RowCount = 3, Row = id => Clean(id) });

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
    }
}
