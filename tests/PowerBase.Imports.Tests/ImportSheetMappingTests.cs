using NSubstitute;
using ClosedXML.Excel;
using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>A workbook whose sheets have different columns: each table picks the sheets it takes and maps each one's columns separately.</summary>
public class ImportSheetMappingTests
{
    private const long ContactsId = 12;
    private static readonly Guid ContactsPublicId = Guid.NewGuid();

    // Home "Dst": Name (6, required, unique), Qty (7, number), Note (9). Contacts: Title (6), Code (8, required, unique).
    private static HarnessDestination Contacts() => new()
    {
        Id = ContactsId, PublicId = ContactsPublicId, Name = "Contacts",
        Fields = [ImportHarness.RecordId(ContactsId), ImportHarness.Field(ContactsId, 6, "Title", "Text"), ImportHarness.Field(ContactsId, 8, "Code", "Text", required: true, unique: true)]
    };

    private static byte[] Workbook(params (string Sheet, string[] Headings, string[][] Rows, int HeaderRow)[] sheets)
    {
        using var wb = new XLWorkbook();
        foreach (var (name, headings, rows, headerRow) in sheets)
        {
            var ws = wb.AddWorksheet(name);
            for (var c = 0; c < headings.Length; c++) ws.Cell(headerRow, c + 1).Value = headings[c];
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < rows[r].Length; c++) ws.Cell(headerRow + 1 + r, c + 1).Value = rows[r][c];
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static (string, string[], string[][], int) Sheet(string name, string[] headings, params string[][] rows) => (name, headings, rows, 1);

    // People: Name, Qty, Note. Courses: Title, Code. Both: Name, Qty, Title, Code.
    private static byte[] Book(int people = 3, int courses = 2, int both = 2) => Workbook(
        Sheet("People", ["Name", "Qty", "Note"], Enumerable.Range(1, people).Select(i => new[] { $"p{i}", i.ToString(), "x" }).ToArray()),
        Sheet("Courses", ["Title", "Code"], Enumerable.Range(1, courses).Select(i => new[] { $"Course {i}", $"c{i}" }).ToArray()),
        Sheet("Both", ["Name", "Qty", "Title", "Code"], Enumerable.Range(1, both).Select(i => new[] { $"b{i}", (10 + i).ToString(), $"Title b{i}", $"cb{i}" }).ToArray()));

    private static ImportFieldMapping M(int dest, int source) => new() { DestFid = dest, SourceFid = source };

    private static ImportSheetSettings Settings(string name, params string[] columns) => new() { Name = name, HeaderRow = 1, Columns = [.. columns] };

    private static ImportDefinitionConfig Config(Action<ImportDefinitionConfig>? tweak = null)
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "Workbook", SourceKind = ImportSourceKinds.File, ImportType = ImportTypes.Copy,
            File = new ImportFileOptions
            {
                Format = "xlsx", HeaderRow = 1, ColumnBy = "name",
                Sheets = [Settings("People", "Name", "Qty", "Note"), Settings("Courses", "Title", "Code"), Settings("Both", "Name", "Qty", "Title", "Code")]
            },
            // The first table takes the People sheet and the Both sheet; Contacts takes the Courses sheet and the Both sheet.
            SheetMappings =
            [
                new ImportSheetMapping { Sheet = "People", Mappings = [M(6, 1001), M(7, 1002), M(9, 1003)] },
                new ImportSheetMapping { Sheet = "Both", Mappings = [M(6, 1001), M(7, 1002)] }
            ],
            AdditionalTargets =
            [
                new ImportTargetConfig
                {
                    DestinationTableId = ContactsPublicId, ImportType = ImportTypes.Copy,
                    SheetMappings =
                    [
                        new ImportSheetMapping { Sheet = "Courses", Mappings = [M(6, 1001), M(8, 1002)] },
                        new ImportSheetMapping { Sheet = "Both", Mappings = [M(6, 1003), M(8, 1004)] }
                    ]
                }
            ]
        };
        tweak?.Invoke(cfg);
        return cfg;
    }

    private static ImportHarness Run(ImportDefinitionConfig? cfg = null, byte[]? book = null) =>
        ImportHarness.Create(cfg ?? Config(), new HarnessOptions { UploadedFile = book ?? Book(), ExtraDestinations = [Contacts()] });

    private static string Text(IReadOnlyDictionary<long, object?> row, long fid) => Convert.ToString(row.GetValueOrDefault(fid), System.Globalization.CultureInfo.InvariantCulture) ?? "";

    // ---- the business case ----

    [Fact]
    public async Task Each_table_takes_the_rows_of_the_sheets_it_maps_each_with_its_own_columns()
    {
        var h = Run();

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.Store.InsertedByTable[11].Select(r => Text(r, 6)).Should().Equal("p1", "p2", "p3", "b1", "b2");
        h.Store.InsertedByTable[ContactsId].Select(r => Text(r, 8)).Should().BeEquivalentTo("c1", "c2", "cb1", "cb2");
        h.Store.InsertedByTable[ContactsId].Select(r => Text(r, 6)).Should().BeEquivalentTo("Course 1", "Course 2", "Title b1", "Title b2");
    }

    [Fact]
    public async Task The_same_sheet_goes_to_two_tables_each_with_its_own_mapping()
    {
        var h = Run();

        await h.RunAsync();

        // The Both sheet's Name/Qty went to the first table and its Title/Code to Contacts.
        h.Store.InsertedByTable[11].Single(r => Text(r, 6) == "b1")[7].Should().Be(11m);
        h.Store.InsertedByTable[ContactsId].Single(r => Text(r, 8) == "cb1")[6].Should().Be("Title b1");
    }

    [Fact]
    public async Task Counts_are_kept_per_table_across_its_sheets_and_the_run_counts_each_source_row_once()
    {
        var h = Run();

        await h.RunAsync();

        h.TargetCounters[0].Should().Be((5L, 0L, 0L, 0L));
        h.TargetCounters[1].Should().Be((4L, 0L, 0L, 0L));
        h.Counters.Read.Should().Be(3 + 2 + 2, "People, Courses and Both are each read once");
        h.Counters.Inserted.Should().Be(9);
    }

    [Fact]
    public async Task Each_table_has_one_details_file_with_the_sheet_of_every_row_and_the_union_of_the_fields_it_writes()
    {
        var h = Run();

        await h.RunAsync();

        h.DetailsFiles.Should().HaveCount(2);
        h.DetailsFiles[0].Should().StartWith("Source row,Sheet,Result,Record ID#,Reason,Column,Details,Existing record,Name,Qty,Note");
        h.DetailsFiles[0].Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Select(l => l.Split(',')[1]).Should().Equal("People", "People", "People", "Both", "Both");
        h.DetailsFiles[1].Should().StartWith("Source row,Sheet,Result,Record ID#,Reason,Column,Details,Existing record,Title,Code");
        h.DetailsFiles[0].Should().Contain("Inserted");
    }

    [Fact]
    public async Task A_row_that_fails_is_reported_for_its_table_and_sheet_and_the_other_sheets_still_import()
    {
        var book = Workbook(
            Sheet("People", ["Name", "Qty", "Note"], ["p1", "1", "x"], ["p2", "many", "x"]),
            Sheet("Courses", ["Title", "Code"], ["Course", "c1"]),
            Sheet("Both", ["Name", "Qty", "Title", "Code"], ["b1", "5", "T", "cb1"]));
        var h = Run(book: book);

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Partial);
        h.TargetCounters[0].Should().Be((2L, 0L, 0L, 1L));
        h.Issues.Should().ContainSingle(i => i.Message.StartsWith("Dst:") && i.Message.Contains("Sheet"));
    }

    // ---- each sheet's own settings, filter and virtual columns ----

    [Fact]
    public async Task A_sheet_has_its_own_header_row_and_first_data_row()
    {
        var book = Workbook(
            ("People", ["Name", "Qty", "Note"], [["p1", "1", "x"]], 3),   // the column names are on row 3
            Sheet("Courses", ["Title", "Code"], ["Course", "c1"]),
            Sheet("Both", ["Name", "Qty", "Title", "Code"], ["b1", "5", "T", "cb1"]));
        var cfg = Config(c => c.File!.Sheets[0].HeaderRow = 3);

        var h = Run(cfg, book);
        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.Store.InsertedByTable[11].Select(r => Text(r, 6)).Should().Equal("p1", "b1");
    }

    [Fact]
    public async Task A_sheets_filter_decides_which_of_its_rows_go_into_the_table()
    {
        var cfg = Config(c => c.SheetMappings[0].Conditions =
            new FilterGroup { Nodes = [new() { Condition = new() { FieldId = 1001, Operator = "eq", Value = "p2" } }] });

        var h = Run(cfg);
        await h.RunAsync();

        h.Store.InsertedByTable[11].Select(r => Text(r, 6)).Should().Equal("p2", "b1", "b2");
        h.TargetCounters[0].Should().Be((3L, 0L, 0L, 0L));
    }

    [Fact]
    public async Task A_sheet_has_virtual_columns_of_its_own_that_another_table_reading_the_same_sheet_does_not_see()
    {
        var cfg = Config(c =>
        {
            c.SheetMappings[0].VirtualColumns = [new ImportVirtualColumn { Fid = ImportVirtual.FidBase, Name = "Label", Kind = ImportVirtual.Formula, Formula = "[Name] & \"!\"" }];
            c.SheetMappings[0].Mappings = [M(6, 1001), M(7, 1002), M(9, ImportVirtual.FidBase)];
        });

        var h = Run(cfg);
        await h.RunAsync();

        h.Store.InsertedByTable[11].Where(r => Text(r, 6).StartsWith('p')).Select(r => Text(r, 9)).Should().Equal("p1!", "p2!", "p3!");
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
    }

    [Fact]
    public async Task Virtual_columns_of_two_tables_reading_the_same_sheet_do_not_get_mixed_up()
    {
        // Both tables define a virtual column with the same id on the Both sheet, with different formulas.
        var cfg = Config(c =>
        {
            c.SheetMappings[1].VirtualColumns = [new ImportVirtualColumn { Fid = ImportVirtual.FidBase, Name = "Tag", Kind = ImportVirtual.Formula, Formula = "\"home-\" & [Name]" }];
            c.SheetMappings[1].Mappings = [M(6, 1001), M(7, 1002), M(9, ImportVirtual.FidBase)];
            c.AdditionalTargets[0].SheetMappings[1].VirtualColumns = [new ImportVirtualColumn { Fid = ImportVirtual.FidBase, Name = "Tag", Kind = ImportVirtual.Formula, Formula = "\"contacts-\" & [Code]" }];
            c.AdditionalTargets[0].SheetMappings[1].Mappings = [M(6, ImportVirtual.FidBase), M(8, 1004)];
        });

        var h = Run(cfg);
        await h.RunAsync();

        h.Store.InsertedByTable[11].Single(r => Text(r, 6) == "b1")[9].Should().Be("home-b1");
        h.Store.InsertedByTable[ContactsId].Single(r => Text(r, 8) == "cb1")[6].Should().Be("contacts-cb1");
    }

    // ---- across sheets ----

    [Fact]
    public async Task Remove_duplicates_works_across_the_sheets_of_a_table_and_the_first_sheet_wins()
    {
        var book = Workbook(
            Sheet("People", ["Name", "Qty", "Note"], ["same", "1", "x"], ["p2", "2", "x"]),
            Sheet("Courses", ["Title", "Code"], ["Course", "c1"]),
            Sheet("Both", ["Name", "Qty", "Title", "Code"], ["same", "9", "T", "cb1"], ["b2", "5", "T2", "cb2"]));
        var cfg = Config(c => c.ColumnRules = []);
        cfg.SheetMappings[0].ColumnRules = [new ImportColumnRule { DestFid = 6, RemoveDuplicates = true }];
        cfg.SheetMappings[1].ColumnRules = [new ImportColumnRule { DestFid = 6, RemoveDuplicates = true }];

        var h = Run(cfg, book);
        await h.RunAsync();

        h.Store.InsertedByTable[11].Select(r => Text(r, 6)).Should().Equal("same", "p2", "b2");
        h.Issues.Should().ContainSingle(i => i.ReasonCode == ImportReason.DuplicateInRun);
    }

    [Fact]
    public async Task A_unique_value_repeated_on_another_sheet_of_the_same_table_is_an_error_there()
    {
        var book = Workbook(
            Sheet("People", ["Name", "Qty", "Note"], ["same", "1", "x"]),
            Sheet("Courses", ["Title", "Code"], ["Course", "c1"]),
            Sheet("Both", ["Name", "Qty", "Title", "Code"], ["same", "9", "T", "cb1"]));

        var h = Run(book: book);
        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().ContainSingle();
        h.Issues.Should().ContainSingle(i => i.ReasonCode == ImportReason.DuplicateInRun && i.Message.StartsWith("Dst:"));
    }

    [Fact]
    public async Task Abort_if_any_issue_stops_everything_when_a_later_sheet_has_a_bad_row()
    {
        var book = Workbook(
            Sheet("People", ["Name", "Qty", "Note"], ["p1", "1", "x"]),
            Sheet("Courses", ["Title", "Code"], ["Course", "c1"]),
            Sheet("Both", ["Name", "Qty", "Title", "Code"], ["b1", "oops", "T", "cb1"]));
        var cfg = Config(c => c.ConstraintPolicy = ImportConstraintPolicy.AbortIfAnyIssue);

        var h = Run(cfg, book);
        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Store.Inserted.Should().BeEmpty("nothing is written when any row, on any sheet, has a problem");
    }

    [Fact]
    public async Task Skip_duplicate_groups_looks_at_every_sheet_of_the_table()
    {
        var book = Workbook(
            Sheet("People", ["Name", "Qty", "Note"], ["dup", "1", "x"], ["p2", "2", "x"]),
            Sheet("Courses", ["Title", "Code"], ["Course", "c1"]),
            Sheet("Both", ["Name", "Qty", "Title", "Code"], ["dup", "9", "T", "cb1"]));
        var cfg = Config(c => c.ConstraintPolicy = ImportConstraintPolicy.ExcludeDuplicateGroups);

        var h = Run(cfg, book);
        await h.RunAsync();

        h.Store.InsertedByTable[11].Select(r => Text(r, 6)).Should().Equal("p2");
    }

    // ---- a table that reads the file whole next to one that reads sheets ----

    [Fact]
    public async Task A_table_can_read_sheets_while_another_reads_one_sheet_as_the_file_always_did()
    {
        var cfg = Config(c =>
        {
            c.AdditionalTargets[0].SheetMappings = [];
            c.AdditionalTargets[0].Mappings = [M(6, 1001), M(8, 1002)];
            c.File!.Sheet = "Courses";
            c.File.Columns = ["Title", "Code"];
        });

        var h = Run(cfg);
        await h.RunAsync();

        h.Store.InsertedByTable[11].Select(r => Text(r, 6)).Should().Equal("p1", "p2", "p3", "b1", "b2");
        h.Store.InsertedByTable[ContactsId].Select(r => Text(r, 8)).Should().Equal("c1", "c2");
    }

    [Fact]
    public async Task A_single_table_can_read_its_sheets_and_still_gets_a_count_and_a_details_file()
    {
        var cfg = Config(c => c.AdditionalTargets = []);

        var h = Run(cfg);
        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().HaveCount(5);
        h.TargetCounters[0].Should().Be((5L, 0L, 0L, 0L));
        h.DetailsFiles.Should().HaveCount(1);
        h.Counters.Read.Should().Be(5, "only the sheets that are mapped are read");
    }

    // ---- what is refused ----

    [Fact]
    public async Task A_sheet_the_workbook_does_not_have_is_named()
    {
        var book = Workbook(Sheet("People", ["Name", "Qty", "Note"], ["p1", "1", "x"]), Sheet("Courses", ["Title", "Code"], ["C", "c1"]));

        var h = Run(book: book);
        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("'Both'");
        h.Store.Inserted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_sheet_whose_column_names_are_not_where_the_import_expects_them_is_named()
    {
        var book = Workbook(
            ("People", ["Name", "Qty", "Note"], [["p1", "1", "x"]], 5),
            Sheet("Courses", ["Title", "Code"], ["C", "c1"]),
            Sheet("Both", ["Name", "Qty", "Title", "Code"], ["b1", "5", "T", "cb1"]));

        var h = Run(book: book);
        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("Sheet 'People'");
    }

    [Fact]
    public async Task A_column_a_sheet_mapping_needs_that_the_sheet_no_longer_has_is_named()
    {
        var book = Workbook(
            Sheet("People", ["Name", "Qty"], ["p1", "1"]),   // Note is gone
            Sheet("Courses", ["Title", "Code"], ["C", "c1"]),
            Sheet("Both", ["Name", "Qty", "Title", "Code"], ["b1", "5", "T", "cb1"]));

        var h = Run(book: book);
        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("Note");
    }

    [Fact]
    public async Task The_same_sheet_twice_for_one_table_is_refused()
    {
        var cfg = Config(c => c.SheetMappings.Add(new ImportSheetMapping { Sheet = "people", Mappings = [M(6, 1001)] }));

        var act = () => ImportHarness.Create(cfg, new HarnessOptions { UploadedFile = Book(), ExtraDestinations = [Contacts()] }).PlanBuilder!
            .BuildAllAsync(cfg, ImportHarness.DestTableId, default, ImportFileValidation.SavedSource(cfg.File!));

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*'people'*more than once*");
    }

    [Fact]
    public async Task A_sheet_that_has_no_saved_settings_is_refused_when_the_import_is_saved()
    {
        var cfg = Config(c => c.File!.Sheets.RemoveAt(2));

        var act = () => ImportHarness.Create(cfg, new HarnessOptions { UploadedFile = Book(), ExtraDestinations = [Contacts()] }).PlanBuilder!
            .BuildAllAsync(cfg, ImportHarness.DestTableId, default, ImportFileValidation.SavedSource(cfg.File!));

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*'Both'*");
    }

    [Fact]
    public async Task A_sheet_with_nothing_mapped_names_the_sheet()
    {
        var cfg = Config(c => c.SheetMappings[1].Mappings = []);

        var act = () => ImportHarness.Create(cfg, new HarnessOptions { UploadedFile = Book(), ExtraDestinations = [Contacts()] }).PlanBuilder!
            .BuildAllAsync(cfg, ImportHarness.DestTableId, default, ImportFileValidation.SavedSource(cfg.File!));

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*Sheet 'Both'*");
    }

    [Fact]
    public async Task A_table_filter_next_to_sheet_mappings_is_refused_because_each_sheet_has_its_own()
    {
        var cfg = Config(c => c.TableConditions = new FilterGroup { Nodes = [new() { Condition = new() { FieldId = 1001, Operator = "eq", Value = "x" } }] });

        var act = () => ImportHarness.Create(cfg, new HarnessOptions { UploadedFile = Book(), ExtraDestinations = [Contacts()] }).PlanBuilder!
            .BuildAllAsync(cfg, ImportHarness.DestTableId, default, ImportFileValidation.SavedSource(cfg.File!));

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*each sheet's own mapping*");
    }

    [Fact]
    public async Task A_workbook_read_sheet_by_sheet_must_be_a_workbook()
    {
        var cfg = Config(c => c.File!.Format = "csv");

        var act = () => Task.FromResult(ImportFileValidation.Validate(cfg.File));

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*Excel workbook*");
    }

    // ---- saved settings and old imports ----

    [Fact]
    public void Sheet_settings_and_mappings_are_stored_with_the_import_and_read_back()
    {
        var def = new ImportDefinition { Id = 1, DestinationTableId = 11, ImportType = "copy", SourceKind = "file" };
        var cfg = Config();
        cfg.File = ImportFileValidation.Validate(cfg.File);

        ImportConfigMapper.Apply(def, cfg);
        var back = ImportConfigMapper.ToConfig(def, Guid.Empty);

        back.File!.Sheets.Select(s => s.Name).Should().Equal("People", "Courses", "Both");
        back.File.Sheets[2].Columns.Should().Equal("Name", "Qty", "Title", "Code");
        back.SheetMappings.Select(s => s.Sheet).Should().Equal("People", "Both");
        back.AdditionalTargets.Single().SheetMappings.Select(s => s.Sheet).Should().Equal("Courses", "Both");
        back.UsesTargetRows.Should().BeTrue();
    }

    [Fact]
    public void An_import_saved_before_sheets_could_be_mapped_has_none_and_does_not_use_target_rows()
    {
        var def = new ImportDefinition { Id = 1, DestinationTableId = 11, ImportType = "copy", OptionsJson = "{\"constraintPolicy\":\"abortIfAnyIssue\"}" };

        var cfg = ImportConfigMapper.ToConfig(def, Guid.Empty);

        cfg.SheetMappings.Should().BeEmpty();
        cfg.UsesTargetRows.Should().BeFalse();
    }

    [Fact]
    public void Null_lists_from_a_client_mean_none()
    {
        var cfg = ImportJson.Deserialize<ImportDefinitionConfig>("{\"name\":\"x\",\"sheetMappings\":null,\"file\":{\"format\":\"xlsx\",\"sheets\":null}}")!;

        cfg.SheetMappings.Should().BeEmpty();
        cfg.File!.Sheets.Should().BeEmpty();
    }

    [Fact]
    public void The_sheet_settings_are_cleaned_when_saved()
    {
        var saved = ImportFileValidation.Validate(new ImportFileOptions
        {
            Format = "xlsx", Sheets = [new ImportSheetSettings { Name = " People ", HeaderRow = 2, Columns = [" Name ", "Qty"] }]
        });

        saved.Sheets.Single().Name.Should().Be("People");
        saved.Sheets.Single().Columns.Should().Equal("Name", "Qty");
        saved.Columns.Should().BeEmpty("the shared list may be empty when every table reads sheets");
    }

    [Theory]
    [InlineData("", 1, 1)]
    [InlineData("S", 5000, 1)]
    [InlineData("S", 3, 3)]
    [InlineData("S", 1, 0)]
    public void A_bad_sheet_setting_is_refused(string name, int headerRow, int columns)
    {
        var act = () => ImportFileValidation.ValidateSheets(
            [new ImportSheetSettings { Name = name, HeaderRow = headerRow, DataStartRow = headerRow == 3 ? 3 : null, Columns = Enumerable.Repeat("c", columns).ToList() }], "xlsx");

        act.Should().Throw<ValidationException>();
    }

    [Fact]
    public void The_same_sheet_listed_twice_is_refused()
    {
        var act = () => ImportFileValidation.ValidateSheets([Settings("A", "x"), Settings("a", "y")], "xlsx");

        act.Should().Throw<ValidationException>().WithMessage("*more than once*");
    }

    // ---- the run keeps its single-source behaviour ----

    [Fact]
    public async Task An_import_that_does_not_map_sheets_runs_as_it_did()
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "One sheet", SourceKind = ImportSourceKinds.File, ImportType = ImportTypes.Copy,
            File = new ImportFileOptions { Format = "xlsx", HeaderRow = 1, ColumnBy = "name", Sheet = "People", Columns = ["Name", "Qty", "Note"] },
            Mappings = [M(6, 1001), M(7, 1002), M(9, 1003)]
        };

        var h = Run(cfg);
        await h.RunAsync();

        h.Store.Inserted.Should().HaveCount(3);
        h.TargetCounters.Should().BeEmpty("a single-table run from one source keeps no per-table counts");
    }
}

/// <summary>A saved import whose sheet mappings can no longer work is flagged, naming the sheet.</summary>
public class ImportSheetCheckerTests
{
    private const long DestId = 11;
    private readonly PowerBase.Application.Common.Interfaces.IAppTableRepository _tables = NSubstitute.Substitute.For<PowerBase.Application.Common.Interfaces.IAppTableRepository>();
    private readonly PowerBase.Application.Common.Interfaces.IAppFieldRepository _fields = NSubstitute.Substitute.For<PowerBase.Application.Common.Interfaces.IAppFieldRepository>();
    private readonly List<AppField> _dest;
    private readonly ImportDefinitionChecker _sut;

    public ImportSheetCheckerTests()
    {
        _dest = [ImportHarness.RecordId(DestId), ImportHarness.Field(DestId, 6, "Name", "Text", unique: true), ImportHarness.Field(DestId, 7, "Qty", "Number")];
        _tables.GetByIdAsync(DestId, NSubstitute.Arg.Any<CancellationToken>()).Returns(new AppTable { Id = DestId, PublicId = ImportHarness.DestTableId });
        _fields.ListByTableAsync(DestId, NSubstitute.Arg.Any<CancellationToken>()).Returns(_ => _dest);
        _sut = new ImportDefinitionChecker(_tables, _fields, new PowerBase.Formula.FormulaEngine(), NSubstitute.Substitute.For<IImportDefinitionRepository>());
    }

    private static ImportDefinition Saved(Action<ImportDefinitionConfig>? tweak = null)
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "Sheets", SourceKind = ImportSourceKinds.File, ImportType = ImportTypes.Copy,
            File = new ImportFileOptions { Format = "xlsx", HeaderRow = 1, ColumnBy = "name", Sheets = [new ImportSheetSettings { Name = "People", Columns = ["Name", "Qty"] }] },
            SheetMappings = [new ImportSheetMapping { Sheet = "People", Mappings = [new() { DestFid = 6, SourceFid = 1001 }, new() { DestFid = 7, SourceFid = 1002 }] }]
        };
        tweak?.Invoke(cfg);
        var def = new ImportDefinition { Id = 1, PublicId = Guid.NewGuid(), DestinationTableId = DestId, ImportType = "copy", SourceKind = "file" };
        ImportConfigMapper.Apply(def, cfg);
        return def;
    }

    private async Task<ImportDefinition> Check(ImportDefinition def)
    {
        await _sut.RefreshAsync(def, default);
        return def;
    }

    [Fact]
    public async Task A_sound_sheet_mapping_is_not_flagged() => (await Check(Saved())).NeedsAttention.Should().BeFalse();

    [Fact]
    public async Task A_destination_field_a_sheet_writes_to_that_was_deleted_names_the_sheet()
    {
        var def = Saved();
        _dest.RemoveAll(f => f.Fid == 7);

        (await Check(def)).AttentionReason.Should().Contain("Sheet 'People'");
    }

    [Fact]
    public async Task A_sheet_mapping_without_saved_settings_is_flagged()
    {
        var def = Saved(c => c.File!.Sheets = []);

        (await Check(def)).AttentionReason.Should().Contain("no saved settings");
    }

    [Fact]
    public async Task A_sheet_mapping_that_reads_a_column_the_saved_sheet_does_not_have_is_flagged()
    {
        var def = Saved(c => c.SheetMappings[0].Mappings.Add(new() { DestFid = 7, SourceFid = 1009 }));
        def = Saved(c => { c.SheetMappings[0].Mappings = [new() { DestFid = 6, SourceFid = 1009 }]; });

        (await Check(def)).AttentionReason.Should().Contain("Sheet 'People'");
    }

    [Fact]
    public async Task A_sheets_virtual_column_that_stopped_calculating_is_flagged_with_the_sheet()
    {
        var def = Saved(c => c.SheetMappings[0].VirtualColumns = [new ImportVirtualColumn { Fid = ImportVirtual.FidBase, Name = "Bad", Kind = ImportVirtual.Formula, Formula = "[Nothing]" }]);

        (await Check(def)).AttentionReason.Should().Contain("Sheet 'People'").And.Contain("Bad");
    }

    [Fact]
    public async Task A_sheets_filter_on_a_column_it_no_longer_has_is_flagged()
    {
        var def = Saved(c => c.SheetMappings[0].Conditions = new FilterGroup { Nodes = [new() { Condition = new() { FieldId = 1009, Operator = "eq", Value = "x" } }] });

        (await Check(def)).AttentionReason.Should().Contain("Sheet 'People'").And.Contain("filters on");
    }
}
