using ClosedXML.Excel;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>A workbook's sheets read one after the other into the same table.</summary>
public class ImportAllSheetsTests
{
    private static byte[] Workbook(params (string Sheet, string[] Headings, string[][] Rows)[] sheets)
    {
        using var wb = new XLWorkbook();
        foreach (var (name, headings, rows) in sheets)
        {
            var ws = wb.AddWorksheet(name);
            for (var c = 0; c < headings.Length; c++) ws.Cell(1, c + 1).Value = headings[c];
            for (var r = 0; r < rows.Length; r++)
                for (var c = 0; c < rows[r].Length; c++) ws.Cell(r + 2, c + 1).Value = rows[r][c];
        }
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        return ms.ToArray();
    }

    private static ImportDefinitionConfig Config(string columnBy = "name") => new()
    {
        Name = "All sheets", SourceKind = ImportSourceKinds.File, ImportType = ImportTypes.Copy,
        File = new ImportFileOptions { Format = "xlsx", HeaderRow = 1, ColumnBy = columnBy, AllSheets = true, Columns = ["Name", "Qty", "Note"] },
        Mappings = [new() { DestFid = 6, SourceFid = 1001 }, new() { DestFid = 7, SourceFid = 1002 }, new() { DestFid = 9, SourceFid = 1003 }]
    };

    private static ImportHarness Run(byte[] workbook, ImportDefinitionConfig? config = null) =>
        ImportHarness.Create(config ?? Config(), new HarnessOptions { UploadedFile = workbook });

    private static string[] Heads => ["Name", "Qty", "Note"];
    private static string[][] Rows(string prefix, int count) => Enumerable.Range(1, count).Select(i => new[] { $"{prefix}{i}", i.ToString(), "x" }).ToArray();
    private static string Value(IReadOnlyDictionary<long, object?> row, long fid) => Convert.ToString(row.GetValueOrDefault(fid), System.Globalization.CultureInfo.InvariantCulture) ?? "";

    [Fact]
    public async Task Ten_sheets_of_ten_records_import_as_one_hundred()
    {
        var h = Run(Workbook(Enumerable.Range(1, 10).Select(s => ($"Sheet{s}", Heads, Rows($"s{s}-", 10))).ToArray()));

        await h.RunAsync();

        h.Counters.Should().Be((100L, 100L, 0L, 0L, 0L));
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
        h.Store.Inserted.Should().HaveCount(100);
    }

    [Fact]
    public async Task Sheets_are_read_in_workbook_order()
    {
        var h = Run(Workbook(("B", Heads, Rows("b", 2)), ("A", Heads, Rows("a", 2))));

        await h.RunAsync();

        h.Store.Inserted.Select(r => Value(r, 6)).Should().Equal("b1", "b2", "a1", "a2");
    }

    [Fact]
    public async Task A_sheet_with_the_columns_in_a_different_order_is_read_by_heading()
    {
        var h = Run(Workbook(
            ("One", Heads, [["Pen", "1", "a"]]),
            ("Two", ["Note", "Name", "Qty"], [["b", "Pad", "2"]])));

        await h.RunAsync();

        var rows = h.Store.Inserted.ToList();
        rows.Select(r => (Value(r, 6), Value(r, 7), Value(r, 9))).Should().Equal(("Pen", "1", "a"), ("Pad", "2", "b"));
    }

    [Fact]
    public async Task By_position_each_sheet_uses_the_same_places()
    {
        var h = Run(Workbook(
            ("One", ["Product", "Count", "Remark"], [["Pen", "1", "a"]]),
            ("Two", ["x", "y", "z"], [["Pad", "2", "b"]])), Config("position"));

        await h.RunAsync();

        h.Store.Inserted.Select(r => Value(r, 6)).Should().Equal("Pen", "Pad");
    }

    [Fact]
    public async Task A_sheet_missing_a_column_the_import_uses_fails_the_run_before_anything_is_written_and_names_the_sheet()
    {
        var h = Run(Workbook(
            ("January", Heads, Rows("j", 3)),
            ("February", ["Name", "Note"], [["f1", "x"]])));

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("Sheet 'February'").And.Contain("'Qty'");
        h.Store.Inserted.Should().BeEmpty();
    }

    [Fact]
    public async Task A_column_the_import_does_not_use_may_be_missing_from_a_sheet()
    {
        var cfg = Config();
        cfg.Mappings = [new() { DestFid = 6, SourceFid = 1001 }, new() { DestFid = 7, SourceFid = 1002 }]; // Note is not used
        var h = Run(Workbook(("One", Heads, Rows("a", 2)), ("Two", ["Name", "Qty"], [["b1", "1"]])), cfg);

        await h.RunAsync();

        h.Counters.Inserted.Should().Be(3);
    }

    [Fact]
    public async Task An_empty_sheet_is_passed_over()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Data");
        for (var c = 0; c < 3; c++) ws.Cell(1, c + 1).Value = Heads[c];
        ws.Cell(2, 1).Value = "Pen"; ws.Cell(2, 2).Value = 1; ws.Cell(2, 3).Value = "a";
        wb.AddWorksheet("Instructions"); // nothing on it
        using var ms = new MemoryStream();
        wb.SaveAs(ms);

        var h = Run(ms.ToArray());
        await h.RunAsync();

        h.Counters.Inserted.Should().Be(1);
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Success);
    }

    [Fact]
    public async Task A_problem_row_is_reported_with_its_sheet_and_its_row_in_that_sheet()
    {
        var h = Run(Workbook(
            ("January", Heads, [["Pen", "1", "a"], ["Pad", "2", "b"]]),
            ("February", Heads, [["Ink", "3", "c"], ["Pad", "4", "d"]])));   // Pad repeats a name from January

        await h.RunAsync();

        h.Counters.Should().Be((4L, 3L, 0L, 0L, 1L));
        var issue = h.Issues.Single();
        issue.SourceRowRef.Should().Be(3, "the second data row of its sheet is row 3");
        issue.Message.Should().StartWith("Sheet 'February':");
        h.FeedbackCsv.Should().Contain("Sheet 'February'");
    }

    [Fact]
    public async Task One_sheet_alone_still_works_when_the_option_is_on()
    {
        var h = Run(Workbook(("Only", Heads, Rows("o", 5))));

        await h.RunAsync();

        h.Counters.Inserted.Should().Be(5);
    }

    [Fact]
    public async Task Without_the_option_only_the_chosen_sheet_is_read()
    {
        var cfg = Config();
        cfg.File!.AllSheets = false;
        cfg.File.Sheet = "Two";
        var h = Run(Workbook(("One", Heads, Rows("a", 4)), ("Two", Heads, Rows("b", 3))), cfg);

        await h.RunAsync();

        h.Counters.Inserted.Should().Be(3);
    }

    [Fact]
    public async Task Cancelling_part_way_through_a_workbook_stops_it()
    {
        var sheets = Enumerable.Range(1, 3).Select(s => ($"S{s}", Heads, Rows($"s{s}-", 1500))).ToArray();
        var h = ImportHarness.Create(Config(), new HarnessOptions { UploadedFile = Workbook(sheets), CancelAtAdvance = 1 });

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Cancelled);
        h.Counters.Read.Should().BeLessThan(4500);
    }

    // ---- pieces ----

    [Fact]
    public void Row_ids_across_sheets_keep_order_and_split_back()
    {
        var a = ImportFileSheets.RowId(0, 1_048_576);
        var b = ImportFileSheets.RowId(1, 1);
        (b > a).Should().BeTrue("the run's cursor only moves forward");
        ImportFileSheets.Split(ImportFileSheets.RowId(7, 42)).Should().Be((7, 42L));
    }

    [Fact]
    public void Columns_are_found_in_another_sheet_by_heading_or_by_place()
    {
        var canonical = ImportFileColumns.FromHeader(["Name", "Qty"], 2);
        var other = ImportFileColumns.FromHeader(["qty", "Extra", "NAME"], 3);

        ImportFileSheets.Translate(canonical, other, byName: true).Should().Equal(2, 0);
        ImportFileSheets.Translate(canonical, other, byName: false).Should().Equal(0, 1);
        ImportFileSheets.Translate(canonical, ImportFileColumns.FromHeader(["Name"], 1), byName: false).Should().Equal(0, -1);
    }

    [Fact]
    public void Every_sheet_is_checked_for_the_columns_the_import_uses_and_the_message_names_them()
    {
        var first = ImportFileColumns.FromHeader(["Name", "Qty", "Note"], 3);
        var options = new ImportFileOptions { Format = "xlsx", ColumnBy = "name", AllSheets = true, Columns = ["Name", "Qty", "Note"] };
        var file = new ImportFileSource(first, "f", options.ToLayout(), "", 0, 0, options, [
            new ImportFileSheet("One", first, 1, 2),
            new ImportFileSheet("Two", ImportFileColumns.FromHeader(["Name"], 1), 1, 2)]);

        ImportFileSheets.MissingColumns(file, new HashSet<long> { 1001, 1002 }).Should().ContainSingle().Which.Should().Contain("Sheet 'Two'").And.Contain("'Qty'");
        ImportFileSheets.MissingColumns(file, new HashSet<long> { 1001 }).Should().BeEmpty();
    }

    [Fact]
    public void Reading_every_sheet_needs_an_excel_file()
    {
        var act = () => ImportFileValidation.Validate(new ImportFileOptions { Format = "csv", AllSheets = true, Columns = ["a"] });

        act.Should().Throw<ValidationException>().WithMessage("*Excel*");
    }

    [Fact]
    public void The_option_survives_saving_and_drops_the_single_sheet_name()
    {
        var o = ImportFileValidation.Validate(new ImportFileOptions { Format = "xlsx", AllSheets = true, Sheet = "Ignored", Columns = ["a"] });

        o.AllSheets.Should().BeTrue();
        o.Sheet.Should().BeNull();
        var def = new ImportDefinition();
        ImportConfigMapper.Apply(def, new ImportDefinitionConfig { Name = "n", SourceKind = "file", File = o });
        ImportConfigMapper.ToConfig(def, Guid.Empty).File!.AllSheets.Should().BeTrue();
    }

    [Fact]
    public async Task The_preview_totals_every_sheet_and_lists_what_each_lacks()
    {
        var bytes = Workbook(("One", Heads, Rows("a", 3)), ("Two", ["Name", "Qty"], [["b1", "1"], ["b2", "2"]]));
        var files = Substitute.For<IImportFileRepository>();
        var access = Substitute.For<IImportFileAccess>();
        var user = Substitute.For<IQueryContext>();
        user.UserId.Returns(5L);
        var file = new ImportFile { PublicId = Guid.NewGuid(), UploadedByUserId = 5, Format = "xlsx", StoragePath = "/f" };
        files.GetByPublicIdAsync(file.PublicId, Arg.Any<CancellationToken>()).Returns(file);
        access.OpenAsync("/f", Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream>(new MemoryStream(bytes)));

        var preview = await new PreviewImportFileHandler(files, access, user).HandleAsync(file.PublicId, null, null, null, null, default, allSheets: true);

        preview.DataRows.Should().Be(5);
        preview.Columns.Select(c => c.Name).Should().Equal("Name", "Qty", "Note");
        preview.SheetSummaries!.Select(s => (s.Name, s.DataRows)).Should().Equal(("One", 3L), ("Two", 2L));
        preview.SheetSummaries![0].MissingColumns.Should().BeEmpty();
        preview.SheetSummaries![1].MissingColumns.Should().Equal("Note");
    }

    [Fact]
    public void A_chart_sheet_is_not_offered_as_a_sheet_of_data()
    {
        using var wb = new XLWorkbook();
        wb.AddWorksheet("Data").Cell("A1").Value = "x";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;

        ImportXlsxReader.SheetNames(ms).Should().Equal("Data");
    }
}
