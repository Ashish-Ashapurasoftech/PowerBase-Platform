using System.Text;
using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>The filter operator "does not have a duplicate": a row passes when its value in the field does not repeat in the source; with "import the first
/// row" the first row of each repeated value passes too. Rows are counted among those that pass the filter's other conditions, in the order they are read.</summary>
public class ImportDuplicateFilterTests
{
    private const long ContactsId = 12;
    private static readonly Guid ContactsPublicId = Guid.NewGuid();

    private static FilterNode NoDuplicate(int fid, bool keepFirst = false) =>
        new() { Condition = new() { FieldId = fid, Operator = "noDuplicate", Value = keepFirst ? "true" : null } };

    private static FilterNode Is(int fid, string op, string value) => new() { Condition = new() { FieldId = fid, Operator = op, Value = value } };

    private static FilterGroup All(params FilterNode[] nodes) => new() { Logic = "and", Nodes = [.. nodes] };

    // ---- the index ----

    [Fact]
    public void The_index_counts_a_value_however_it_is_written_and_never_counts_a_blank()
    {
        var index = new ImportDuplicateIndex();
        index.Add(6, "Ann", 1); index.Add(6, " ann ", 2); index.Add(6, "ANN", 3); index.Add(6, "Bob", 4);
        index.Add(6, null, 5); index.Add(6, "", 6); index.Add(6, "   ", 7);

        index.IsKept(6, "Ann", 1, keepFirst: false).Should().BeFalse("three rows have it");
        index.IsKept(6, "Bob", 4, keepFirst: false).Should().BeTrue("only one row has it");
        index.IsKept(6, null, 5, keepFirst: false).Should().BeTrue("a blank is never a duplicate");
        index.IsKept(6, "", 6, keepFirst: false).Should().BeTrue();
    }

    [Fact]
    public void With_the_first_row_kept_only_the_first_row_of_a_repeated_value_passes()
    {
        var index = new ImportDuplicateIndex();
        index.Add(6, "Ann", 10); index.Add(6, "Ann", 20); index.Add(6, "Ann", 30);

        index.IsKept(6, "Ann", 10, keepFirst: true).Should().BeTrue();
        index.IsKept(6, "Ann", 20, keepFirst: true).Should().BeFalse();
        index.IsKept(6, "Ann", 30, keepFirst: true).Should().BeFalse();
        index.IsKept(6, "Ann", 10, keepFirst: false).Should().BeFalse();
    }

    [Fact]
    public void Each_field_is_counted_on_its_own_and_a_value_nothing_was_counted_for_passes()
    {
        var index = new ImportDuplicateIndex();
        index.Add(6, "x", 1); index.Add(6, "x", 2);
        index.Add(9, "x", 1);

        index.IsKept(9, "x", 1, keepFirst: false).Should().BeTrue("field 9 has it once");
        index.IsKept(8, "x", 1, keepFirst: false).Should().BeTrue("nothing was counted for field 8");
        new ImportDuplicateIndex().IsKept(6, "x", 1, keepFirst: false).Should().BeTrue("the counting pass did not run");
    }

    // ---- the filter ----

    private static IReadOnlyDictionary<string, object?> Row(long id, string? name, string? qty = null) =>
        new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = name, ["f_8"] = qty };

    private static ImportRowFilter FilterOf(FilterGroup tree) =>
        new(tree, [ImportHarness.Field(10, 6, "Name", "Text"), ImportHarness.Field(10, 8, "Qty", "Text")]);

    [Fact]
    public void While_counting_every_row_passes_and_afterwards_the_duplicates_do_not()
    {
        var filter = FilterOf(All(NoDuplicate(6)));
        var rows = new[] { Row(1, "a"), Row(2, "b"), Row(3, "a"), Row(4, "c") };

        filter.BeginCountingDuplicates();
        rows.Select(filter.Matches).Should().OnlyContain(passes => passes, "counting never filters");
        filter.FinishCountingDuplicates();

        rows.Select(filter.Matches).Should().Equal(false, true, false, true);
    }

    [Fact]
    public void Only_the_rows_that_pass_the_other_conditions_are_counted_whatever_the_order_of_the_conditions()
    {
        // "a" appears twice, but one of them is not "keep": counted among the rows that pass, "a" appears once.
        var rows = new[] { Row(1, "a", "keep"), Row(2, "a", "drop"), Row(3, "b", "keep"), Row(4, "b", "keep") };
        foreach (var tree in new[] { All(NoDuplicate(6), Is(8, "eq", "keep")), All(Is(8, "eq", "keep"), NoDuplicate(6)) })
        {
            var filter = FilterOf(tree);
            filter.BeginCountingDuplicates();
            foreach (var r in rows) filter.Matches(r);
            filter.FinishCountingDuplicates();

            rows.Select(filter.Matches).Should().Equal(true, false, false, false);
        }
    }

    [Fact]
    public void A_filter_without_a_duplicate_condition_is_unchanged_and_needs_no_counting()
    {
        var filter = FilterOf(All(Is(8, "eq", "keep")));

        filter.CountsDuplicates.Should().BeFalse();
        filter.Matches(Row(1, "a", "keep")).Should().BeTrue();
        filter.Matches(Row(2, "a", "drop")).Should().BeFalse();
        ImportRowFilter.DuplicateFields(All(Is(8, "eq", "keep"))).Should().BeEmpty();
        ImportRowFilter.DuplicateFields(new FilterGroup { Nodes = [new() { Group = All(NoDuplicate(6)) }, NoDuplicate(8)] }).Should().Equal(6, 8);
    }

    // ---- a run ----

    private static ImportDefinitionConfig Config(FilterGroup? conditions, Action<ImportDefinitionConfig>? tweak = null)
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "Dups", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Copy, Conditions = conditions,
            Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 9, SourceFid = 9 }]
        };
        tweak?.Invoke(cfg);
        return cfg;
    }

    // Name is unique (the destination's Name must be); Note repeats: rows 1-2 share "pair", rows 3-5 share "triple", rows 7-8 have none, the rest are unique. Qty alternates odd/even.
    private static HarnessOptions Source(int rows = 10, bool encryptedName = false) => new()
    {
        RowCount = rows, EncryptedSourceName = encryptedName,
        Row = id => new Dictionary<string, object?>
        {
            ["Id"] = id, ["f_6"] = "n" + id, ["f_8"] = id % 2 == 0 ? "even" : "odd",
            ["f_9"] = id <= 2 ? "pair" : id <= 5 ? "triple" : id is 7 or 8 ? null : "note" + id
        }
    };

    private static IEnumerable<string> Names(params int[] ids) => ids.Select(i => "n" + i);

    [Fact]
    public async Task A_run_leaves_out_every_row_of_a_repeated_value()
    {
        var h = ImportHarness.Create(Config(All(NoDuplicate(9))), Source());

        await h.RunAsync();

        h.Store.Inserted.Select(r => r[6]).Should().Equal(Names(6, 7, 8, 9, 10));
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success, "a row a filter leaves out is not a problem");
        h.Counters.Read.Should().Be(5);
    }

    [Fact]
    public async Task With_the_first_row_kept_the_first_of_each_repeated_value_is_imported_too()
    {
        var h = ImportHarness.Create(Config(All(NoDuplicate(9, keepFirst: true))), Source());

        await h.RunAsync();

        h.Store.Inserted.Select(r => r[6]).Should().Equal(Names(1, 3, 6, 7, 8, 9, 10));
    }

    [Fact]
    public async Task A_blank_value_is_never_a_duplicate()
    {
        // Rows 7 and 8 both have no Note: they are not duplicates of each other, so both pass.
        var h = ImportHarness.Create(Config(All(NoDuplicate(9))), Source());

        await h.RunAsync();

        h.Store.Inserted.Select(r => r[6]).Should().Contain(Names(7, 8));
    }

    [Fact]
    public async Task Duplicates_far_apart_are_found_across_chunks()
    {
        var rows = 5000;
        var options = Source(rows);
        options.Row = id => new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = "n" + id, ["f_8"] = "x", ["f_9"] = id is 3 or 4800 ? "far" : "note" + id };
        var h = ImportHarness.Create(Config(All(NoDuplicate(9))), options);

        await h.RunAsync();

        h.Store.Inserted.Should().HaveCount(rows - 2).And.NotContain(r => Equals(r[6], "n3") || Equals(r[6], "n4800"));
        h.Counters.Read.Should().Be(rows - 2);
    }

    [Fact]
    public async Task The_other_conditions_decide_which_rows_are_counted()
    {
        // Row 2 is left out by the other condition, so among the rows that pass, "pair" is on row 1 alone and row 1 passes; "triple" still repeats.
        var h = ImportHarness.Create(Config(All(Is(6, "ne", "n2"), NoDuplicate(9))), Source(encryptedName: true));

        await h.RunAsync();

        h.Store.Inserted.Select(r => r[6]).Should().Equal(Names(1, 6, 7, 8, 9, 10));
    }

    [Fact]
    public async Task Merging_on_a_duplicate_free_filter_runs_as_a_merge()
    {
        var cfg = Config(All(NoDuplicate(9)), c => { c.ImportType = ImportTypes.Merge; c.MergeKeyFid = 6; });
        var h = ImportHarness.Create(cfg, Source());

        await h.RunAsync();

        h.Store.Inserted.Select(r => r[6]).Should().Equal(Names(6, 7, 8, 9, 10));
    }

    [Fact]
    public async Task A_file_is_counted_the_same_way()
    {
        var csv = new StringBuilder("Name,Qty,Note\n");
        foreach (var name in new[] { "Ann", "Bob", "ann", "Cid", "Bob", "Dee" }) csv.Append(name + ",1,n\n");
        var cfg = new ImportDefinitionConfig
        {
            Name = "File", SourceKind = ImportSourceKinds.File, ImportType = ImportTypes.Copy,
            File = new ImportFileOptions { Format = "csv", HeaderRow = 1, ColumnBy = "name", Columns = ["Name", "Qty", "Note"] },
            Conditions = All(NoDuplicate(1001)),
            Mappings = [new() { DestFid = 6, SourceFid = 1001 }, new() { DestFid = 7, SourceFid = 1002 }]
        };
        var h = ImportHarness.Create(cfg, new HarnessOptions { UploadedFile = Encoding.UTF8.GetBytes(csv.ToString()) });

        await h.RunAsync();

        h.Store.Inserted.Select(r => r[6]).Should().Equal("Cid", "Dee");

        var keep = ImportHarness.Create(new ImportDefinitionConfig
        {
            Name = "File", SourceKind = ImportSourceKinds.File, ImportType = ImportTypes.Copy,
            File = cfg.File, Conditions = All(NoDuplicate(1001, keepFirst: true)), Mappings = cfg.Mappings
        }, new HarnessOptions { UploadedFile = Encoding.UTF8.GetBytes(csv.ToString()) });
        await keep.RunAsync();

        keep.Store.Inserted.Select(r => r[6]).Should().Equal("Ann", "Bob", "Cid", "Dee");
    }

    // ---- a table's own filter ----

    [Fact]
    public async Task A_table_filter_of_a_one_table_import_works_like_the_import_filter()
    {
        var h = ImportHarness.Create(Config(null, c => c.TableConditions = All(NoDuplicate(9))), Source());

        await h.RunAsync();

        h.Store.Inserted.Select(r => r[6]).Should().Equal(Names(6, 7, 8, 9, 10));
    }

    [Fact]
    public async Task Each_table_counts_for_itself_among_the_rows_that_reach_it()
    {
        var contacts = new HarnessDestination
        {
            Id = ContactsId, PublicId = ContactsPublicId, Name = "Contacts",
            Fields = [ImportHarness.RecordId(ContactsId), ImportHarness.Field(ContactsId, 6, "Title", "Text"), ImportHarness.Field(ContactsId, 8, "Code", "Text")]
        };
        var cfg = Config(null, c =>
        {
            c.TableConditions = All(NoDuplicate(9));                       // the first table: no row whose Note repeats
            c.AdditionalTargets =
            [
                new ImportTargetConfig
                {
                    DestinationTableId = ContactsPublicId, ImportType = ImportTypes.Copy,
                    Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 8, SourceFid = 9 }],
                    Conditions = All(NoDuplicate(9, keepFirst: true))      // the second: the first row of a repeated Note is kept
                }
            ];
        });
        var options = Source();
        options.ExtraDestinations = [contacts];
        var h = ImportHarness.Create(cfg, options);

        await h.RunAsync();

        h.Store.InsertedByTable[11].Select(r => r[6]).Should().Equal(Names(6, 7, 8, 9, 10));
        h.Store.InsertedByTable[ContactsId].Select(r => r[6]).Should().Equal(Names(1, 3, 6, 7, 8, 9, 10));
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
    }

    // ---- saving ----

    [Fact]
    public async Task A_duplicate_condition_on_an_ordinary_field_is_accepted_on_save()
    {
        var cfg = Config(All(NoDuplicate(6, keepFirst: true), NoDuplicate(9)));
        var h = ImportHarness.Create(cfg, Source());

        var plans = await h.PlanBuilder.BuildAllAsync(cfg, ImportHarness.DestTableId, CancellationToken.None);

        plans.Should().ContainSingle();
        plans[0].SourceFilter.Should().BeNull("a duplicate condition never reaches the database");
    }

    [Theory]
    [InlineData("Date")]
    [InlineData("Number")]
    [InlineData("Boolean")]
    [InlineData("User")]
    public async Task A_duplicate_condition_is_accepted_on_the_types_that_hold_one_value(string typeCode)
    {
        var cfg = Config(All(NoDuplicate(8)), c => c.Mappings = [new() { DestFid = 6, SourceFid = 6 }]);
        var options = Source();
        options.ConfigureSource = fields => fields.Single(f => f.Fid == 8).TypeCode = typeCode;
        var h = ImportHarness.Create(cfg, options);

        var plans = await h.PlanBuilder.BuildAllAsync(cfg, ImportHarness.DestTableId, CancellationToken.None);

        plans.Should().ContainSingle();
        plans[0].RowFilter.Should().NotBeNull();
        plans[0].RowFilter!.CountsDuplicates.Should().BeTrue();
    }

    [Fact]
    public async Task A_field_that_holds_several_values_cannot_be_checked_for_duplicates()
    {
        var cfg = Config(All(NoDuplicate(9)), c => c.Mappings = [new() { DestFid = 6, SourceFid = 6 }]);
        var options = Source();
        options.ConfigureSource = fields => fields.Single(f => f.Fid == 9).TypeCode = "MultiSelect";
        var h = ImportHarness.Create(cfg, options);

        var act = () => h.PlanBuilder.BuildAllAsync(cfg, ImportHarness.DestTableId, CancellationToken.None);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*cannot be checked for duplicates*");
    }

    [Fact]
    public async Task A_condition_on_a_field_the_source_does_not_have_and_a_value_that_means_nothing_are_refused()
    {
        var cfg = Config(All(NoDuplicate(99)));
        var h = ImportHarness.Create(cfg, Source());
        var unknown = () => h.PlanBuilder.BuildAllAsync(cfg, ImportHarness.DestTableId, CancellationToken.None);
        await unknown.Should().ThrowAsync<ValidationException>();

        var odd = Config(All(new FilterNode { Condition = new() { FieldId = 6, Operator = "noDuplicate", Value = "maybe" } }));
        var h2 = ImportHarness.Create(odd, Source());
        var badValue = () => h2.PlanBuilder.BuildAllAsync(odd, ImportHarness.DestTableId, CancellationToken.None);
        await badValue.Should().ThrowAsync<ValidationException>().WithMessage("*first row*");
    }

    [Fact]
    public async Task An_import_without_a_duplicate_condition_reads_the_source_once_as_before()
    {
        var h = ImportHarness.Create(Config(All(Is(6, "ne", "n2"))), Source(encryptedName: true));

        await h.RunAsync();

        h.Store.Inserted.Should().HaveCount(9);
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
    }
}
