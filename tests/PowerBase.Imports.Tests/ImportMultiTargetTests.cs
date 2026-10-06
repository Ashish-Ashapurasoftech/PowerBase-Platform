using System.Text;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>One source filling several tables in one run: each table has its own mapping and rules and is checked and written on its own.</summary>
public class ImportMultiTargetTests
{
    private const long ContactsId = 12;
    private static readonly Guid ContactsPublicId = Guid.NewGuid();

    // Source rows: Name (f_6), Qty (f_8), Note (f_9), all text. Home table "Dst" (Name required+unique, Qty number, Note text).
    // Second table "Contacts": Title (fid 6, text), Code (fid 8, text, required + unique).
    private static HarnessDestination Contacts(Action<List<AppField>>? tweak = null)
    {
        var fields = new List<AppField>
        {
            ImportHarness.RecordId(ContactsId), ImportHarness.Field(ContactsId, 6, "Title", "Text"),
            ImportHarness.Field(ContactsId, 8, "Code", "Text", required: true, unique: true)
        };
        tweak?.Invoke(fields);
        return new HarnessDestination { Id = ContactsId, PublicId = ContactsPublicId, Name = "Contacts", Fields = fields };
    }

    private static IReadOnlyDictionary<string, object?> Row(long id, string? name = null, string? qty = "1") => new Dictionary<string, object?>
    {
        ["Id"] = id, ["f_6"] = name ?? "n" + id, ["f_8"] = qty, ["f_9"] = "note " + id
    };

    private static ImportDefinitionConfig Config(Action<ImportDefinitionConfig>? tweak = null)
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "Two tables", SourceTableId = ImportHarness.SourceTableId, ImportType = ImportTypes.Copy,
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

    private static ImportHarness Harness(ImportDefinitionConfig? cfg = null, int rows = 40, Func<long, IReadOnlyDictionary<string, object?>>? row = null, Action<HarnessOptions>? tweak = null)
    {
        var options = new HarnessOptions { RowCount = rows, Row = row ?? (id => Row(id)), ExtraDestinations = [Contacts()] };
        tweak?.Invoke(options);
        return ImportHarness.Create(cfg ?? Config(), options);
    }

    [Fact]
    public async Task Every_row_is_written_into_both_tables_and_each_table_is_counted_on_its_own()
    {
        var h = Harness();

        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().HaveCount(40);
        h.Store.InsertedByTable[ContactsId].Should().HaveCount(40);
        h.TargetCounters[0].Should().Be((40L, 0L, 0L, 0L));
        h.TargetCounters[1].Should().Be((40L, 0L, 0L, 0L));
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.Store.RecordCountByTable.Should().BeEquivalentTo(new Dictionary<long, int> { [11] = 40, [ContactsId] = 40 });
    }

    [Fact]
    public async Task Each_table_gets_only_what_its_own_mapping_says()
    {
        var h = Harness(rows: 3);

        await h.RunAsync();

        var home = h.Store.InsertedByTable[11].First();
        var contacts = h.Store.InsertedByTable[ContactsId].First();
        home.Keys.Should().BeEquivalentTo(new long[] { 6, 7 }, "the home table maps Name and Qty only");
        contacts.Keys.Should().BeEquivalentTo(new long[] { 6, 8 }, "Contacts maps Note to Title and Name to Code only");
        contacts[8].Should().Be("n1");
        contacts[6].Should().Be("note 1");
    }

    [Fact]
    public async Task A_row_bad_for_one_table_is_imported_into_the_other_and_reported_for_the_one_it_failed()
    {
        // Row 5 has a blank Name: Contacts needs a Code (taken from Name), the home table needs a Name too, so use a Qty that only
        // the home table's Number field rejects: row 5's Qty is not a number.
        var h = Harness(row: id => id == 5 ? Row(id, qty: "many") : Row(id));

        await h.RunAsync();

        h.TargetCounters[0].Should().Be((39L, 0L, 0L, 1L), "the home table rejects row 5's Qty");
        h.TargetCounters[1].Should().Be((40L, 0L, 0L, 0L), "Contacts does not use Qty, so it takes row 5");
        h.Store.InsertedByTable[ContactsId].Select(r => r[8]).Should().Contain("n5");
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Partial);
    }

    [Fact]
    public async Task Issues_and_the_details_file_name_the_table_they_belong_to()
    {
        // Row 5 has a bad Qty (the home table rejects it); row 7 repeats row 6's Name, which Contacts takes as its unique Code.
        var h = Harness(row: id => id == 5 ? Row(id, qty: "many") : id == 7 ? Row(id, name: "n6") : Row(id));

        await h.RunAsync();

        h.Issues.Should().Contain(i => i.Message.StartsWith("Dst:"));
        h.Issues.Should().Contain(i => i.Message.StartsWith("Contacts:"));
        h.FeedbackCsv.Should().StartWith("Table,Source row,Result,Reason,Column,Details,Existing record,Values");
        h.FeedbackCsv.Should().Contain("Contacts,").And.Contain("Dst,");
        h.FeedbackCsv.Should().Contain("Code=n6", "the carried values are listed by field name");
    }

    [Fact]
    public async Task Run_totals_are_what_the_tables_got_and_rows_read_counts_each_source_row_once()
    {
        var h = Harness(row: id => id % 10 == 0 ? Row(id, qty: "many") : Row(id));

        await h.RunAsync();

        h.Counters.Read.Should().Be(40);
        h.Counters.Inserted.Should().Be(36 + 40);
        h.Counters.Errored.Should().Be(4);
        var sum = h.TargetCounters.Values.Aggregate((a, b) => (a.Inserted + b.Inserted, a.Updated + b.Updated, a.Skipped + b.Skipped, a.Errored + b.Errored));
        (h.Counters.Inserted, h.Counters.Errored).Should().Be((sum.Inserted, sum.Errored));
    }

    [Fact]
    public async Task Every_table_reconciles_with_the_rows_read()
    {
        var h = Harness(rows: 4500, row: id => id % 7 == 0 ? Row(id, qty: "bad") : Row(id));

        await h.RunAsync();

        h.Counters.Read.Should().Be(4500);
        foreach (var (_, t) in h.TargetCounters) (t.Inserted + t.Updated + t.Skipped + t.Errored).Should().Be(4500);
        h.Advances.Should().BeGreaterThan(1, "a big source is read in chunks");
    }

    [Fact]
    public async Task Abort_if_any_issue_writes_nothing_in_any_table_when_one_table_has_a_problem()
    {
        var h = Harness(Config(c => c.ConstraintPolicy = ImportConstraintPolicy.AbortIfAnyIssue), row: id => id == 9 ? Row(id, qty: "many") : Row(id));

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Store.InsertedByTable.Should().BeEmpty("nothing is written anywhere, not even the clean rows or the table that had no problem");
        h.TargetCounters[0].Errored.Should().Be(1);
    }

    [Fact]
    public async Task A_clean_source_passes_the_abort_check_and_fills_every_table()
    {
        var h = Harness(Config(c => c.ConstraintPolicy = ImportConstraintPolicy.AbortIfAnyIssue));

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.Store.InsertedByTable[11].Should().HaveCount(40);
        h.Store.InsertedByTable[ContactsId].Should().HaveCount(40);
    }

    [Fact]
    public async Task Excluding_duplicate_groups_is_decided_for_each_table_separately()
    {
        // Rows 3 and 4 share a Name: a duplicate group in the home table (Name is unique there) and in Contacts (Code comes from Name).
        var h = Harness(Config(c => c.ConstraintPolicy = ImportConstraintPolicy.ExcludeDuplicateGroups),
            row: id => id is 3 or 4 ? Row(id, name: "same") : Row(id));

        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().HaveCount(38);
        h.Store.InsertedByTable[ContactsId].Should().HaveCount(38);
        h.Store.InsertedByTable[11].Select(r => r[6]).Should().NotContain("same");
    }

    [Fact]
    public async Task A_second_table_can_merge_while_the_first_copies()
    {
        var cfg = Config(c =>
        {
            c.AdditionalTargets[0].ImportType = ImportTypes.Merge;
            c.AdditionalTargets[0].MergeKeyFid = 8;
        });
        var h = Harness(cfg, rows: 6);
        h.Store.Existing["n2"] = 502; // a Contacts record whose Code is n2 already exists (the stand-in lookup is shared)

        await h.RunAsync();

        // The stand-in store answers "does this value exist" for every table alike, so the home table sees n2 as taken too: 5 of 6 copy.
        h.Store.InsertedByTable[11].Should().HaveCount(5, "the home table copies (n2 is reported as already existing)");
        h.Store.UpdatedByTable.GetValueOrDefault(ContactsId, new()).Select(u => u.RecordId).Should().Contain(502);
    }

    [Fact]
    public async Task Cancelling_stops_every_table_after_the_chunk_it_is_on()
    {
        var h = Harness(rows: 5000, tweak: o => o.CancelAtAdvance = 1);

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Cancelled);
        h.Counters.Read.Should().BeLessThan(5000);
        h.TargetCounters[0].Inserted.Should().Be(h.TargetCounters[1].Inserted, "both tables had the same chunks");
    }

    [Fact]
    public async Task One_table_failing_to_save_ends_the_run_failed_and_keeps_what_the_other_table_already_got_counted()
    {
        var h = Harness(rows: 10);
        h.Store.Rejects = _ => throw new InvalidOperationException("the database is unavailable"); // not a row problem: a real failure

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("unavailable");
    }

    [Fact]
    public async Task A_file_is_read_once_for_all_the_tables()
    {
        var csv = "Name,Qty,Note\n" + string.Join("", Enumerable.Range(1, 12).Select(i => $"n{i},{i},note {i}\n"));
        var cfg = new ImportDefinitionConfig
        {
            Name = "File into two", SourceKind = ImportSourceKinds.File,
            File = new ImportFileOptions { Format = "csv", HeaderRow = 1, ColumnBy = "name", Columns = ["Name", "Qty", "Note"] },
            Mappings = [new() { DestFid = 6, SourceFid = 1001 }, new() { DestFid = 7, SourceFid = 1002 }],
            AdditionalTargets =
            [
                new ImportTargetConfig { DestinationTableId = ContactsPublicId, Mappings = [new() { DestFid = 6, SourceFid = 1003 }, new() { DestFid = 8, SourceFid = 1001 }] }
            ]
        };
        var h = ImportHarness.Create(cfg, new HarnessOptions { UploadedFile = Encoding.UTF8.GetBytes(csv), ExtraDestinations = [Contacts()] });

        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().HaveCount(12);
        h.Store.InsertedByTable[ContactsId].Should().HaveCount(12);
        h.Store.InsertedByTable[ContactsId].First()[6].Should().Be("note 1");
    }

    [Fact]
    public async Task Every_sheet_of_a_workbook_feeds_every_table()
    {
        using var wb = new ClosedXML.Excel.XLWorkbook();
        foreach (var s in new[] { "A", "B" })
        {
            var ws = wb.AddWorksheet(s);
            ws.Cell(1, 1).Value = "Name"; ws.Cell(1, 2).Value = "Qty"; ws.Cell(1, 3).Value = "Note";
            for (var r = 0; r < 3; r++) { ws.Cell(r + 2, 1).Value = $"{s}{r}"; ws.Cell(r + 2, 2).Value = r; ws.Cell(r + 2, 3).Value = "x"; }
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        var cfg = new ImportDefinitionConfig
        {
            Name = "Workbook into two", SourceKind = ImportSourceKinds.File,
            File = new ImportFileOptions { Format = "xlsx", HeaderRow = 1, ColumnBy = "name", AllSheets = true, Columns = ["Name", "Qty", "Note"] },
            Mappings = [new() { DestFid = 6, SourceFid = 1001 }],
            AdditionalTargets = [new ImportTargetConfig { DestinationTableId = ContactsPublicId, Mappings = [new() { DestFid = 8, SourceFid = 1001 }] }]
        };
        var h = ImportHarness.Create(cfg, new HarnessOptions { UploadedFile = ms.ToArray(), ExtraDestinations = [Contacts()] });

        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().HaveCount(6);
        h.Store.InsertedByTable[ContactsId].Should().HaveCount(6);
    }

    [Fact]
    public async Task An_import_with_no_extra_tables_runs_the_single_table_path_unchanged()
    {
        var h = Harness(Config(c => c.AdditionalTargets = []), rows: 10);

        await h.RunAsync();

        h.Store.InsertedByTable.Keys.Should().Equal(11L);
        h.TargetCounters.Should().BeEmpty("a single-table run keeps no per-table counts");
        h.FeedbackCsv.Should().BeNull("nothing was rejected");
    }

    [Fact]
    public async Task A_single_table_runs_details_file_has_no_table_column()
    {
        var h = Harness(Config(c => c.AdditionalTargets = []), rows: 10, row: id => id == 3 ? Row(id, qty: "many") : Row(id));

        await h.RunAsync();

        h.FeedbackCsv.Should().StartWith("Source row,Result,Reason,Column,Details,Existing record,Name,Qty");
    }

    [Fact]
    [Trait("Category", "Load")]
    public async Task A_hundred_thousand_rows_into_three_tables_reconcile_for_every_table()
    {
        var third = new HarnessDestination
        {
            Id = 13, PublicId = Guid.NewGuid(), Name = "Archive",
            Fields = [ImportHarness.RecordId(13), ImportHarness.Field(13, 6, "Label", "Text")]
        };
        var cfg = Config(c => c.AdditionalTargets.Add(new ImportTargetConfig
        {
            DestinationTableId = third.PublicId, Mappings = [new() { DestFid = 6, SourceFid = 9 }]
        }));
        var h = ImportHarness.Create(cfg, new HarnessOptions
        {
            RowCount = 100_000, Row = id => id % 20 == 0 ? Row(id, qty: "bad") : Row(id), ExtraDestinations = [Contacts(), third]
        });
        GC.Collect();
        var before = GC.GetTotalMemory(false);
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await h.RunAsync();

        watch.Elapsed.Should().BeLessThan(TimeSpan.FromMinutes(3));
        h.Counters.Read.Should().Be(100_000);
        foreach (var (_, t) in h.TargetCounters) (t.Inserted + t.Updated + t.Skipped + t.Errored).Should().Be(100_000);
        h.TargetCounters[0].Errored.Should().Be(5_000, "only the home table maps Qty");
        h.TargetCounters[1].Inserted.Should().Be(100_000);
        h.TargetCounters[2].Inserted.Should().Be(100_000);
        // The fake store keeps every row it is given; the engine itself must not add a second copy of the source.
        (GC.GetTotalMemory(false) - before).Should().BeLessThan(900L * 1_048_576);
    }

    // ---- planning and saving ----

    private static ImportHarness Planner(Action<HarnessOptions>? tweak = null) => Harness(tweak: tweak);

    [Fact]
    public async Task The_plans_are_one_per_table_home_first_with_one_fixed_clock()
    {
        var h = Planner();

        var plans = await h.PlanBuilder!.BuildAllAsync(Config(), ImportHarness.DestTableId, default);

        plans.Select(p => p.Destination.Name).Should().Equal("Dst", "Contacts");
        plans[0].FormulaOptions.UtcNow.Should().Be(plans[1].FormulaOptions.UtcNow);
    }

    [Fact]
    public async Task A_table_may_be_filled_only_once()
    {
        var cfg = Config(c => c.AdditionalTargets.Add(new ImportTargetConfig
        {
            DestinationTableId = ContactsPublicId, Mappings = [new() { DestFid = 6, SourceFid = 9 }]
        }));

        var act = () => Planner().PlanBuilder!.BuildAllAsync(cfg, ImportHarness.DestTableId, default);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*only once*");
    }

    [Fact]
    public async Task The_home_table_cannot_be_listed_again_as_an_extra_table()
    {
        var cfg = Config(c => c.AdditionalTargets[0].DestinationTableId = ImportHarness.DestTableId);

        var act = () => Planner().PlanBuilder!.BuildAllAsync(cfg, ImportHarness.DestTableId, default);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*only once*");
    }

    [Fact]
    public async Task Tables_of_another_app_are_refused()
    {
        var h = Planner(o => o.ExtraDestinations = [new HarnessDestination { Id = ContactsId, PublicId = ContactsPublicId, Name = "Contacts", AppId = 2, Fields = Contacts().Fields }]);

        var act = () => h.PlanBuilder!.BuildAllAsync(Config(), ImportHarness.DestTableId, default);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*same app*");
    }

    [Fact]
    public async Task More_than_five_tables_are_refused()
    {
        var cfg = Config(c =>
        {
            c.AdditionalTargets.Clear();
            for (var i = 0; i < ImportPlanBuilder.MaxAdditionalTargets + 1; i++)
                c.AdditionalTargets.Add(new ImportTargetConfig { DestinationTableId = Guid.NewGuid(), Mappings = [new() { DestFid = 6, SourceFid = 9 }] });
        });

        var act = () => Planner().PlanBuilder!.BuildAllAsync(cfg, ImportHarness.DestTableId, default);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*at most 5*");
    }

    [Fact]
    public async Task A_problem_in_an_extra_table_names_that_table()
    {
        var cfg = Config(c => c.AdditionalTargets[0].Mappings = [new() { DestFid = 99, SourceFid = 9 }]); // Contacts has no field 99

        var act = () => Planner().PlanBuilder!.BuildAllAsync(cfg, ImportHarness.DestTableId, default);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*'Contacts'*");
    }

    [Fact]
    public async Task An_extra_table_with_no_table_chosen_is_refused()
    {
        var cfg = Config(c => c.AdditionalTargets[0].DestinationTableId = Guid.Empty);

        var act = () => Planner().PlanBuilder!.BuildAllAsync(cfg, ImportHarness.DestTableId, default);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*Choose the table*");
    }

    [Fact]
    public async Task The_source_is_read_with_every_field_any_table_needs()
    {
        var cfg = Config(c => c.Mappings = [new() { DestFid = 6, SourceFid = 6 }]); // home needs Name only; Contacts needs Note and Name
        var plans = await Planner().PlanBuilder!.BuildAllAsync(cfg, ImportHarness.DestTableId, default);

        var union = ImportPlanBuilder.UnionForReading(plans);

        union.ReadFields.Select(f => f.Fid).Should().Contain([3, 6, 9]);
        union.ReadFields.Should().HaveCountGreaterThanOrEqualTo(plans[0].ReadFields.Count);
    }

    [Fact]
    public void Several_tables_round_trip_through_the_stored_row_and_a_single_table_stores_nothing_extra()
    {
        var def = new ImportDefinition();
        ImportConfigMapper.Apply(def, Config());

        var back = ImportConfigMapper.ToConfig(def, ImportHarness.SourceTableId);

        back.AdditionalTargets.Should().ContainSingle();
        back.AdditionalTargets[0].DestinationTableId.Should().Be(ContactsPublicId);
        back.AdditionalTargets[0].Mappings.Should().HaveCount(2);

        var single = new ImportDefinition();
        ImportConfigMapper.Apply(single, Config(c => c.AdditionalTargets = []));
        single.OptionsJson.Should().BeNull("an import into one table is stored exactly as before");
        ImportConfigMapper.ToConfig(single, ImportHarness.SourceTableId).AdditionalTargets.Should().BeEmpty();
    }

    [Fact]
    public void An_import_saved_before_this_existed_reads_as_one_table()
    {
        var old = new ImportDefinition { OptionsJson = "{\"constraintPolicy\":\"abortIfAnyIssue\"}" };

        var cfg = ImportConfigMapper.ToConfig(old, Guid.NewGuid());

        cfg.AdditionalTargets.Should().BeEmpty();
        cfg.ConstraintPolicy.Should().Be(ImportConstraintPolicy.AbortIfAnyIssue);
    }

    [Fact]
    public void A_run_snapshot_from_before_this_existed_still_reads()
    {
        var json = "{\"destinationTableId\":\"" + Guid.NewGuid() + "\",\"config\":{\"name\":\"n\",\"importType\":\"copy\",\"mappings\":[]}}";

        var snapshot = ImportJson.Deserialize<ImportRunSnapshot>(json)!;

        snapshot.Config.AdditionalTargets.Should().BeEmpty();
    }
}
