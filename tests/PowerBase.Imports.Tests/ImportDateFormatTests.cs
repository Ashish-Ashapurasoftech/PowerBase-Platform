using System.Text;
using ClosedXML.Excel;
using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Application.Imports.Files;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Imports.Tests;

/// <summary>A date or a time in a file is read in the format the person chose for that column, never guessed: 02/10/2026 is 2 October or 10 February, and only they know.</summary>
public class ImportDateFormatTests
{
    private static DateTime? Date(string text, string format, string? time = null, bool withTime = false) =>
        ImportDateFormats.TryParseDate(text, format, time, withTime, out var value, out _) ? value : null;

    private static string? Time(string text, string format) => ImportDateFormats.TryParseTime(text, format, out var value, out _) ? value : null;

    // ---- dates ----

    [Fact]
    public void The_same_text_is_a_different_day_in_a_different_format()
    {
        Date("02/10/2026", "MM-DD-YYYY").Should().Be(new DateTime(2026, 2, 10));
        Date("02/10/2026", "DD-MM-YYYY").Should().Be(new DateTime(2026, 10, 2));
        Date("12/12/2026", "MM-DD-YYYY").Should().Be(new DateTime(2026, 12, 12));
        Date("2026-10-02", "YYYY-MM-DD").Should().Be(new DateTime(2026, 10, 2));
    }

    [Theory]
    [InlineData("02/10/2026")]
    [InlineData("02-10-2026")]
    [InlineData("02.10.2026")]
    [InlineData("2/10/2026")]
    public void Any_of_the_usual_separators_is_read_the_same(string text) => Date(text, "MM-DD-YYYY").Should().Be(new DateTime(2026, 2, 10));

    [Fact]
    public void A_two_digit_year_is_this_century_and_the_year_must_have_the_digits_the_format_says()
    {
        Date("02/10/26", "MM-DD-YY").Should().Be(new DateTime(2026, 2, 10));
        Date("02/10/26", "MM-DD-YYYY").Should().BeNull("the format says four digits");
        Date("02/10/2026", "MM-DD-YY").Should().BeNull("the format says two digits");
    }

    [Theory]
    [InlineData("25/12/2026", "MM-DD-YYYY")]   // there is no month 25
    [InlineData("12/25/2026", "DD-MM-YYYY")]
    [InlineData("31/02/2026", "DD-MM-YYYY")]
    [InlineData("not a date", "MM-DD-YYYY")]
    [InlineData("2026", "MM-DD-YYYY")]
    public void A_text_that_is_not_a_date_in_the_chosen_format_is_refused_not_read_the_other_way_round(string text, string format) =>
        Date(text, format).Should().BeNull();

    [Fact]
    public void A_real_Excel_date_cell_arrives_in_a_machine_form_and_is_read_whatever_the_format()
    {
        Date("2026-07-30", "DD-MM-YYYY").Should().Be(new DateTime(2026, 7, 30));
        Date("2026-07-30T00:00:00", "MM-DD-YYYY").Should().Be(new DateTime(2026, 7, 30));
        Date("2026-07-30T16:00:00", "MM-DD-YYYY", "HH:mm", withTime: true).Should().Be(new DateTime(2026, 7, 30, 16, 0, 0, DateTimeKind.Utc));
    }

    [Fact]
    public void A_month_written_as_a_word_cannot_be_mistaken_and_is_read()
    {
        Date("5-Apr-2026", "DD-MM-YYYY").Should().Be(new DateTime(2026, 4, 5));
        Date("April 5, 2026", "MM-DD-YYYY").Should().Be(new DateTime(2026, 4, 5));
    }

    // ---- date and time ----

    [Fact]
    public void A_date_and_time_cell_reads_its_time_in_the_chosen_time_format()
    {
        Date("02/10/2026 4:30 PM", "DD-MM-YYYY", "hh:mm AM/PM", withTime: true).Should().Be(new DateTime(2026, 10, 2, 16, 30, 0, DateTimeKind.Utc));
        Date("02/10/2026 16:30", "DD-MM-YYYY", "HH:mm", withTime: true).Should().Be(new DateTime(2026, 10, 2, 16, 30, 0, DateTimeKind.Utc));
        Date("02/10/2026", "DD-MM-YYYY", "HH:mm", withTime: true).Should().Be(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc), "a cell with no time is midnight");
        Date("02/10/2026 4:30 PM", "DD-MM-YYYY", "HH:mm", withTime: true).Should().Be(new DateTime(2026, 10, 2, 16, 30, 0, DateTimeKind.Utc), "an AM or PM written on the cell is never ambiguous");
    }

    [Fact]
    public void A_date_field_ignores_a_time_in_the_cell()
    {
        Date("02/10/2026 4:30 PM", "DD-MM-YYYY", null, withTime: false).Should().Be(new DateTime(2026, 10, 2));
    }

    // ---- times ----

    [Theory]
    [InlineData("9:00 AM", "09:00")]
    [InlineData("9:00 am", "09:00")]
    [InlineData("12:00 AM", "00:00")]
    [InlineData("12:30 PM", "12:30")]
    [InlineData("4:05 PM", "16:05")]
    [InlineData("11:59 PM", "23:59")]
    public void A_12_hour_time_needs_its_AM_or_PM(string text, string stored) => Time(text, "hh:mm AM/PM").Should().Be(stored);

    [Theory]
    [InlineData("9:00")]            // no AM/PM: 9 in the morning or the evening?
    [InlineData("14:00 AM")]        // a contradiction
    [InlineData("0:30 PM")]
    [InlineData("25:00 PM")]
    [InlineData("9:61 AM")]
    public void A_12_hour_time_without_AM_or_PM_or_with_an_impossible_hour_is_refused(string text) => Time(text, "hh:mm AM/PM").Should().BeNull();

    [Theory]
    [InlineData("09:00", "09:00")]
    [InlineData("9:05", "09:05")]
    [InlineData("23:59", "23:59")]
    [InlineData("16:30:15", "16:30:15")]
    public void A_24_hour_time_is_read_as_written(string text, string stored) => Time(text, "HH:mm").Should().Be(stored);

    [Theory]
    [InlineData("24:00")]
    [InlineData("14:00 AM")]
    public void A_24_hour_column_refuses_hours_past_23_and_a_contradiction(string text) => Time(text, "HH:mm").Should().BeNull();

    [Fact]
    public void A_format_with_seconds_stores_seconds_and_one_without_keeps_them_only_when_the_cell_has_them()
    {
        Time("4:00 PM", "hh:mm:ss AM/PM").Should().Be("16:00:00");
        Time("4:00:30 PM", "hh:mm AM/PM").Should().Be("16:00:30");
        Time("16:00", "HH:mm:ss").Should().Be("16:00:00");
    }

    [Theory]
    [InlineData("14:40", "14:40")]
    [InlineData("23:05", "23:05")]
    [InlineData("0:15", "00:15")]
    public void An_hour_that_can_only_be_24_hour_needs_no_AM_or_PM_even_in_a_12_hour_column(string text, string stored) => Time(text, "hh:mm AM/PM").Should().Be(stored);

    [Fact]
    public void An_Excel_time_cell_arrives_with_seconds_and_no_AM_or_PM_and_is_read_in_a_12_hour_column_too()
    {
        Time("09:00:00", "hh:mm AM/PM").Should().Be("09:00");
        Time("16:00:00", "hh:mm AM/PM").Should().Be("16:00");
        Time("2026-07-30T16:45:00", "hh:mm AM/PM").Should().Be("16:45");
    }

    [Theory]
    [InlineData("9:05 PM", "21:05")]
    [InlineData("21:05 PM", "21:05")]
    [InlineData("12:30 AM", "00:30")]
    [InlineData("00:30 AM", "00:30")]
    public void An_AM_or_PM_that_agrees_with_the_hour_is_honoured_in_either_kind_of_column_even_the_time_you_asked_about(string text, string stored)
    {
        Time(text, "hh:mm AM/PM").Should().Be(stored);
        Time(text, "HH:mm").Should().Be(stored);
    }

    [Fact]
    public void The_time_the_business_asked_about_21_05_00_PM_is_simply_nine_oclock_in_the_evening()
    {
        Time("21:05:00 PM", "hh:mm:ss AM/PM").Should().Be("21:05:00");
        Time("21:05:00 PM", "HH:mm:ss").Should().Be("21:05:00");
    }

    // ---- the time zone of a date and time (filled in from the browser; none stores the reading as written) ----

    [Fact]
    public void A_date_and_time_in_a_zone_is_stored_as_the_moment_it_was_in_utc_so_it_shows_as_written()
    {
        var india = ImportDateFormats.ZoneOf("Asia/Calcutta");
        india.Should().NotBeNull();

        ImportDateFormats.TryParseDate("05/01/2024 07:15 AM", "DD-MM-YYYY", "hh:mm AM/PM", true, out var utc, out _, india).Should().BeTrue();
        utc.Should().Be(new DateTime(2024, 1, 5, 1, 45, 0, DateTimeKind.Utc));
        TimeZoneInfo.ConvertTimeFromUtc(utc, india!).Should().Be(new DateTime(2024, 1, 5, 7, 15, 0));

        ImportDateFormats.TryParseDate("05/01/2024 07:15 AM", "DD-MM-YYYY", "hh:mm AM/PM", true, out var none, out _).Should().BeTrue();
        none.Should().Be(new DateTime(2024, 1, 5, 7, 15, 0, DateTimeKind.Utc), "no zone: stored as written");
    }

    [Fact]
    public void A_late_evening_stays_on_its_own_day_and_a_month_word_date_is_in_the_zone_too()
    {
        var india = ImportDateFormats.ZoneOf("Asia/Calcutta");

        ImportDateFormats.TryParseDate("21/12/2005 9:05:00 PM", "DD-MM-YYYY", "hh:mm:ss AM/PM", true, out var evening, out _, india).Should().BeTrue();
        TimeZoneInfo.ConvertTimeFromUtc(evening, india!).Should().Be(new DateTime(2005, 12, 21, 21, 5, 0));
        ImportDateFormats.TryParseDate("9-Mar-2022 06:30:00", "DD-MM-YYYY", "HH:mm:ss", true, out var morning, out _, india).Should().BeTrue();
        TimeZoneInfo.ConvertTimeFromUtc(morning, india!).Should().Be(new DateTime(2022, 3, 9, 6, 30, 0));
        ImportDateFormats.TryParseDate("31 October 2026 12:00 PM", "DD-MM-YYYY", "hh:mm AM/PM", true, out var noon, out _, india).Should().BeTrue();
        TimeZoneInfo.ConvertTimeFromUtc(noon, india!).Should().Be(new DateTime(2026, 10, 31, 12, 0, 0));
    }

    [Fact]
    public void A_zone_that_does_not_exist_is_refused_but_none_is_allowed()
    {
        ImportDateFormats.ZoneOf("Nowhere/Land").Should().BeNull();
        ImportDateFormats.Problem("When", Field("DateTime"), Field("Text"), "DD-MM-YYYY", "HH:mm", "Nowhere/Land").Should().Contain("not a time zone");
        ImportDateFormats.Problem("When", Field("DateTime"), Field("Text"), "DD-MM-YYYY", "HH:mm", "Asia/Calcutta").Should().BeNull();
        ImportDateFormats.Problem("When", Field("DateTime"), Field("Text"), "DD-MM-YYYY", "HH:mm").Should().BeNull("an older mapping has none and is stored as written");
    }

    // ---- which columns need a format ----

    private static AppField Field(string type) => new() { Id = 1, AppTableId = 1, Fid = 6, Name = "X", TypeCode = type };

    [Fact]
    public void Only_text_going_to_a_date_or_a_time_needs_a_format()
    {
        ImportDateFormats.Problem("When", Field("Date"), Field("Text"), null, null).Should().Contain("date format");
        ImportDateFormats.Problem("When", Field("Time"), Field("Text"), null, null).Should().Contain("time format");
        ImportDateFormats.Problem("When", Field("DateTime"), Field("Text"), "DD-MM-YYYY", null).Should().Contain("time format");
        ImportDateFormats.Problem("When", Field("DateTime"), Field("Text"), "DD-MM-YYYY", "HH:mm").Should().BeNull();
        ImportDateFormats.Problem("When", Field("Date"), Field("DateTime"), null, null).Should().BeNull("a source that is already a date carries its own value");
        ImportDateFormats.Problem("Qty", Field("Number"), Field("Text"), null, null).Should().BeNull();
    }

    [Fact]
    public void A_format_that_is_not_in_the_lists_is_refused()
    {
        ImportDateFormats.Problem("When", Field("Date"), Field("Text"), "dd/MM/yyyy", null).Should().Contain("not a date format");
        ImportDateFormats.Problem("When", Field("Time"), Field("Text"), null, "H:m").Should().Contain("not a time format");
    }

    // ---- the whole path: a file, a mapping with a format ----

    private static ImportDefinitionConfig FileConfig(string? dateFormat, string? timeFormat)
    {
        return new ImportDefinitionConfig
        {
            Name = "f.csv", SourceKind = ImportSourceKinds.File,
            File = new ImportFileOptions { Format = "csv", HeaderRow = 1, ColumnBy = "name", Columns = ["Name", "Day", "Start"] },
            Mappings =
            [
                new() { DestFid = 6, SourceFid = 1001 },
                new() { DestFid = 12, SourceFid = 1002, DateFormat = dateFormat },
                new() { DestFid = 13, SourceFid = 1003, TimeFormat = timeFormat },
            ]
        };
    }

    private static ImportHarness Harness(ImportDefinitionConfig cfg, string csv) => ImportHarness.Create(cfg, new HarnessOptions
    {
        UploadedFile = Encoding.UTF8.GetBytes(csv),
        ConfigureDestination = d =>
        {
            d.Add(ImportHarness.Field(11, 12, "Day", "Date"));
            d.Add(ImportHarness.Field(11, 13, "Start", "Time"));
        }
    });

    [Fact]
    public async Task A_run_reads_the_dates_and_times_in_the_chosen_formats()
    {
        var h = Harness(FileConfig("DD-MM-YYYY", "hh:mm AM/PM"), "Name,Day,Start\nPen,02/10/2026,4:30 PM\nPad,25/12/2026,9:00 AM\n");

        await h.RunAsync();

        h.Counters.Inserted.Should().Be(2);
        var rows = h.Store.Inserted;
        rows[0][12].Should().Be(new DateTime(2026, 10, 2));
        rows[0][13].Should().Be("16:30");
        rows[1][12].Should().Be(new DateTime(2026, 12, 25));
        rows[1][13].Should().Be("09:00");
    }

    [Fact]
    public async Task The_same_file_in_another_format_gives_other_dates_and_reports_those_that_do_not_fit()
    {
        var h = Harness(FileConfig("MM-DD-YYYY", "HH:mm"), "Name,Day,Start\nPen,02/10/2026,16:30\nPad,25/12/2026,9:00\n");

        await h.RunAsync();

        h.Counters.Inserted.Should().Be(1);
        h.Store.Inserted.Single()[12].Should().Be(new DateTime(2026, 2, 10));
        h.Issues.Single().Message.Should().Contain("25/12/2026").And.Contain("MM-DD-YYYY");
    }

    [Fact]
    public async Task A_mapping_of_text_to_a_date_with_no_format_is_refused_when_saved_or_run()
    {
        var cfg = FileConfig(null, "HH:mm");
        var h = Harness(cfg, "Name,Day,Start\nPen,02/10/2026,16:30\n");

        var act = () => h.PlanBuilder.BuildAllAsync(cfg, ImportHarness.DestTableId, default, ImportFileValidation.SavedSource(cfg.File!));

        (await act.Should().ThrowAsync<ValidationException>()).WithMessage("*date format*Day*");
    }

    // ---- a formula over those columns sees them as the field gets them ----

    private static ImportDefinitionConfig FormulaConfig(string dateFormat, string timeFormat) => new()
    {
        Name = "f.csv", SourceKind = ImportSourceKinds.File,
        File = new ImportFileOptions { Format = "csv", HeaderRow = 1, ColumnBy = "name", Columns = ["Name", "Day", "Start"] },
        VirtualColumns = [new ImportVirtualColumn { Fid = ImportVirtual.FidBase, Name = "Label", Kind = ImportVirtual.Formula, Formula = "[Day] & \"/\" & [Start]" }],
        Mappings =
        [
            new() { DestFid = 6, SourceFid = ImportVirtual.FidBase },
            new() { DestFid = 12, SourceFid = 1002, DateFormat = dateFormat },
            new() { DestFid = 13, SourceFid = 1003, TimeFormat = timeFormat },
        ]
    };

    [Fact]
    public async Task A_formula_reads_a_date_and_a_time_column_in_the_formats_chosen_for_them()
    {
        // 02/10/2026 is 2 October under DD-MM-YYYY: the formula shows the same day the Date field gets, in the app's (month-first) form.
        var h = Harness(FormulaConfig("DD-MM-YYYY", "hh:mm AM/PM"), "Name,Day,Start\nPen,02/10/2026,4:30 PM\n");

        await h.RunAsync();

        var row = h.Store.Inserted.Single();
        row[12].Should().Be(new DateTime(2026, 10, 2));
        row[6].Should().Be("10-02-2026/4:30:00 PM");
    }

    [Fact]
    public async Task The_same_cells_in_another_format_give_the_other_day_in_the_formula_too()
    {
        var h = Harness(FormulaConfig("MM-DD-YYYY", "HH:mm"), "Name,Day,Start\nPen,02/10/2026,16:30\n");

        await h.RunAsync();

        var row = h.Store.Inserted.Single();
        row[12].Should().Be(new DateTime(2026, 2, 10));
        row[6].Should().Be("02-10-2026/4:30:00 PM");
    }

    // ---- a workbook whose date cells are real dates written in different ways on the sheet ----

    [Fact]
    public async Task A_real_date_cell_is_read_as_the_sheet_shows_it_in_the_format_the_person_chose()
    {
        using var wb = new XLWorkbook();
        var ws = wb.AddWorksheet("Data");
        ws.Cell("A1").Value = "Name"; ws.Cell("B1").Value = "Day"; ws.Cell("C1").Value = "Start"; ws.Cell("D1").Value = "Created";
        void Row(int r, string name, DateTime day, string dayFormat, DateTime time, string timeFormat, DateTime created, string createdFormat)
        {
            ws.Cell(r, 1).Value = name;
            ws.Cell(r, 2).Value = day; ws.Cell(r, 2).Style.DateFormat.Format = dayFormat;
            ws.Cell(r, 3).Value = time; ws.Cell(r, 3).Style.DateFormat.Format = timeFormat;
            ws.Cell(r, 4).Value = created; ws.Cell(r, 4).Style.DateFormat.Format = createdFormat;
        }
        var t = new DateTime(1900, 1, 1, 19, 15, 0);
        Row(2, "a", new DateTime(1990, 1, 5), "dd/mm/yyyy", t, "hh:mm AM/PM", new DateTime(2024, 1, 5, 7, 15, 0), "dd/mm/yyyy hh:mm AM/PM");
        Row(3, "b", new DateTime(1985, 12, 11), "mm-dd-yyyy", t, "hh:mm AM/PM", new DateTime(2023, 11, 12, 14, 40, 0), "mm-dd-yyyy hh:mm");   // 11-12 on the sheet
        Row(4, "c", new DateTime(2005, 12, 21), "m/d/yyyy", t, "hh:mm AM/PM", new DateTime(2005, 12, 21, 9, 5, 0), "m/d/yyyy hh:mm");        // 12/21/2005 on the sheet
        using var ms = new MemoryStream();
        wb.SaveAs(ms);

        var cfg = new ImportDefinitionConfig
        {
            Name = "f.xlsx", SourceKind = ImportSourceKinds.File,
            File = new ImportFileOptions { Format = "xlsx", HeaderRow = 1, ColumnBy = "name", Columns = ["Name", "Day", "Start", "Created"] },
            Mappings =
            [
                new() { DestFid = 6, SourceFid = 1001 },
                new() { DestFid = 12, SourceFid = 1002, DateFormat = "DD-MM-YYYY" },
                new() { DestFid = 13, SourceFid = 1003, TimeFormat = "hh:mm AM/PM" },
                new() { DestFid = 14, SourceFid = 1004, DateFormat = "DD-MM-YYYY", TimeFormat = "hh:mm AM/PM", TimeZone = "Asia/Calcutta" },
            ]
        };
        var h = ImportHarness.Create(cfg, new HarnessOptions
        {
            UploadedFile = ms.ToArray(),
            ConfigureDestination = d =>
            {
                d.Add(ImportHarness.Field(11, 12, "Day", "Date"));
                d.Add(ImportHarness.Field(11, 13, "Start", "Time"));
                d.Add(ImportHarness.Field(11, 14, "Created", "DateTime"));
            }
        });

        await h.RunAsync();

        // a: sheet shows 05/01/1990 and 05/01/2024 07:15 AM, read day first: 5 January. b: 12-11-1985 is the 12th of November. c: 12/21/2005 has no month 21.
        h.Counters.Inserted.Should().Be(2);
        var rows = h.Store.Inserted;
        rows[0][12].Should().Be(new DateTime(1990, 1, 5));
        rows[0][13].Should().Be("19:15");
        rows[0][14].Should().Be(new DateTime(2024, 1, 5, 1, 45, 0, DateTimeKind.Utc), "07:15 in India is 01:45 UTC, which the grid shows back as 07:15");
        rows[1][12].Should().Be(new DateTime(1985, 11, 12));
        rows[1][14].Should().Be(new DateTime(2023, 12, 11, 9, 10, 0, DateTimeKind.Utc), "11-12-2023 14:40 day first is 11 December, 14:40 India");
        h.Issues.Single().SourceRowRef.Should().Be(4);
        h.Issues.Single().Message.Should().Contain("12/21/2005").And.Contain("DD-MM-YYYY");
    }

    [Fact]
    public async Task A_run_started_from_a_browser_uses_its_zone_for_a_date_and_time_whose_mapping_names_none()
    {
        var cfg = new ImportDefinitionConfig
        {
            Name = "f.csv", SourceKind = ImportSourceKinds.File, RunTimeZone = "Asia/Calcutta",
            File = new ImportFileOptions { Format = "csv", HeaderRow = 1, ColumnBy = "name", Columns = ["Name", "When"] },
            Mappings = [new() { DestFid = 6, SourceFid = 1001 }, new() { DestFid = 14, SourceFid = 1002, DateFormat = "DD-MM-YYYY", TimeFormat = "hh:mm AM/PM" }]
        };
        var h = ImportHarness.Create(cfg, new HarnessOptions
        {
            UploadedFile = Encoding.UTF8.GetBytes("Name,When\nPen,05/01/2024 07:15 AM\n"),
            ConfigureDestination = d => d.Add(ImportHarness.Field(11, 14, "When", "DateTime"))
        });

        await h.RunAsync();

        h.Store.Inserted.Single()[14].Should().Be(new DateTime(2024, 1, 5, 1, 45, 0, DateTimeKind.Utc), "it shows as 07:15 in the browser of the person who ran it");
    }
}
