using System.Text;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.Imports.Tests;

/// <summary>A table's own filter: of the rows the import reads (which already passed the import's conditions), only those that pass this
/// table's filter go into it. Every table can have its own, and each is counted and reported on its own.</summary>
public class ImportTableFilterTests
{
    private const long ContactsId = 12;
    private static readonly Guid ContactsPublicId = Guid.NewGuid();

    private static HarnessDestination Contacts() => new()
    {
        Id = ContactsId, PublicId = ContactsPublicId, Name = "Contacts",
        Fields = [ImportHarness.RecordId(ContactsId), ImportHarness.Field(ContactsId, 6, "Title", "Text"), ImportHarness.Field(ContactsId, 8, "Code", "Text", required: true, unique: true)]
    };

    private static FilterGroup Cond(int field, string op, string? value = null) => new() { Nodes = [new() { Condition = new() { FieldId = field, Operator = op, Value = value } }] };

    private static IReadOnlyDictionary<string, object?> Row(long id, string? qty = "1", string? name = null) => new Dictionary<string, object?>
    {
        ["Id"] = id, ["f_6"] = name ?? "n" + id, ["f_8"] = qty, ["f_9"] = "note " + id
    };

    private static ImportDefinitionConfig Config(Action<ImportDefinitionConfig>? tweak = null)
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "Filtered tables", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Copy,
            Mappings = [new() { DestFid = 6, SourceFid = 6 }, new() { DestFid = 7, SourceFid = 8 }],
            AdditionalTargets =
            [
                new ImportTargetConfig
                {
                    DestinationTableId = ContactsPublicId, ImportType = ImportTypes.Copy,
                    Mappings = [new() { DestFid = 6, SourceFid = 9 }, new() { DestFid = 8, SourceFid = 6 }]
                }
            ]
        };
        tweak?.Invoke(cfg);
        return cfg;
    }

    // Name is encrypted, so the import's own condition on it (when a test has one) is checked on each row too.
    private static HarnessOptions Source(int rows = 40, Func<long, IReadOnlyDictionary<string, object?>>? row = null, bool encrypted = false) => new()
    {
        RowCount = rows, Row = row ?? (id => Row(id)), EncryptedSourceName = encrypted, ExtraDestinations = [Contacts()]
    };

    private static ImportDefinitionConfig WithContactsFilter(FilterGroup filter, Action<ImportDefinitionConfig>? tweak = null) =>
        Config(c => { c.AdditionalTargets[0].Conditions = filter; tweak?.Invoke(c); });

    // ---- which table gets which rows ----

    [Fact]
    public async Task An_added_tables_filter_decides_which_rows_it_gets_and_the_first_table_still_gets_all()
    {
        var h = ImportHarness.Create(WithContactsFilter(Cond(6, "startsWith", "n2")), Source());

        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().HaveCount(40);
        h.Store.InsertedByTable[ContactsId].Should().HaveCount(11).And.OnlyContain(r => ((string)r[8]!).StartsWith("n2"));
        h.TargetCounters[0].Should().Be((40L, 0L, 0L, 0L));
        h.TargetCounters[1].Should().Be((11L, 0L, 0L, 0L));
    }

    [Fact]
    public async Task The_first_tables_own_filter_works_the_same_way()
    {
        var h = ImportHarness.Create(Config(c => c.TableConditions = Cond(6, "startsWith", "n3")), Source());

        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().HaveCount(11).And.OnlyContain(r => ((string)r[6]!).StartsWith("n3"));
        h.Store.InsertedByTable[ContactsId].Should().HaveCount(40);
    }

    [Fact]
    public async Task Each_table_can_have_a_different_filter_and_they_may_overlap()
    {
        var cfg = WithContactsFilter(Cond(6, "startsWith", "n2"), c => c.TableConditions = Cond(6, "wildcard", "*5"));
        var h = ImportHarness.Create(cfg, Source());

        await h.RunAsync();

        // endsWith 5: n5, n15, n25, n35; startsWith n2: n2, n20 .. n29
        h.Store.InsertedByTable[11].Select(r => (string)r[6]!).Should().BeEquivalentTo("n5", "n15", "n25", "n35");
        h.Store.InsertedByTable[ContactsId].Should().HaveCount(11);
        h.Store.InsertedByTable[11].Select(r => (string)r[6]!).Intersect(h.Store.InsertedByTable[ContactsId].Select(r => (string)r[8]!)).Should().Equal("n25");
    }

    [Fact]
    public async Task The_import_conditions_narrow_first_and_a_table_filter_narrows_further()
    {
        var cfg = WithContactsFilter(Cond(6, "wildcard", "*5"), c => c.Conditions = Cond(6, "startsWith", "n2"));
        var h = ImportHarness.Create(cfg, Source(encrypted: true));

        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().HaveCount(11, "the import's conditions apply to every table");
        h.Store.InsertedByTable[ContactsId].Select(r => (string)r[8]!).Should().Equal("n25"); // of n2, n20 .. n29, the one that ends in 5
        h.Counters.Read.Should().Be(11);
    }

    [Fact]
    public async Task A_filter_that_matches_nothing_leaves_that_table_empty_without_failing_the_run()
    {
        var h = ImportHarness.Create(WithContactsFilter(Cond(6, "startsWith", "zzz")), Source());

        await h.RunAsync();

        h.Store.InsertedByTable.GetValueOrDefault(ContactsId, []).Should().BeEmpty();
        h.Store.InsertedByTable[11].Should().HaveCount(40);
        h.TargetCounters.GetValueOrDefault(1).Should().Be((0L, 0L, 0L, 0L));
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
    }

    [Fact]
    public async Task A_table_with_no_filter_takes_every_row_that_was_read()
    {
        var h = ImportHarness.Create(Config(), Source());

        await h.RunAsync();

        h.Store.InsertedByTable[ContactsId].Should().HaveCount(40);
    }

    [Fact]
    public async Task Each_table_gets_its_own_details_file_listing_only_the_rows_it_was_given()
    {
        var h = ImportHarness.Create(WithContactsFilter(Cond(6, "startsWith", "n2")), Source(rows: 30));

        await h.RunAsync();

        h.DetailsFiles.Should().HaveCount(2);
        var home = h.DetailsFiles[0].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        var contacts = h.DetailsFiles[1].Split('\n', StringSplitOptions.RemoveEmptyEntries);
        home.Should().HaveCount(31, "the headings and every row");
        contacts.Should().HaveCount(12, "the headings and the 11 rows that passed its filter");
        contacts.Skip(1).Should().OnlyContain(l => l.Contains(",Inserted,"));
    }

    // ---- counts and reports ----

    [Fact]
    public async Task A_row_a_table_does_not_get_is_not_counted_or_reported_for_it()
    {
        // Row 5's Qty is not a number: the first table rejects it. Contacts' filter leaves row 5 out, and does not use Qty anyway.
        var h = ImportHarness.Create(
            Config(c => { c.TableConditions = Cond(6, "ne", "n5"); c.AdditionalTargets[0].Conditions = Cond(6, "ne", "n5"); }),
            Source(row: id => id == 5 ? Row(id, qty: "many") : Row(id)));

        await h.RunAsync();

        h.TargetCounters[0].Should().Be((39L, 0L, 0L, 0L), "row 5 is not the first table's row, so its bad Qty is not its problem");
        h.TargetCounters[1].Should().Be((39L, 0L, 0L, 0L));
        h.Issues.Should().BeEmpty();
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
    }

    [Fact]
    public async Task A_row_a_table_does_get_is_still_checked_and_reported_for_that_table()
    {
        var h = ImportHarness.Create(Config(c => c.TableConditions = Cond(6, "startsWith", "n1")), Source(row: id => id == 12 ? Row(id, qty: "many") : Row(id)));

        await h.RunAsync();

        h.TargetCounters[0].Errored.Should().Be(1);
        h.Issues.Should().Contain(i => i.Message.StartsWith("Dst:"));
    }

    [Fact]
    public async Task Every_table_reconciles_with_the_rows_it_was_given()
    {
        var h = ImportHarness.Create(WithContactsFilter(Cond(6, "startsWith", "n1"), c => c.TableConditions = Cond(6, "wildcard", "*3")),
            Source(rows: 4500, row: id => id % 7 == 0 ? Row(id, qty: "bad") : Row(id)));

        await h.RunAsync();

        var matched = Enumerable.Range(1, 4500).Select(i => $"n{i}").ToList();
        var home = matched.Count(n => n.EndsWith('3'));
        var contacts = matched.Count(n => n.StartsWith("n1"));
        var homeBad = Enumerable.Range(1, 4500).Count(i => $"n{i}".EndsWith('3') && i % 7 == 0);
        var t0 = h.TargetCounters[0];
        var t1 = h.TargetCounters[1];
        (t0.Inserted + t0.Updated + t0.Skipped + t0.Errored).Should().Be(home);
        t0.Errored.Should().Be(homeBad);
        (t1.Inserted + t1.Updated + t1.Skipped + t1.Errored).Should().Be(contacts);
    }

    [Fact]
    public async Task Run_totals_are_what_the_tables_got()
    {
        var h = ImportHarness.Create(WithContactsFilter(Cond(6, "startsWith", "n2")), Source());

        await h.RunAsync();

        h.Counters.Read.Should().Be(40, "the source rows read, counted once");
        h.Counters.Inserted.Should().Be(40 + 11);
    }

    // ---- the policies that look at every row ----

    [Fact]
    public async Task Abort_if_any_issue_looks_only_at_the_rows_each_table_is_given()
    {
        // Row 9 breaks the first table (bad Qty), but the first table's filter leaves it out: nothing is wrong, so everything is imported.
        var cfg = Config(c => { c.ConstraintPolicy = ImportConstraintPolicy.AbortIfAnyIssue; c.TableConditions = Cond(6, "ne", "n9"); });
        var h = ImportHarness.Create(cfg, Source(row: id => id == 9 ? Row(id, qty: "many") : Row(id)));

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.Store.InsertedByTable[11].Should().HaveCount(39);
        h.Store.InsertedByTable[ContactsId].Should().HaveCount(40);
    }

    [Fact]
    public async Task Abort_if_any_issue_still_stops_when_a_row_a_table_is_given_has_a_problem()
    {
        var cfg = Config(c => { c.ConstraintPolicy = ImportConstraintPolicy.AbortIfAnyIssue; c.TableConditions = Cond(6, "startsWith", "n9"); });
        var h = ImportHarness.Create(cfg, Source(row: id => id == 9 ? Row(id, qty: "many") : Row(id)));

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Store.Inserted.Should().BeEmpty();
    }

    [Fact]
    public async Task Duplicate_groups_are_looked_for_among_the_rows_a_table_is_given()
    {
        // Rows 6 and 7 share a Name, which Contacts takes as its unique Code. Contacts' filter leaves row 7 out, so there is no duplicate there.
        var cfg = WithContactsFilter(Cond(6, "ne", "dup"), c => c.ConstraintPolicy = ImportConstraintPolicy.ExcludeDuplicateGroups);
        var h = ImportHarness.Create(cfg, Source(row: id => id is 6 or 7 ? Row(id, name: id == 6 ? "n6" : "dup") : Row(id)));

        await h.RunAsync();

        h.Store.InsertedByTable[ContactsId].Select(r => (string)r[8]!).Should().Contain("n6");
    }

    // ---- fields the filter needs ----

    [Fact]
    public async Task A_filter_on_a_field_the_table_does_not_map_still_reads_that_field()
    {
        // Contacts maps Note and Name; its filter is on Qty (fid 8), which it does not use. The source must be read with Qty.
        var h = ImportHarness.Create(WithContactsFilter(Cond(8, "eq", "2")), Source());

        var plans = await h.PlanBuilder!.BuildAllAsync(WithContactsFilter(Cond(8, "eq", "2")), ImportHarness.DestTableId, default);

        plans[1].ReadFields.Select(f => f.Fid).Should().Contain(8);
        ImportPlanBuilder.UnionForReading(plans).ReadFields.Select(f => f.Fid).Should().Contain(8);
    }

    [Fact]
    public async Task A_filter_that_compares_two_fields_works()
    {
        var cond = new FilterGroup { Nodes = [new() { Condition = new() { FieldId = 6, Operator = "eq", ValueMode = "field", ValueFieldId = 9 } }] };
        var h = ImportHarness.Create(WithContactsFilter(cond), Source(row: id => id % 2 == 0 ? new Dictionary<string, object?> { ["Id"] = id, ["f_6"] = "v" + id, ["f_8"] = "1", ["f_9"] = "v" + id } : Row(id)));

        await h.RunAsync();

        h.Store.InsertedByTable[ContactsId].Should().HaveCount(20);
    }

    [Fact]
    public async Task A_table_filter_works_on_the_columns_of_a_file()
    {
        var csv = new StringBuilder("Name,Qty,Note\n");
        foreach (var i in Enumerable.Range(1, 20)) csv.Append($"Pen{i},1,n{i}\n");
        foreach (var i in Enumerable.Range(1, 5)) csv.Append($"Ink{i},1,n{i}\n");
        var cfg = new ImportDefinitionConfig
        {
            Name = "File", SourceKind = ImportSourceKinds.File, ImportType = ImportTypes.Copy,
            File = new ImportFileOptions { Format = "csv", HeaderRow = 1, ColumnBy = "name", Columns = ["Name", "Qty", "Note"] },
            Mappings = [new() { DestFid = 6, SourceFid = 1001 }, new() { DestFid = 7, SourceFid = 1002 }],
            AdditionalTargets =
            [
                new ImportTargetConfig
                {
                    DestinationTableId = ContactsPublicId, Mappings = [new() { DestFid = 6, SourceFid = 1003 }, new() { DestFid = 8, SourceFid = 1001 }],
                    Conditions = Cond(1001, "startsWith", "Ink")
                }
            ]
        };
        var h = ImportHarness.Create(cfg, new HarnessOptions { UploadedFile = Encoding.UTF8.GetBytes(csv.ToString()), ExtraDestinations = [Contacts()] });

        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().HaveCount(25);
        h.Store.InsertedByTable[ContactsId].Should().HaveCount(5);
    }

    [Fact]
    public async Task A_table_filter_follows_its_column_when_the_files_columns_move()
    {
        var csv = "Note,Name,Qty\nn1,Ink1,1\nn2,Pen2,1\nn3,Ink3,1\n"; // Name is now the second column
        var cfg = new ImportDefinitionConfig
        {
            Name = "File", SourceKind = ImportSourceKinds.File, ImportType = ImportTypes.Copy,
            File = new ImportFileOptions { Format = "csv", HeaderRow = 1, ColumnBy = "name", Columns = ["Name", "Qty", "Note"] },
            Mappings = [new() { DestFid = 6, SourceFid = 1001 }, new() { DestFid = 7, SourceFid = 1002 }],
            AdditionalTargets =
            [
                new ImportTargetConfig
                {
                    DestinationTableId = ContactsPublicId, Mappings = [new() { DestFid = 6, SourceFid = 1003 }, new() { DestFid = 8, SourceFid = 1001 }],
                    Conditions = Cond(1001, "startsWith", "Ink")
                }
            ]
        };
        var h = ImportHarness.Create(cfg, new HarnessOptions { UploadedFile = Encoding.UTF8.GetBytes(csv), ExtraDestinations = [Contacts()] });

        await h.RunAsync();

        h.Store.InsertedByTable[ContactsId].Select(r => (string)r[8]!).Should().BeEquivalentTo("Ink1", "Ink3");
    }

    // ---- saving ----

    [Fact]
    public async Task A_table_filter_on_an_import_into_one_table_is_refused_because_the_import_conditions_already_do_that()
    {
        var cfg = Config(c => { c.AdditionalTargets = []; c.TableConditions = Cond(6, "eq", "x"); });

        var act = () => ImportHarness.Create(cfg, Source()).PlanBuilder!.BuildAllAsync(cfg, ImportHarness.DestTableId, default);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*more than one table*");
    }

    [Fact]
    public async Task A_filter_on_a_field_the_source_does_not_have_is_refused_and_names_the_table()
    {
        var cfg = WithContactsFilter(Cond(99, "eq", "x"));

        var act = () => ImportHarness.Create(cfg, Source()).PlanBuilder!.BuildAllAsync(cfg, ImportHarness.DestTableId, default);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*Contacts*");
    }

    [Fact]
    public async Task Ask_the_user_values_are_refused_in_a_table_filter_as_in_the_import_conditions()
    {
        var ask = new FilterGroup { Nodes = [new() { Condition = new() { FieldId = 6, Operator = "eq", ValueMode = "ask" } }] };
        var cfg = Config(c => c.TableConditions = ask);

        var act = () => ImportHarness.Create(cfg, Source()).PlanBuilder!.BuildAllAsync(cfg, ImportHarness.DestTableId, default);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*Ask the user*");
    }

    [Fact]
    public void Table_filters_are_stored_with_the_import_and_read_back()
    {
        var def = new ImportDefinition { Id = 1, DestinationTableId = 11, ImportType = "copy" };
        var cfg = Config(c => { c.TableConditions = Cond(6, "eq", "a"); c.AdditionalTargets[0].Conditions = Cond(6, "eq", "b"); });

        ImportConfigMapper.Apply(def, cfg);
        var back = ImportConfigMapper.ToConfig(def, ImportHarness.SourceTableId);

        back.TableConditions!.Nodes.Single().Condition!.Value.Should().Be("a");
        back.AdditionalTargets.Single().Conditions!.Nodes.Single().Condition!.Value.Should().Be("b");
    }

    [Fact]
    public void An_import_saved_before_table_filters_existed_has_none()
    {
        var def = new ImportDefinition { Id = 1, DestinationTableId = 11, ImportType = "copy", OptionsJson = "{\"constraintPolicy\":\"abortIfAnyIssue\"}" };

        var cfg = ImportConfigMapper.ToConfig(def, Guid.Empty);

        cfg.TableConditions.Should().BeNull();
    }

    [Fact]
    public void An_empty_table_filter_is_not_stored()
    {
        var def = new ImportDefinition { Id = 1, DestinationTableId = 11, ImportType = "copy" };

        ImportConfigMapper.Apply(def, Config(c => { c.AdditionalTargets = []; c.TableConditions = new FilterGroup(); }));

        def.OptionsJson.Should().BeNull();
    }

    // ---- needs attention ----

    [Fact]
    public async Task A_deleted_field_a_table_filter_uses_flags_the_import()
    {
        const long sourceId = 10, destId = 11;
        var tables = Substitute.For<IAppTableRepository>();
        var fields = Substitute.For<IAppFieldRepository>();
        tables.GetByIdAsync(sourceId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = sourceId, PublicId = ImportHarness.SourceTableId });
        tables.GetByIdAsync(destId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = destId, PublicId = ImportHarness.DestTableId });
        fields.ListByTableAsync(sourceId, Arg.Any<CancellationToken>()).Returns([ImportHarness.RecordId(sourceId), ImportHarness.Field(sourceId, 6, "Name", "Text")]);
        fields.ListByTableAsync(destId, Arg.Any<CancellationToken>()).Returns([ImportHarness.RecordId(destId), ImportHarness.Field(destId, 6, "Name", "Text")]);
        var checker = new ImportDefinitionChecker(tables, fields, new FormulaEngine(), Substitute.For<IImportDefinitionRepository>());
        var def = new ImportDefinition
        {
            Id = 1, PublicId = Guid.NewGuid(), SourceTableId = sourceId, DestinationTableId = destId, ImportType = "copy",
            FieldMappingJson = ImportJson.Serialize(new List<ImportFieldMapping> { new() { DestFid = 6, SourceFid = 6 } }),
            OptionsJson = ImportJson.Serialize(new { tableConditions = Cond(8, "eq", "x") })
        };

        await checker.RefreshAsync(def, default);

        def.NeedsAttention.Should().BeTrue();
        def.AttentionReason.Should().Contain("filters a table");
    }
}
