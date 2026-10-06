using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using PowerBase.Application.Imports.Files;

namespace PowerBase.Imports.Tests;

public class ImportCsvReaderTests
{
    private static List<(long Row, string?[] Cells)> ReadAll(string csv, char delimiter = ',', Encoding? encoding = null)
    {
        var enc = encoding ?? new UTF8Encoding(false);
        using var stream = new MemoryStream(enc.GetPreamble().Concat(enc.GetBytes(csv)).ToArray());
        using var reader = new ImportCsvReader(stream, delimiter);
        var rows = new List<(long, string?[])>();
        while (reader.Read(out var n, out var cells)) rows.Add((n, cells));
        return rows;
    }

    [Fact]
    public void Plain_rows_split_on_the_delimiter_and_number_from_one()
    {
        var rows = ReadAll("a,b,c\n1,2,3\n");
        rows.Should().HaveCount(2);
        rows[0].Cells.Should().Equal("a", "b", "c");
        rows[1].Row.Should().Be(2);
    }

    [Fact]
    public void Quoted_values_keep_delimiters_quotes_and_line_breaks()
    {
        var rows = ReadAll("name,note\n\"Smith, Jo\",\"said \"\"hi\"\"\nthen left\"\n");
        rows.Should().HaveCount(2);
        rows[1].Cells.Should().Equal("Smith, Jo", "said \"hi\"\nthen left");
    }

    [Fact]
    public void Windows_mac_and_unix_line_endings_all_work_and_the_last_line_needs_no_ending()
    {
        ReadAll("a\r\nb\rc\nd").Select(r => r.Cells[0]).Should().Equal("a", "b", "c", "d");
    }

    [Fact]
    public void Empty_and_spaces_only_cells_are_blank_but_other_spacing_is_kept()
    {
        var cells = ReadAll("x,,   ,\" \", y ").Single().Cells;
        cells.Should().Equal("x", null, null, null, " y ");
    }

    [Fact]
    public void A_byte_order_mark_is_not_part_of_the_first_name()
        => ReadAll("﻿Name,Qty\n").First().Cells[0].Should().Be("Name");

    [Fact]
    public void Utf16_with_a_mark_is_read_as_utf16()
        => ReadAll("Name;Qty\n", ';', new UnicodeEncoding(false, true)).First().Cells.Should().Equal("Name", "Qty");

    [Fact]
    public void A_stray_quote_inside_an_unquoted_value_is_kept()
        => ReadAll("5\" pipe,ok").Single().Cells.Should().Equal("5\" pipe", "ok");

    [Fact]
    public void A_quote_that_never_closes_is_reported_with_its_row_not_swallowed()
    {
        var act = () => ReadAll("a,b\n1,\"oops\n2,3\n");
        act.Should().Throw<ImportFileFormatException>().WithMessage("*Row 2*quoted value*");
    }

    [Fact]
    public void A_runaway_value_is_stopped()
    {
        var act = () => ReadAll("\"" + new string('x', ImportFileFormats.MaxCellLength + 10));
        act.Should().Throw<ImportFileFormatException>();
    }

    [Fact]
    public void An_empty_file_has_no_rows() => ReadAll("").Should().BeEmpty();

    [Fact]
    public void A_blank_line_is_a_row_with_one_blank_cell()
        => ReadAll("a\n\nb\n").Select(r => r.Cells.Length == 1 && r.Cells[0] is null).Should().Equal(false, true, false);

    [Theory]
    [InlineData("a,b,c\n1,2,3\n4,5,6\n", ',')]
    [InlineData("a;b;c\n1;2;3\n4;5;6\n", ';')]
    [InlineData("a\tb\tc\n1\t2\t3\n", '\t')]
    [InlineData("a|b|c\n1|2|3\n", '|')]
    [InlineData("\"a,x\";b;c\n\"1,2\";2;3\n", ';')]   // commas inside quotes do not count
    [InlineData("single\nvalues\n", ',')]            // nothing to detect: comma
    public void The_separator_is_detected(string csv, char expected)
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(csv));
        ImportCsvReader.DetectDelimiter(stream).Should().Be(expected);
        stream.Position.Should().Be(0, "detecting must not consume the stream");
    }

    [Fact]
    public void A_large_file_streams_every_row()
    {
        var sb = new StringBuilder("id,name\n");
        for (var i = 1; i <= 200_000; i++) sb.Append(i).Append(",name ").Append(i).Append('\n');
        var rows = ReadAll(sb.ToString());
        rows.Should().HaveCount(200_001);
        rows[^1].Cells[0].Should().Be("200000");
    }
}

public class ImportXlsxReaderTests
{
    private static MemoryStream Workbook(Action<IXLWorksheet> fill, string sheet = "Data", Action<XLWorkbook>? more = null)
    {
        using var wb = new XLWorkbook();
        fill(wb.AddWorksheet(sheet));
        more?.Invoke(wb);
        var ms = new MemoryStream();
        wb.SaveAs(ms);
        ms.Position = 0;
        return ms;
    }

    private static List<(long Row, string?[] Cells)> ReadAll(Stream stream, string? sheet = null)
    {
        using var reader = ImportXlsxReader.Open(stream, sheet);
        var rows = new List<(long, string?[])>();
        while (reader.Read(out var n, out var cells)) rows.Add((n, cells));
        return rows;
    }

    [Fact]
    public void Text_numbers_and_checkboxes_come_back_as_plain_text()
    {
        using var stream = Workbook(ws =>
        {
            ws.Cell("A1").Value = "Name"; ws.Cell("B1").Value = "Qty"; ws.Cell("C1").Value = "Active";
            ws.Cell("A2").Value = "Pen"; ws.Cell("B2").Value = 12.5; ws.Cell("C2").Value = true;
            ws.Cell("A3").Value = "Pad"; ws.Cell("B3").Value = 1000000; ws.Cell("C3").Value = false;
        });

        var rows = ReadAll(stream);

        rows[0].Cells.Should().Equal("Name", "Qty", "Active");
        rows[1].Cells.Should().Equal("Pen", "12.5", "true");
        rows[2].Cells.Should().Equal("Pad", "1000000", "false");
    }

    [Fact]
    public void Dates_and_times_are_iso_text_and_other_numbers_are_not_mistaken_for_dates()
    {
        using var stream = Workbook(ws =>
        {
            ws.Cell("A1").Value = new DateTime(2026, 3, 9);
            ws.Cell("B1").Value = new DateTime(2026, 3, 9, 14, 30, 5);
            ws.Cell("C1").Value = 45000;                       // General: a number, even though it could be a date
            ws.Cell("D1").Value = 45000; ws.Cell("D1").Style.NumberFormat.Format = "d-mmm-yy";
            ws.Cell("E1").Value = 1234.5; ws.Cell("E1").Style.NumberFormat.Format = "#,##0.00 \"days\"";   // 'd' inside quotes
        });

        ReadAll(stream).Single().Cells.Should().Equal("2026-03-09", "2026-03-09T14:30:05", "45000", "2023-03-15", "1234.5");
    }

    [Fact]
    public void Gaps_in_a_row_and_in_the_rows_keep_their_places_and_row_numbers()
    {
        using var stream = Workbook(ws =>
        {
            ws.Cell("A1").Value = "first"; ws.Cell("D1").Value = "fourth";
            ws.Cell("B5").Value = "row five";
        });

        var rows = ReadAll(stream);

        rows.Select(r => r.Row).Should().Equal(1, 5);
        rows[0].Cells.Should().Equal("first", null, null, "fourth");
        rows[1].Cells.Should().Equal(null, "row five");
    }

    [Fact]
    public void Numbers_are_written_without_exponents_or_trailing_zeros()
    {
        using var stream = Workbook(ws =>
        {
            ws.Cell("A1").Value = 0.00001; ws.Cell("B1").Value = 1234.50; ws.Cell("C1").Value = 1E+20; ws.Cell("D1").Value = -7;
        });

        ReadAll(stream).Single().Cells.Should().Equal("0.00001", "1234.5", "100000000000000000000", "-7");
    }

    [Fact]
    public void A_formula_gives_its_calculated_value()
    {
        using var stream = Workbook(ws => { ws.Cell("A1").Value = 4; ws.Cell("B1").FormulaA1 = "A1*2"; });

        // ClosedXML writes formulas without a cached value until Excel recalculates; the reader must not crash on that.
        var cells = ReadAll(stream).Single().Cells;
        cells[0].Should().Be("4");
    }

    [Fact]
    public void A_chosen_sheet_is_read_and_an_unknown_one_is_refused()
    {
        using var stream = Workbook(ws => ws.Cell("A1").Value = "on first", "One", wb => wb.AddWorksheet("Two").Cell("A1").Value = "on second");

        ReadAll(stream, "Two").Single().Cells.Should().Equal("on second");
        stream.Position = 0;
        ReadAll(stream, "two").Single().Cells.Should().Equal("on second");
        stream.Position = 0;
        ReadAll(stream).Single().Cells.Should().Equal("on first");
        stream.Position = 0;
        var act = () => ReadAll(stream, "Three");
        act.Should().Throw<ImportFileFormatException>().WithMessage("*'Three'*");
    }

    [Fact]
    public void Sheet_names_are_listed_in_workbook_order()
    {
        using var stream = Workbook(_ => { }, "Customers", wb => wb.AddWorksheet("Orders"));

        ImportXlsxReader.SheetNames(stream).Should().Equal("Customers", "Orders");
    }

    [Fact]
    public void Repeated_text_uses_the_shared_table_and_still_reads_right()
    {
        using var stream = Workbook(ws => { for (var i = 1; i <= 50; i++) ws.Cell(i, 1).Value = i % 2 == 0 ? "even" : "odd"; });

        var values = ReadAll(stream).Select(r => r.Cells[0]).ToList();
        values.Count(v => v == "even").Should().Be(25);
        values[0].Should().Be("odd");
    }

    [Fact]
    public void A_file_that_is_not_a_workbook_is_refused_with_advice()
    {
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes("PK\u0003\u0004 not really"));

        var act = () => ReadAll(stream);

        act.Should().Throw<ImportFileFormatException>().WithMessage("*.xlsx*");
    }

    [Fact]
    public void A_large_sheet_streams_every_row()
    {
        using var stream = Workbook(ws =>
        {
            for (var r = 1; r <= 20_000; r++) { ws.Cell(r, 1).Value = r; ws.Cell(r, 2).Value = "name " + r; }
        });

        var rows = ReadAll(stream);

        rows.Should().HaveCount(20_000);
        rows[^1].Cells.Should().Equal("20000", "name 20000");
    }

    [Theory]
    [InlineData("A", 0)]
    [InlineData("Z", 25)]
    [InlineData("AA", 26)]
    [InlineData("AB12", 27)]
    [InlineData("XFD1", 16383)]
    public void Cell_addresses_give_column_places(string address, int expected) => ImportXlsxReader.ColumnIndex(address).Should().Be(expected);
}

public class ImportFileInspectorTests
{
    private static MemoryStream Csv(string text) => new(Encoding.UTF8.GetBytes(text));

    [Fact]
    public void A_preview_gives_named_columns_the_first_rows_and_the_row_count()
    {
        var csv = "Name,Qty\nPen,1\nPad,2\n\nInk,3\n";
        using var stream = Csv(csv);

        var preview = ImportFileInspector.Preview(stream, new ImportFileLayout("csv", null, 1, null, null));

        preview.Columns.Select(c => c.Name).Should().Equal("Name", "Qty");
        preview.Columns.Select(c => c.Fid).Should().Equal(1001, 1002);
        preview.DataRows.Should().Be(3, "the blank line is not a row");
        preview.LastRow.Should().Be(5);
        preview.Rows.Should().HaveCount(3);
        preview.Delimiter.Should().Be(",");
    }

    [Fact]
    public void Blank_and_repeated_names_get_places_of_their_own_and_the_first_repeat_wins_by_name()
    {
        var columns = ImportFileColumns.FromHeader(["Name", "", "name", "Name", "Qty"], 5);

        columns.Select(c => c.UniqueName).Should().Equal("Name", "Column 2", "name (2)", "Name (3)", "Qty");
        columns.Select(c => c.Duplicate).Should().Equal(false, false, true, true, false);
    }

    [Fact]
    public void A_file_without_a_header_names_the_columns_by_place()
    {
        using var stream = Csv("Pen,1\nPad,2\n");

        var preview = ImportFileInspector.Preview(stream, new ImportFileLayout("csv", null, 0, null, null));

        preview.Columns.Select(c => c.Name).Should().Equal("Column 1", "Column 2");
        preview.DataRows.Should().Be(2);
    }

    [Fact]
    public void A_header_further_down_and_data_starting_later_are_honoured()
    {
        using var stream = Csv("Report title\n\nName,Qty\nunits,row\nPen,1\nPad,2\n");

        var preview = ImportFileInspector.Preview(stream, new ImportFileLayout("csv", null, 3, 5, null));

        preview.Columns.Select(c => c.Name).Should().Equal("Name", "Qty");
        preview.Rows.Select(r => r[0]).Should().Equal("Pen", "Pad");
        preview.LastRow.Should().Be(6);
    }

    [Fact]
    public void A_header_row_that_is_not_in_the_file_is_reported()
    {
        using var stream = Csv("a,b\n");

        var act = () => ImportFileInspector.Preview(stream, new ImportFileLayout("csv", null, 9, null, null));

        act.Should().Throw<ImportFileFormatException>().WithMessage("*Row 9*");
    }

    [Fact]
    public void A_data_row_wider_than_the_header_adds_a_column()
    {
        using var stream = Csv("a,b\n1,2,3\n");

        ImportFileInspector.Preview(stream, new ImportFileLayout("csv", null, 1, null, null)).Columns.Select(c => c.Name)
            .Should().Equal("a", "b", "Column 3");
    }

    [Fact]
    public void Columns_are_read_the_same_way_at_run_time()
    {
        using var stream = Csv("Name,Qty\nPen,1\n");

        ImportFileInspector.ReadColumns(stream, new ImportFileLayout("csv", null, 1, null, ',')).Select(c => c.Name).Should().Equal("Name", "Qty");
    }

    [Fact]
    public void The_fields_the_engine_reads_are_text_plus_a_row_number()
    {
        var fields = ImportFileColumns.ToFields(ImportFileColumns.FromHeader(["Name", "Qty"], 2));

        fields.Select(f => (f.Fid, f.TypeCode)).Should().Equal((3, "Number"), (1001, "Text"), (1002, "Text"));
        fields[0].PhysicalColumnName.Should().Be("Id");
    }

    [Theory]
    [InlineData("data.xlsx", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, "xlsx")]
    [InlineData("data.csv", new byte[] { 0x61, 0x2C }, "csv")]
    [InlineData("data.TXT", new byte[] { 0x61, 0x2C }, "csv")]
    [InlineData("data.xls", new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }, null)]
    [InlineData("data.xlsx", new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }, null)]      // an old .xls renamed
    [InlineData("data.csv", new byte[] { 0x50, 0x4B, 0x03, 0x04 }, null)]       // a zip renamed
    [InlineData("data.pdf", new byte[] { 0x25, 0x50, 0x44, 0x46 }, null)]
    public void The_format_comes_from_the_contents_and_the_name_together(string name, byte[] head, string? expected)
        => ImportFileFormats.Detect(name, head).Should().Be(expected);
}
