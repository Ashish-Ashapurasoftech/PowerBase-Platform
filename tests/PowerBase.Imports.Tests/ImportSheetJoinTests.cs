using ClosedXML.Excel;
using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>A record that is spread over the sheets of a workbook: rows of a table's sheets that share a value in a common field are combined into
/// one record before it is checked and written, so a required field that one sheet maps is satisfied by that sheet, and a sheet without the common
/// field is simply imported on its own.</summary>
public class ImportSheetJoinTests
{
    // Destination "Dst": Name (6, required, unique), Qty (7, number), Note (9). Sheets A and B share a key that goes to Note (the common field).
    private const int Name = 6, Qty = 7, Note = 9;

    private static byte[] Workbook(params (string Sheet, string[] Headings, string[][] Rows)[] sheets)
    {
        using var wb = new XLWorkbook();
        foreach (var (name, headings, rows) in sheets)
        {
            var ws = wb.AddWorksheet(name);
            for (var c = 0; c < headings.Length; c++) ws.Cell(1, c + 1).Value = headings[c];
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < rows[r].Length; c++) ws.Cell(2 + r, c + 1).Value = rows[r][c];
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static ImportFieldMapping M(int dest, int source) => new() { DestFid = dest, SourceFid = source };
    private static ImportSheetSettings Settings(string name, params string[] columns) => new() { Name = name, HeaderRow = 1, Columns = [.. columns] };

    // A: Key, Qty.  B: Key, Name.  C (not combined): Name, Qty.
    private static byte[] Book(string[][]? a = null, string[][]? b = null, string[][]? c = null) => Workbook(
        ("A", ["Key", "Qty"], a ?? [["k1", "1"], ["k2", "2"]]),
        ("B", ["Key", "Name"], b ?? [["k1", "n1"], ["k2", "n2"], ["k3", "n3"]]),
        ("C", ["Name", "Qty"], c ?? [["solo", "9"]]));

    private static ImportDefinitionConfig Config(Action<ImportDefinitionConfig>? tweak = null, bool withC = false)
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "Workbook", SourceKind = ImportSourceKinds.File, ImportType = ImportTypes.Copy,
            File = new ImportFileOptions
            {
                Format = "xlsx", HeaderRow = 1, ColumnBy = "name",
                Sheets = [Settings("A", "Key", "Qty"), Settings("B", "Key", "Name"), .. withC ? new[] { Settings("C", "Name", "Qty") } : []]
            },
            SheetMappings =
            [
                new ImportSheetMapping { Sheet = "A", Mappings = [M(Note, 1001), M(Qty, 1002)], JoinFieldFid = Note },
                new ImportSheetMapping { Sheet = "B", Mappings = [M(Note, 1001), M(Name, 1002)], JoinFieldFid = Note },
                .. withC ? new[] { new ImportSheetMapping { Sheet = "C", Mappings = [M(Name, 1001), M(Qty, 1002)] } } : []
            ]
        };
        tweak?.Invoke(cfg);
        return cfg;
    }

    private static ImportHarness Run(ImportDefinitionConfig cfg, byte[]? book = null) =>
        ImportHarness.Create(cfg, new HarnessOptions { UploadedFile = book ?? Book() });

    private static string Text(IReadOnlyDictionary<long, object?> row, long fid) => Convert.ToString(row.GetValueOrDefault(fid), System.Globalization.CultureInfo.InvariantCulture) ?? "";

    [Fact]
    public async Task Rows_of_the_sheets_that_share_the_common_value_become_one_record()
    {
        var h = Run(Config());

        await h.RunAsync();

        var rows = h.Store.InsertedByTable[11];
        rows.Should().HaveCount(3, "k1 and k2 are in both sheets and k3 only in B");
        var k1 = rows.Single(r => Text(r, Note) == "k1");
        Text(k1, Name).Should().Be("n1");
        k1[Qty].Should().Be(1m);
        Text(rows.Single(r => Text(r, Note) == "k2"), Name).Should().Be("n2");
        Text(rows.Single(r => Text(r, Note) == "k3"), Name).Should().Be("n3");
    }

    [Fact]
    public async Task A_required_field_that_only_one_sheet_maps_is_satisfied_by_that_sheet()
    {
        var h = Run(Config());

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.Issues.Should().BeEmpty();
    }

    [Fact]
    public async Task A_record_that_no_sheet_gives_the_required_value_is_reported_once_and_the_rest_import()
    {
        var h = Run(Config(), Book(a: [["k1", "1"], ["k4", "4"]]));

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Partial);
        h.Store.InsertedByTable[11].Should().HaveCount(3);
        h.Issues.Should().ContainSingle(i => i.Message.Contains("'Name' is required") && i.Message.Contains("Sheet 'A'"));
    }

    [Fact]
    public async Task Counts_are_per_record_and_reconcile()
    {
        var h = Run(Config());

        await h.RunAsync();

        h.TargetCounters[0].Should().Be((3L, 0L, 0L, 0L));
        h.Counters.Read.Should().Be(5, "the five rows of the two sheets, which make three records");
        h.Counters.Inserted.Should().Be(3);
    }

    [Fact]
    public async Task A_second_row_with_the_same_value_in_one_sheet_is_a_record_of_its_own()
    {
        var h = Run(Config(), Book(a: [["k1", "1"], ["k1", "5"]], b: [["k1", "n1"]]));

        await h.RunAsync();

        var rows = h.Store.InsertedByTable[11];
        // The first A row and B's row are combined; the second A row has no Name, so it is rejected on its own, not merged or dropped.
        rows.Should().ContainSingle();
        Text(rows[0], Name).Should().Be("n1");
        h.Issues.Should().ContainSingle(i => i.Message.Contains("'Name' is required"));
    }

    [Fact]
    public async Task A_sheet_that_is_not_combined_is_imported_on_its_own()
    {
        var h = Run(Config(withC: true), Book());

        await h.RunAsync();

        var rows = h.Store.InsertedByTable[11];
        rows.Should().HaveCount(4);
        Text(rows.Single(r => Text(r, Name) == "solo"), Note).Should().BeEmpty();
    }

    [Fact]
    public async Task A_row_with_no_common_value_is_a_record_by_itself()
    {
        var h = Run(Config(), Book(a: [["", "1"]], b: [["", "x"], ["k1", "n1"]]));

        await h.RunAsync();

        // Neither blank-key row can be merged with anything: "x" has a Name and imports, A's blank row has none and is rejected.
        h.Store.InsertedByTable[11].Select(r => Text(r, Name)).Should().BeEquivalentTo("x", "n1");
    }

    [Fact]
    public async Task The_values_match_without_regard_to_case_or_surrounding_spaces()
    {
        var h = Run(Config(), Book(a: [["K1", "1"]], b: [[" k1 ", "n1"]]));

        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().ContainSingle();
    }

    [Fact]
    public async Task A_sheets_filter_applies_before_its_rows_are_combined()
    {
        var cfg = Config(c => c.SheetMappings[0].Conditions = FilterEquals(1001, "k2"));
        var h = Run(cfg);

        await h.RunAsync();

        var rows = h.Store.InsertedByTable[11];
        rows.Should().HaveCount(3);
        rows.Single(r => Text(r, Note) == "k1").GetValueOrDefault(Qty).Should().BeNull("A's k1 row was filtered out, so it gave no Qty");
        rows.Single(r => Text(r, Note) == "k2")[Qty].Should().Be(2m);
    }

    [Fact]
    public async Task The_first_sheet_with_a_value_decides_it()
    {
        var book = Workbook(("A", ["Key", "Qty", "Name"], [["k1", "1", "from A"]]), ("B", ["Key", "Name"], [["k1", "from B"]]));
        var cfg = Config(c =>
        {
            c.File!.Sheets = [Settings("A", "Key", "Qty", "Name"), Settings("B", "Key", "Name")];
            c.SheetMappings[0].Mappings = [M(Note, 1001), M(Qty, 1002), M(Name, 1003)];
        });
        var h = Run(cfg, book);

        await h.RunAsync();

        Text(h.Store.InsertedByTable[11].Single(), Name).Should().Be("from A");
    }

    [Fact]
    public async Task Remove_duplicates_works_on_the_combined_records()
    {
        var cfg = Config(c => c.SheetMappings[1].ColumnRules = [new ImportColumnRule { DestFid = Name, RemoveDuplicates = true }]);
        var h = Run(cfg, Book(b: [["k1", "same"], ["k2", "same"]]));

        await h.RunAsync();

        h.Store.InsertedByTable[11].Should().ContainSingle();
        h.Counters.Skipped.Should().Be(1);
    }

    [Fact]
    public async Task A_merge_needs_its_key_in_one_sheet_only()
    {
        var cfg = Config(c => { c.ImportType = ImportTypes.Merge; c.MergeKeyFid = Name; });
        var h = Run(cfg);

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.Store.InsertedByTable[11].Should().HaveCount(3);
    }

    [Fact]
    public async Task A_virtual_column_of_a_sheet_gives_its_value_to_the_combined_record()
    {
        var cfg = Config(c =>
        {
            c.SheetMappings[0].VirtualColumns = [new ImportVirtualColumn { Fid = ImportVirtual.FidBase, Name = "Label", Kind = ImportVirtual.Formula, Formula = "\"x-\" & [Key]" }];
            c.SheetMappings[0].Mappings = [M(Note, 1001), M(Name, ImportVirtual.FidBase)];
            c.SheetMappings[1].Mappings = [M(Note, 1001)];
        });
        var h = Run(cfg);

        await h.RunAsync();

        var rows = h.Store.InsertedByTable[11];
        Text(rows.Single(r => Text(r, Note) == "k1"), Name).Should().Be("x-k1");
        h.Issues.Should().Contain(i => i.Message.Contains("'Name' is required"), "k3 is only in B, which gives no Name");
    }

    [Fact]
    public async Task Sheets_with_no_common_field_are_still_read_as_records_and_a_sheet_may_lack_the_merge_key()
    {
        var cfg = Config(c =>
        {
            c.ImportType = ImportTypes.Merge;
            c.MergeKeyFid = Name;
            foreach (var sheet in c.SheetMappings) { sheet.JoinFieldFid = null; sheet.Combined = true; }
        });
        var h = Run(cfg);

        await h.RunAsync();

        // A has no Name (no key): its two rows are records without the required Name; B's three rows import.
        h.Store.InsertedByTable[11].Select(r => Text(r, Name)).Should().BeEquivalentTo("n1", "n2", "n3");
        h.Issues.Should().HaveCount(2);
    }

    [Fact]
    public async Task With_no_field_chosen_the_sheets_are_combined_on_the_unique_field_they_share()
    {
        // Name is unique and in both sheets; Note (the "common field" of the other tests) is not unique, so it is not guessed.
        var cfg = Config(c =>
        {
            c.SheetMappings[0].Mappings = [M(Name, 1001), M(Qty, 1002)];
            c.SheetMappings[1].Mappings = [M(Name, 1001), M(Note, 1002)];
            c.File!.Sheets = [Settings("A", "Key", "Qty"), Settings("B", "Key", "Note")];
            foreach (var sheet in c.SheetMappings) { sheet.JoinFieldFid = null; sheet.Combined = true; }
        });
        var h = Run(cfg, Workbook(("A", ["Key", "Qty"], [["k1", "1"], ["k2", "2"]]), ("B", ["Key", "Note"], [["k1", "n1"], ["k3", "n3"]])));

        await h.RunAsync();

        var rows = h.Store.InsertedByTable[11];
        rows.Should().HaveCount(3, "k1 is in both sheets");
        var k1 = rows.Single(r => Text(r, Name) == "k1");
        k1[Qty].Should().Be(1m);
        Text(k1, Note).Should().Be("n1");
    }

    [Fact]
    public async Task Choosing_nothing_to_combine_on_keeps_every_row_a_record_of_its_own()
    {
        var cfg = Config(c =>
        {
            c.SheetMappings[0].Mappings = [M(Name, 1001), M(Qty, 1002)];
            c.SheetMappings[1].Mappings = [M(Name, 1001), M(Note, 1002)];
            c.File!.Sheets = [Settings("A", "Key", "Qty"), Settings("B", "Key", "Note")];
            foreach (var sheet in c.SheetMappings) { sheet.JoinFieldFid = 0; sheet.Combined = true; }
        });
        var h = Run(cfg, Workbook(("A", ["Key", "Qty"], [["k1", "1"]]), ("B", ["Key", "Note"], [["k1", "n1"]])));

        await h.RunAsync();

        // The same Name twice: the second is a duplicate of a unique field, so only the first is imported.
        h.Store.InsertedByTable[11].Should().ContainSingle();
        h.Issues.Should().NotBeEmpty();
    }

    [Fact]
    public async Task The_details_file_has_a_row_per_record_that_says_which_sheet_it_started_in()
    {
        var h = Run(Config());

        await h.RunAsync();

        h.DetailsFiles.Should().HaveCount(1);
        h.DetailsFiles[0].Should().StartWith("Source row,Result,Record ID#,Reason,Column,Details,Existing record,Note,Qty,Name");
        h.DetailsFiles[0].Split('\n', StringSplitOptions.RemoveEmptyEntries).Skip(1).Should().HaveCount(3);
        h.DetailsFiles[0].Should().Contain("Sheet 'A'").And.Contain("Sheet 'B'");
    }

    [Fact]
    public async Task Combining_on_two_different_fields_is_refused()
    {
        var cfg = Config(c => c.SheetMappings[1].JoinFieldFid = Name);

        var act = () => Plan(cfg);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*one field only*");
    }

    [Fact]
    public async Task A_sheet_must_map_the_field_it_is_combined_on()
    {
        var cfg = Config(c => c.SheetMappings[1].Mappings = [M(Name, 1002)]);

        var act = () => Plan(cfg);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*Sheet 'B'*field its rows are combined on*");
    }

    [Fact]
    public async Task A_fixed_value_cannot_stand_in_a_combined_mapping()
    {
        var cfg = Config(c => c.SheetMappings[0].Mappings.Add(new ImportFieldMapping { DestFid = Qty + 100, Source = ImportMappingSource.Static, StaticValue = "1" }));

        var act = () => Plan(cfg);

        await act.Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task The_combined_sheets_save_and_read_back_with_the_field_they_are_combined_on()
    {
        var plans = await Plan(Config());

        plans.Should().ContainSingle();
        plans[0].JoinedFrom.Should().HaveCount(2);
        plans[0].JoinedFrom!.All(p => p.JoinFieldFid == Note).Should().BeTrue();
    }

    private static Task<IReadOnlyList<ImportPlan>> Plan(ImportDefinitionConfig cfg) =>
        ImportHarness.Create(cfg, new HarnessOptions { UploadedFile = Book() }).PlanBuilder!
            .BuildAllAsync(cfg, ImportHarness.DestTableId, default, ImportFileValidation.SavedSource(cfg.File!));

    private static PowerBase.Application.Reports.FilterGroup FilterEquals(int fid, string value) => new()
    {
        Nodes = [new() { Condition = new() { FieldId = fid, Operator = "eq", Value = value } }]
    };
}
