using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>Which columns of a file's sheets a destination table is offered: the heading names one of its fields, and every filled cell of the
/// sample fits that field's type.</summary>
public class ImportColumnFitTests
{
    private static readonly Guid TableId = Guid.NewGuid();

    private readonly IImportFileRepository _files = Substitute.For<IImportFileRepository>();
    private readonly IImportFileAccess _fileAccess = Substitute.For<IImportFileAccess>();
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();
    private readonly IAppAccessService _access = Substitute.For<IAppAccessService>();
    private readonly IQueryContext _user = Substitute.For<IQueryContext>();

    public ImportColumnFitTests()
    {
        _user.UserId.Returns(5L);
        _tables.GetByPublicIdAsync(TableId, Arg.Any<CancellationToken>()).Returns(new AppTable { Id = 11, AppId = 1, PublicId = TableId, Name = "Employee" });
        Fields(F(6, "Employee ID", "Text"), F(7, "First Name", "Text"), F(8, "Last Name", "Text"), F(9, "Age", "Number"),
            F(10, "Hired", "Date"), F(11, "Active", "Boolean"));
    }

    private static AppField F(int fid, string name, string type, string? settings = null, bool system = false) => new()
    {
        Id = 1100 + fid, AppTableId = 11, Fid = fid, Name = name, Label = name, TypeCode = type, Settings = settings, IsSystem = system
    };

    private void Fields(params AppField[] fields) => _fields.ListByTableAsync(11, Arg.Any<CancellationToken>()).Returns(fields.ToList());

    private ColumnFitHandler Sut() => new(_files, _fileAccess, _tables, _fields, _access, _user);

    private ImportFile Stored(string format, byte[] bytes, long owner = 5)
    {
        var file = new ImportFile { PublicId = Guid.NewGuid(), UploadedByUserId = owner, Format = format, StoragePath = "/files/x", FileName = "x." + format };
        _files.GetByPublicIdAsync(file.PublicId, Arg.Any<CancellationToken>()).Returns(file);
        _fileAccess.OpenAsync("/files/x", Arg.Any<CancellationToken>()).Returns(_ => Task.FromResult<Stream>(new MemoryStream(bytes)));
        return file;
    }

    private ImportFile Csv(string text) => Stored("csv", Encoding.UTF8.GetBytes(text));

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

    private async Task<IReadOnlyList<ImportColumnFitColumn>> Fit(ImportFile file) =>
        (await Sut().HandleAsync(file.PublicId, new ImportColumnFitRequest(TableId, null), default)).Sheets.Single().Columns;

    private static ImportColumnFitColumn Of(IReadOnlyList<ImportColumnFitColumn> columns, string name) => columns.Single(c => c.Name == name);

    // ---- the heading ----

    [Fact]
    public async Task A_heading_that_names_a_field_is_matched_whatever_its_case_spacing_or_punctuation()
    {
        var columns = await Fit(Csv("EMPLOYEE_ID,first-name,Last  Name\nE1,Ann,Lee\n"));

        columns.Select(c => (c.FieldFid, c.Fits)).Should().Equal((6, true), (7, true), (8, true));
        columns[0].FieldName.Should().Be("Employee ID");
    }

    [Fact]
    public async Task A_heading_no_field_has_is_not_offered_and_says_why()
    {
        var columns = await Fit(Csv("Employee ID,Favourite Colour\nE1,Blue\n"));

        var colour = Of(columns, "Favourite Colour");
        (colour.Fits, colour.Reason, colour.FieldFid).Should().Be((false, "noField", (int?)null));
        Of(columns, "Employee ID").Fits.Should().BeTrue();
    }

    [Fact]
    public async Task The_record_id_system_fields_and_formulas_are_never_offered()
    {
        Fields(F(3, "Record ID#", "Number", system: true), F(4, "Created by", "User", system: true), F(5, "Total", "Formula_Number"), F(6, "Name", "Text"));

        var columns = await Fit(Csv("Record ID#,Created by,Total,Name\n1,x,3,Ann\n"));

        columns.Select(c => c.Fits).Should().Equal(false, false, false, true);
        columns.Take(3).Should().OnlyContain(c => c.Reason == "noField");
    }

    [Fact]
    public async Task Of_several_fields_that_read_alike_the_one_spelled_like_the_heading_wins()
    {
        Fields(F(6, "Employee-ID", "Text"), F(7, "Employee ID", "Text"));

        var columns = await Fit(Csv("Employee ID\nE1\n"));

        columns.Single().FieldFid.Should().Be(7);
    }

    // ---- the data ----

    [Fact]
    public async Task Numbers_fit_a_number_field_and_text_does_not()
    {
        (await Fit(Csv("Age\n31\n44\n"))).Single().Fits.Should().BeTrue();

        var bad = (await Fit(Csv("Age\n31\nforty\n"))).Single();

        (bad.Fits, bad.Reason, bad.Failed, bad.Sampled, bad.Example).Should().Be((false, "type", 1, 2, "forty"));
        bad.FieldFid.Should().Be(9, "it is still named as the field it nearly matched");
    }

    [Fact]
    public async Task Dates_and_checkboxes_are_tried_with_the_conversions_a_run_uses()
    {
        var good = await Fit(Csv("Hired,Active\n2024-03-09,true\n2023-01-01,no\n"));
        good.Should().OnlyContain(c => c.Fits);

        var bad = await Fit(Csv("Hired,Active\nlast week,maybe\n"));
        bad.Should().OnlyContain(c => !c.Fits && c.Reason == "type");
    }

    [Fact]
    public async Task A_text_field_takes_anything()
    {
        (await Fit(Csv("First Name\nAnn\n123\n2024-01-01\n!!\n"))).Single().Fits.Should().BeTrue();
    }

    [Fact]
    public async Task A_fields_own_format_settings_count_as_part_of_its_type()
    {
        Fields(F(6, "Code", "Text", "{\"validation\":{\"maxLength\":3}}"));

        var columns = await Fit(Csv("Code\nabc\nabcdef\n"));

        columns.Single().Should().Match<ImportColumnFitColumn>(c => !c.Fits && c.Reason == "type" && c.Example == "abcdef");
    }

    [Fact]
    public async Task Blank_cells_prove_nothing_so_they_are_ignored_and_an_empty_column_fits()
    {
        var columns = await Fit(Csv("Age,Hired\n31,\n,\n  ,\n"));

        columns.Should().OnlyContain(c => c.Fits);
        Of(columns, "Age").Sampled.Should().Be(1);
        Of(columns, "Hired").Sampled.Should().Be(0);
    }

    [Fact]
    public async Task One_bad_cell_in_the_sample_hides_the_column()
    {
        var rows = string.Join("\n", Enumerable.Range(1, 50).Select(i => i == 25 ? "n/a" : i.ToString()));

        (await Fit(Csv("Age\n" + rows + "\n"))).Single().Fits.Should().BeFalse();
    }

    [Fact]
    public async Task Only_the_first_two_hundred_rows_are_tried()
    {
        var rows = string.Join("\n", Enumerable.Range(1, 400).Select(i => i == 300 ? "n/a" : i.ToString()));

        var column = (await Fit(Csv("Age\n" + rows + "\n"))).Single();

        column.Fits.Should().BeTrue("the bad cell is below the sample; the run will report it");
        column.Sampled.Should().Be(ImportColumnFit.SampleRows);
    }

    [Fact]
    public async Task A_row_shorter_than_the_header_counts_its_missing_cells_as_blank()
    {
        (await Fit(Csv("Employee ID,Age\nE1\nE2,40\n"))).Should().OnlyContain(c => c.Fits);
    }

    // ---- sheets ----

    [Fact]
    public async Task Each_sheet_of_a_workbook_is_judged_on_its_own_columns_in_one_call()
    {
        var book = Workbook(
            ("Employees", ["Employee ID", "First Name", "Last Name"], [["E1", "Ann", "Lee"]]),
            ("Training", ["Course", "Hours"], [["Safety", "3"]]),
            ("Employee-Training", ["Employee ID", "Course", "Age"], [["E1", "Safety", "40"]]));
        var file = Stored("xlsx", book);

        var response = await Sut().HandleAsync(file.PublicId, new ImportColumnFitRequest(TableId,
            [new ImportSheetRequest("Employees"), new ImportSheetRequest("Training"), new ImportSheetRequest("Employee-Training")]), default);

        response.Sheets.Select(s => s.Sheet).Should().Equal("Employees", "Training", "Employee-Training");
        response.Sheets[0].Columns.Should().OnlyContain(c => c.Fits);
        response.Sheets[1].Columns.Should().OnlyContain(c => !c.Fits && c.Reason == "noField", "no training column is a field of the Employee table");
        response.Sheets[2].Columns.Where(c => c.Fits).Select(c => c.Name).Should().Equal("Employee ID", "Age");
    }

    [Fact]
    public async Task A_sheet_can_have_its_own_header_row_and_first_data_row()
    {
        var book = Workbook(("Data", ["Title line", "x"], [["Employee ID", "Age"], ["E1", "31"]]));
        var file = Stored("xlsx", book);

        var response = await Sut().HandleAsync(file.PublicId, new ImportColumnFitRequest(TableId, [new ImportSheetRequest("Data", HeaderRow: 2)]), default);

        response.Sheets.Single().Columns.Select(c => (c.Name, c.Fits)).Should().Equal(("Employee ID", true), ("Age", true));
    }

    [Fact]
    public async Task A_sheet_that_does_not_exist_is_refused_by_name()
    {
        var file = Stored("xlsx", Workbook(("One", ["Age"], [["1"]])));

        var act = () => Sut().HandleAsync(file.PublicId, new ImportColumnFitRequest(TableId, [new ImportSheetRequest("Nope")]), default);

        await act.Should().ThrowAsync<ValidationException>().WithMessage("*'Nope'*");
    }

    [Fact]
    public async Task A_sheet_without_its_row_of_column_names_is_reported_without_failing_the_others()
    {
        var file = Stored("xlsx", Workbook(("Empty", [], []), ("Good", ["Age"], [["1"]])));

        var response = await Sut().HandleAsync(file.PublicId, new ImportColumnFitRequest(TableId, [new ImportSheetRequest("Empty"), new ImportSheetRequest("Good")]), default);

        response.Sheets[0].Error.Should().NotBeNull();
        response.Sheets[1].Columns.Single().Fits.Should().BeTrue();
    }

    [Fact]
    public async Task Too_many_sheets_in_one_call_are_refused()
    {
        var file = Csv("Age\n1\n");
        var sheets = Enumerable.Range(0, ColumnFitHandler.MaxSheets + 1).Select(_ => new ImportSheetRequest(null)).ToList();

        var act = () => Sut().HandleAsync(file.PublicId, new ImportColumnFitRequest(TableId, sheets), default);

        await act.Should().ThrowAsync<ValidationException>();
    }

    // ---- who may ask ----

    [Fact]
    public async Task Someone_elses_file_does_not_exist_for_you()
    {
        var file = Stored("csv", Encoding.UTF8.GetBytes("Age\n1\n"), owner: 99);

        var act = () => Sut().HandleAsync(file.PublicId, new ImportColumnFitRequest(TableId, null), default);

        await act.Should().ThrowAsync<NotFoundException>();
        await _fields.DidNotReceiveWithAnyArgs().ListByTableAsync(default, default);
    }

    [Fact]
    public async Task Without_the_right_to_add_records_to_the_table_there_is_no_answer()
    {
        var file = Csv("Age\n1\n");
        _access.RequirePermissionByTablePublicIdAsync(TableId, PermissionCodes.RecordsCreate, Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new UnauthorizedActionException("add records to the table"));

        var act = () => Sut().HandleAsync(file.PublicId, new ImportColumnFitRequest(TableId, null), default);

        await act.Should().ThrowAsync<UnauthorizedActionException>();
    }

    // ---- the pieces ----

    [Theory]
    [InlineData("Employee ID", "employeeid")]
    [InlineData("  employee_id ", "employeeid")]
    [InlineData("E-mail", "email")]
    [InlineData("Größe (cm)", "größecm")]
    [InlineData("", "")]
    [InlineData(null, "")]
    [InlineData("___", "")]
    public void Names_are_compared_without_case_spaces_underscores_or_punctuation(string? text, string expected) => ImportColumnFit.Normalize(text).Should().Be(expected);

    [Fact]
    public void A_heading_made_only_of_punctuation_matches_nothing()
    {
        ImportColumnFit.FieldFor("---", [F(6, "Name", "Text")]).Should().BeNull();
    }
}
