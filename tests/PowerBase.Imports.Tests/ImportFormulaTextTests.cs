using FluentAssertions;
using PowerBase.Application.Imports;

namespace PowerBase.Imports.Tests;

/// <summary>A formula that joins a file's date and time cells gives the same text for every row, whatever way each cell was typed: the date in the app's format,
/// the time as h:mm:ss AM/PM. Anything that is not wholly a date or time is left alone.</summary>
public class ImportFormulaTextTests
{
    [Theory]
    [InlineData("7/30/2026")]
    [InlineData("07-30-2026")]
    [InlineData("2026-07-30")]
    [InlineData("30-Jul-2026")]
    [InlineData("July 30, 2026")]
    public void A_date_is_shown_in_the_apps_format_whatever_way_it_was_typed(string cell) =>
        ImportFormulaText.Normalize(cell, "MM-DD-YYYY").Should().Be("07-30-2026");

    [Fact]
    public void The_apps_format_decides_the_order_and_the_separator()
    {
        ImportFormulaText.Normalize("2026-07-30", "DD-MM-YYYY").Should().Be("30-07-2026");
        ImportFormulaText.Normalize("30/07/2026", "DD/MM/YYYY").Should().Be("30/07/2026");
        ImportFormulaText.Normalize("2026-07-30", "YYYY-MM-DD").Should().Be("2026-07-30");
        ImportFormulaText.Normalize("2026-07-30", null).Should().Be("07-30-2026");
    }

    [Theory]
    [InlineData("4:00:00 pm", "4:00:00 PM")]
    [InlineData("4:00:00 PM", "4:00:00 PM")]
    [InlineData("4:00 pm", "4:00:00 PM")]
    [InlineData("16:00", "4:00:00 PM")]
    [InlineData("04:00:00", "4:00:00 AM")]
    [InlineData("1899-12-30T16:00:00", "4:00:00 PM")]
    public void A_time_is_shown_as_hours_minutes_seconds_with_AM_or_PM_in_capitals(string cell, string expected) =>
        ImportFormulaText.Normalize(cell, "MM-DD-YYYY").Should().Be(expected);

    [Fact]
    public void A_date_and_time_in_one_cell_shows_both()
    {
        ImportFormulaText.Normalize("7/30/2026 4:00:00 pm", "MM-DD-YYYY").Should().Be("07-30-2026 4:00:00 PM");
        ImportFormulaText.Normalize("2026-07-30T16:00:00", "DD-MM-YYYY").Should().Be("30-07-2026 4:00:00 PM");
    }

    [Theory]
    [InlineData("12983")]
    [InlineData("Session 5")]
    [InlineData("Hello 2026-07-30")]
    [InlineData("7/30/2026 and more")]
    [InlineData("Qualified")]
    [InlineData("1.5")]
    [InlineData("")]
    public void Anything_else_is_left_exactly_as_it_is(string cell) => ImportFormulaText.Normalize(cell, "MM-DD-YYYY").Should().Be(cell);
}
