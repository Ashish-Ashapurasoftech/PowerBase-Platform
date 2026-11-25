using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Entities;

namespace PowerBase.Imports.Tests;

/// <summary>A file's columns are all text, so an import's filter on one lets the operator say what to read the cell as (<c>ValueType</c>): a number for "is greater
/// than", a date for "is before", "is on or after" and "is during", a checkbox for "is equal to checked". A cell that cannot be read that way matches nothing.</summary>
public class ImportFileTypedFilterTests
{
    private static readonly AppField Column = new() { Fid = 1001, Name = "Col", Label = "Col", TypeCode = "Text" };

    private static FilterCondition Cond(string op, string? value, string? valueType, string? valueMode = null) =>
        new() { FieldId = 1001, Operator = op, Value = value, ValueType = valueType, ValueMode = valueMode };

    private static bool Matches(FilterCondition condition, object? cell)
    {
        var resolved = RunReportQueryHandler.ResolveDateValueModeConditions(new FilterGroup { Nodes = [new FilterNode { Condition = condition }] })!;
        return new ImportRowFilter(resolved, [Column]).Matches(new Dictionary<string, object?> { ["f_1001"] = cell });
    }

    // ---- numbers ----

    [Theory]
    [InlineData("gt", "10", "11", true)]
    [InlineData("gt", "10", "9", false)]
    [InlineData("gt", "10", "10", false)]
    [InlineData("gte", "10", "10", true)]
    [InlineData("lt", "10", "9.5", true)]
    [InlineData("lte", "10", "10.01", false)]
    public void A_number_is_compared_as_a_number_not_as_text(string op, string value, string cell, bool expected) =>
        Matches(Cond(op, value, "number"), cell).Should().Be(expected, "as text '9' is after '10'");

    [Theory]
    [InlineData("abc")]
    [InlineData("")]
    [InlineData(null)]
    public void A_cell_that_is_not_a_number_matches_no_number_comparison(string? cell)
    {
        Matches(Cond("gt", "1", "number"), cell).Should().BeFalse();
        Matches(Cond("lt", "1", "number"), cell).Should().BeFalse();
    }

    // ---- dates ----

    [Theory]
    [InlineData("lt", "2026-04-05", "2026-04-04", true)]
    [InlineData("lt", "2026-04-05", "2026-04-05", false)]
    [InlineData("lte", "2026-04-05", "2026-04-05", true)]
    [InlineData("gt", "2026-04-05", "2026-04-06", true)]
    [InlineData("gte", "2026-04-05", "2026-04-05", true)]
    [InlineData("gt", "2026-04-05", "2026-04-05", false)]
    public void A_date_is_read_from_the_text_and_compared_by_day(string op, string value, string cell, bool expected) =>
        Matches(Cond(op, value, "date"), cell).Should().Be(expected);

    [Theory]
    [InlineData("5-Apr-2026")]
    [InlineData("April 5, 2026")]
    [InlineData("2026-04-05 14:30")]
    [InlineData("2026-04-05T09:00:00Z")]
    public void A_date_is_understood_in_the_forms_a_file_writes_it_and_its_time_does_not_matter(string cell)
    {
        Matches(Cond("date_eq", "2026-04-05", "date"), cell).Should().BeTrue();
        Matches(Cond("lte", "2026-04-05", "date"), cell).Should().BeTrue("on or before the day, whatever the time");
        Matches(Cond("gt", "2026-04-05", "date"), cell).Should().BeFalse("the same day is not after it");
    }

    [Theory]
    [InlineData("some text")]
    [InlineData("not a date")]
    [InlineData("")]
    public void Text_in_a_date_comparison_matches_nothing_rather_than_everything(string cell)
    {
        Matches(Cond("lt", "2026-04-05", "date"), cell).Should().BeFalse();
        Matches(Cond("gt", "2026-04-05", "date"), cell).Should().BeFalse();
        Matches(Cond("date_eq", "2026-04-05", "date"), cell).Should().BeFalse();
    }

    [Fact]
    public void Is_during_and_is_not_during_read_the_cell_as_a_date_after_the_period_is_worked_out()
    {
        // "the current year" is worked out to a range of days, which keeps the type, so a text cell is read as a date for both ends.
        var thisYear = DateTime.UtcNow.Year;
        var during = Cond("during", "1:year", "date", "duringCurrent");
        Matches(during, $"{thisYear}-06-15").Should().BeTrue();
        Matches(during, $"{thisYear - 2}-06-15").Should().BeFalse();
        Matches(during, "not a date").Should().BeFalse("text is not during any period");

        var notDuring = Cond("notDuring", "1:year", "date", "duringCurrent");
        Matches(notDuring, $"{thisYear - 2}-06-15").Should().BeTrue();
        Matches(notDuring, $"{thisYear}-06-15").Should().BeFalse();
        Matches(notDuring, "not a date").Should().BeFalse("text is not outside a period either: it is not a date at all");
    }

    [Fact]
    public void A_relative_date_value_is_worked_out_and_keeps_the_type()
    {
        var yesterday = DateTime.UtcNow.Date.AddDays(-1).ToString("yyyy-MM-dd");
        var lastYear = DateTime.UtcNow.Date.AddYears(-1).ToString("yyyy-MM-dd");
        Matches(Cond("gte", null, "date", "yesterday"), yesterday).Should().BeTrue();
        Matches(Cond("gte", null, "date", "yesterday"), lastYear).Should().BeFalse();
    }

    // ---- checkboxes ----

    [Theory]
    [InlineData("Yes", true)]
    [InlineData("TRUE", true)]
    [InlineData("checked", true)]
    [InlineData("1", true)]
    [InlineData("No", false)]
    [InlineData("false", false)]
    [InlineData("Unchecked", false)]
    [InlineData("0", false)]
    [InlineData("", false)]
    public void Is_equal_to_checked_reads_yes_true_checked_and_1_as_checked_and_a_blank_as_unchecked(string cell, bool isChecked)
    {
        Matches(Cond("eq", "true", "checkbox"), cell).Should().Be(isChecked);
        Matches(Cond("eq", "false", "checkbox"), cell).Should().Be(!isChecked);
        Matches(Cond("ne", "true", "checkbox"), cell).Should().Be(!isChecked);
    }

    [Fact]
    public void A_word_that_is_neither_checked_nor_unchecked_matches_neither()
    {
        Matches(Cond("eq", "true", "checkbox"), "maybe").Should().BeFalse();
        Matches(Cond("eq", "false", "checkbox"), "maybe").Should().BeFalse();
        Matches(Cond("ne", "true", "checkbox"), "maybe").Should().BeFalse("it cannot be said to differ from checked when it is not a checkbox value at all");
    }

    // ---- no type: as before ----

    [Fact]
    public void A_condition_with_no_type_still_compares_the_text_as_it_always_did()
    {
        Matches(Cond("contains", "ell", null), "hello").Should().BeTrue();
        Matches(Cond("eq", "hello", null), "HELLO").Should().BeTrue();
        Matches(Cond("gt", "10", null), "9").Should().BeFalse("two numbers compare as numbers already");
    }

    [Fact]
    public void Only_number_date_and_checkbox_are_types()
    {
        ImportRowFilter.IsKnownValueType(null).Should().BeTrue();
        ImportRowFilter.IsKnownValueType("number").Should().BeTrue();
        ImportRowFilter.IsKnownValueType("date").Should().BeTrue();
        ImportRowFilter.IsKnownValueType("checkbox").Should().BeTrue();
        ImportRowFilter.IsKnownValueType("guid").Should().BeFalse();
    }

    [Fact]
    public void The_type_survives_a_save_and_load()
    {
        var back = ImportJson.Deserialize<FilterCondition>(ImportJson.Serialize(Cond("lt", "2026-01-01", "date")))!;
        back.ValueType.Should().Be("date");
    }
}
