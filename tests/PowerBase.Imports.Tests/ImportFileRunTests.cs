using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

/// <summary>A file import through the real engine: the file is read as a stream, mapped, checked row by row and written like any other
/// source, with the row's number in the file as its reference.</summary>
public class ImportFileRunTests
{
    private const int Name = 1001, Qty = 1002, Note = 1003;

    private static ImportDefinitionConfig Config(Action<ImportDefinitionConfig>? tweak = null, string columnBy = "name", params string[] columns)
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "From file", SourceKind = ImportSourceKinds.File, ImportType = ImportTypes.Copy,
            File = new ImportFileOptions { Format = "csv", HeaderRow = 1, ColumnBy = columnBy, Columns = columns.Length > 0 ? columns.ToList() : ["Name", "Qty", "Note"] },
            Mappings = [new() { DestFid = 6, SourceFid = Name }, new() { DestFid = 7, SourceFid = Qty }, new() { DestFid = 9, SourceFid = Note }]
        };
        tweak?.Invoke(cfg);
        return cfg;
    }

    private static ImportHarness Harness(string csv, ImportDefinitionConfig? config = null, Action<HarnessOptions>? tweak = null)
    {
        var options = new HarnessOptions { UploadedFile = Encoding.UTF8.GetBytes(csv) };
        tweak?.Invoke(options);
        return ImportHarness.Create(config ?? Config(), options);
    }

    private static string Value(IReadOnlyDictionary<long, object?> row, long fid) => Convert.ToString(row.GetValueOrDefault(fid), System.Globalization.CultureInfo.InvariantCulture) ?? "";

    [Fact]
    public async Task Good_rows_are_imported_bad_rows_are_reported_with_their_row_in_the_file()
    {
        var h = Harness("Name,Qty,Note\nPen,1,a\nPad,2,b\n,3,c\nPen,4,d\nInk,x,e\nCap,5\n");

        await h.RunAsync();

        h.Counters.Should().Be((6L, 3L, 0L, 0L, 3L));
        h.Accounted.Should().Be(6);
        h.Store.Inserted.Select(r => Value(r, 6)).Should().Equal("Pen", "Pad", "Cap");
        h.Issues.Select(i => i.SourceRowRef).Should().BeEquivalentTo(new long?[] { 4, 5, 6 });
        h.Issues.Single(i => i.SourceRowRef == 6).ReasonCode.Should().Be(ImportReason.TypeMismatch);
        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Partial);
    }

    [Fact]
    public async Task The_uploaded_file_is_kept_for_the_retention_when_the_run_ends_so_the_history_can_show_it()
    {
        var h = Harness("Name,Qty,Note\nPen,1,a\n");
        var before = DateTime.UtcNow;

        await h.RunAsync();

        await h.FileStorage!.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
        await h.FileRepository!.DidNotReceiveWithAnyArgs().DeleteAsync(default, default);
        await h.FileRepository!.Received(1).RetainAsync(Arg.Any<Guid>(), Arg.Is<DateTime>(d => d > before.AddDays(29) && d < before.AddDays(31)), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task The_file_is_kept_even_when_the_run_fails()
    {
        var h = Harness("Name,Qty,Note\nPen,1,a\n", Config(c => c.Mappings.Add(new() { DestFid = 99, SourceFid = Note })));

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        await h.FileStorage!.DidNotReceiveWithAnyArgs().DeleteAsync(default!, default);
        await h.FileRepository!.Received(1).RetainAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_column_is_found_by_its_heading_wherever_it_sits_in_a_new_file()
    {
        var h = Harness("Qty,Note,Extra,Name\n7,a,zzz,Pen\n");

        await h.RunAsync();

        h.Counters.Inserted.Should().Be(1);
        var row = h.Store.Inserted.Single();
        (Value(row, 6), Value(row, 7), Value(row, 9)).Should().Be(("Pen", "7", "a"));
    }

    [Fact]
    public async Task A_heading_is_matched_ignoring_case_and_spaces_around_it()
    {
        var h = Harness(" NAME ,qty,note\nPen,1,a\n");

        await h.RunAsync();

        h.Counters.Inserted.Should().Be(1);
    }

    [Fact]
    public async Task A_file_missing_a_column_the_import_needs_fails_before_anything_is_written()
    {
        var h = Harness("Name,Note\nPen,a\n");

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("'Qty'");
        h.Store.Inserted.Should().BeEmpty();
    }

    [Fact]
    public async Task By_position_a_column_is_the_one_in_the_same_place_whatever_its_heading()
    {
        var h = Harness("Product,Count,Remark\nPen,1,a\n", Config(columnBy: "position"));

        await h.RunAsync();

        var row = h.Store.Inserted.Single();
        (Value(row, 6), Value(row, 7), Value(row, 9)).Should().Be(("Pen", "1", "a"));
    }

    [Fact]
    public async Task By_position_a_file_with_too_few_columns_is_refused()
    {
        var h = Harness("Product,Count\nPen,1\n", Config(columnBy: "position"));

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("fewer columns");
    }

    [Fact]
    public async Task A_formula_reads_the_files_columns_by_their_headings()
    {
        var h = Harness("Name,Qty,Note\nPen,1,a\n", Config(c =>
            c.Mappings = [new() { DestFid = 6, SourceFid = Name }, new() { DestFid = 9, Source = ImportMappingSource.Formula, Formula = "Upper([Name]) & \"-\" & [Qty]" }]));

        await h.RunAsync();

        Value(h.Store.Inserted.Single(), 9).Should().Be("PEN-1");
    }

    [Fact]
    public async Task A_fixed_value_goes_to_every_row()
    {
        var h = Harness("Name,Qty,Note\nPen,1,a\nPad,2,b\n", Config(c =>
            c.Mappings = [new() { DestFid = 6, SourceFid = Name }, new() { DestFid = 9, Source = ImportMappingSource.Static, StaticValue = "imported" }]));

        await h.RunAsync();

        h.Store.Inserted.Select(r => Value(r, 9)).Should().Equal("imported", "imported");
    }

    [Fact]
    public async Task Conditions_are_checked_on_each_row_of_the_file()
    {
        var conditions = new FilterGroup { Nodes = [new() { Condition = new FilterCondition { FieldId = Note, Operator = "eq", Value = "x" } }] };
        var h = Harness("Name,Qty,Note\nPen,1,x\nPad,2,y\nInk,3,x\n", Config(c => c.Conditions = conditions));

        await h.RunAsync();

        h.Store.Inserted.Select(r => Value(r, 7)).Should().Equal("1", "3");
    }

    [Fact]
    public async Task Blank_lines_are_not_rows_and_do_not_shift_the_row_numbers_reported()
    {
        var h = Harness("Name,Qty,Note\nPen,1,a\n\n,,\n,2,b\n");

        await h.RunAsync();

        h.Counters.Read.Should().Be(2);
        h.Issues.Single().SourceRowRef.Should().Be(5, "the blank line (3) and the empty cells (4) are not rows; the next row is row 5 in the file");
    }

    [Fact]
    public async Task A_header_only_file_fails_with_a_clear_message()
    {
        var h = Harness("Name,Qty,Note\n");

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("no rows");
    }

    [Fact]
    public async Task A_data_start_row_below_the_header_skips_the_rows_between()
    {
        var h = Harness("Name,Qty,Note\ntext,number,text\nPen,1,a\n", Config(c => c.File!.DataStartRow = 3));

        await h.RunAsync();

        h.Store.Inserted.Single().Should().ContainKey(6);
        Value(h.Store.Inserted.Single(), 6).Should().Be("Pen");
    }

    [Fact]
    public async Task A_file_without_a_header_is_mapped_by_position()
    {
        var h = Harness("Pen,1,a\nPad,2,b\n", Config(c => c.File!.HeaderRow = 0, columnBy: "position"));

        await h.RunAsync();

        h.Store.Inserted.Select(r => Value(r, 6)).Should().Equal("Pen", "Pad");
    }

    [Fact]
    public async Task A_file_that_is_gone_fails_with_advice_to_upload_again()
    {
        var h = Harness("Name,Qty,Note\nPen,1,a\n");
        ((IFileStorageReadService)h.FileStorage!).OpenReadAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<Task<Stream>>(_ => throw new FileNotFoundException());

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Failed);
        h.Completion!.Value.Detail.Should().Contain("Upload it again");
    }

    [Fact]
    public async Task A_big_file_is_read_in_chunks_and_every_row_is_accounted_for()
    {
        var sb = new StringBuilder("Name,Qty,Note\n");
        for (var i = 1; i <= 5000; i++) sb.Append("n").Append(i).Append(',').Append(i % 50 == 0 ? "bad" : i.ToString()).Append(",x\n");
        var h = Harness(sb.ToString());

        await h.RunAsync();

        h.Counters.Read.Should().Be(5000);
        h.Counters.Errored.Should().Be(100);
        h.Counters.Inserted.Should().Be(4900);
        h.Accounted.Should().Be(5000);
        h.Advances.Should().BeGreaterThan(1);
    }

    [Fact]
    public async Task Dates_and_checkboxes_in_a_workbook_are_converted_to_the_fields_types()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Data");
        ws.Cell("A1").Value = "Name"; ws.Cell("B1").Value = "When"; ws.Cell("C1").Value = "Active"; ws.Cell("D1").Value = "Text date";
        ws.Cell("A2").Value = "Pen"; ws.Cell("B2").Value = new DateTime(2026, 3, 9); ws.Cell("C2").Value = true; ws.Cell("D2").Value = "yes";
        ws.Cell("A3").Value = "Pad"; ws.Cell("B3").Value = new DateTime(2026, 3, 10, 8, 30, 0); ws.Cell("C3").Value = false; ws.Cell("D3").Value = "maybe";
        using var ms = new MemoryStream();
        wb.SaveAs(ms);

        var cfg = Config(c =>
        {
            c.File = new ImportFileOptions { Format = "xlsx", HeaderRow = 1, Columns = ["Name", "When", "Active", "Text date"] };
            c.Mappings = [new() { DestFid = 6, SourceFid = 1001 }, new() { DestFid = 12, SourceFid = 1002 }, new() { DestFid = 13, SourceFid = 1003 }, new() { DestFid = 14, SourceFid = 1004 }];
        });
        var h = ImportHarness.Create(cfg, new HarnessOptions
        {
            UploadedFile = ms.ToArray(),
            ConfigureDestination = d =>
            {
                d.Add(ImportHarness.Field(11, 12, "When", "Date"));
                d.Add(ImportHarness.Field(11, 13, "Active", "Boolean"));
                d.Add(ImportHarness.Field(11, 14, "Flag", "Boolean"));
            }
        });

        await h.RunAsync();

        h.Counters.Inserted.Should().Be(1);
        var pen = h.Store.Inserted.Single();
        pen[12].Should().Be(new DateTime(2026, 3, 9));
        pen[13].Should().Be(true);
        pen[14].Should().Be(true);
        h.Issues.Single().SourceRowRef.Should().Be(3);
        h.Issues.Single().Message.Should().Contain("maybe");
    }

    [Fact]
    public async Task Cancelling_stops_a_file_run_after_the_chunk_it_is_on()
    {
        var sb = new StringBuilder("Name,Qty,Note\n");
        for (var i = 1; i <= 5000; i++) sb.Append("n").Append(i).Append(",1,x\n");
        var h = Harness(sb.ToString(), null, o => o.CancelAtAdvance = 1);

        await h.RunAsync();

        h.Completion!.Value.Status.Should().Be(ImportRunStatus.Cancelled);
        h.Counters.Read.Should().BeLessThan(5000);
    }
}
