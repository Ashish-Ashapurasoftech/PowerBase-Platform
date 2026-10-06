using System.Globalization;
using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Domain.Entities;
using PowerBase.Formula.Types;

namespace PowerBase.Imports.Tests;

public class ImportTypeCompatibilityTests
{
    private static AppField F(string type, bool computed = false) => new() { Fid = 9, Name = "F", TypeCode = type };

    [Theory]
    [InlineData("Number", "Text", true)]
    [InlineData("Text", "Number", true)]
    [InlineData("Currency", "Number", true)]
    [InlineData("Email", "Text", true)]
    [InlineData("SingleSelect", "Text", true)]
    [InlineData("SingleSelect", "TextMultiLine", true)]
    [InlineData("SingleSelect", "Number", false)]
    [InlineData("Text", "SingleSelect", false)]
    [InlineData("Date", "Date", true)]
    [InlineData("Date", "DateTime", false)]
    [InlineData("Date", "Text", false)]
    [InlineData("Boolean", "Text", false)]
    [InlineData("Boolean", "Boolean", true)]
    public void Only_safe_casts_are_allowed(string from, string to, bool allowed) =>
        (ImportTypeCompatibility.CheckMapping(F(from), F(to)) is null).Should().Be(allowed);

    [Theory]
    [InlineData("Reference")]
    [InlineData("User")]
    [InlineData("File")]
    [InlineData("Formula")]
    [InlineData("Lookup")]
    public void Unsupported_and_computed_fields_are_not_writable(string type) =>
        ImportTypeCompatibility.IsWritable(F(type)).Should().BeFalse();

    [Fact]
    public void Text_to_number_rejects_non_numeric_values_instead_of_writing_null()
    {
        ImportTypeCompatibility.TryConvert("12.5", F("Number"), out var ok, out _).Should().BeTrue();
        ok.Should().Be(12.5m);
        ImportTypeCompatibility.TryConvert("abc", F("Number"), out _, out var error).Should().BeFalse();
        error.Should().Contain("abc");
    }

    [Theory]
    [InlineData(12.5, "12.50", true)]
    [InlineData("Abc", "abc", true)]
    [InlineData("a", "b", false)]
    public void Duplicate_keys_compare_the_same_on_both_sides(object a, object b, bool equal)
    {
        var left = ImportKey.Normalize(a is double d ? (decimal)d : a);
        var right = ImportKey.Normalize(b is string s && decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var m) ? m : b);
        ImportKey.Comparer.Equals(left, right).Should().Be(equal);
    }

    // ---- fixed values ----

    [Theory]
    [InlineData("Number", "12.5", 12.5)]
    [InlineData("Currency", " 7 ", 7)]
    public void A_fixed_number_is_parsed_once_in_a_culture_independent_way(string type, string text, double expected)
    {
        ImportTypeCompatibility.TryParseStatic(text, F(type), out var value, out var error).Should().BeTrue(error);
        value.Should().Be((decimal)expected);
    }

    [Theory]
    [InlineData("Number", "twelve")]
    [InlineData("Number", "1,5")]
    [InlineData("Boolean", "maybe")]
    [InlineData("Date", "09/03/2026")]
    [InlineData("Date", "2026-13-40")]
    [InlineData("DateTime", "not a date")]
    [InlineData("SingleSelect", "x")]
    [InlineData("Duration", "5")]
    public void A_fixed_value_the_field_cannot_hold_is_refused_with_a_reason(string type, string text)
    {
        ImportTypeCompatibility.TryParseStatic(text, F(type), out _, out var error).Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData("true", true)]
    [InlineData("YES", true)]
    [InlineData("1", true)]
    [InlineData("false", false)]
    [InlineData("no", false)]
    [InlineData("0", false)]
    public void A_fixed_checkbox_value_accepts_the_usual_spellings(string text, bool expected)
    {
        ImportTypeCompatibility.TryParseStatic(text, F("Boolean"), out var value, out _).Should().BeTrue();
        value.Should().Be(expected);
    }

    [Fact]
    public void A_fixed_date_becomes_a_real_date_and_a_fixed_text_keeps_its_exact_characters()
    {
        ImportTypeCompatibility.TryParseStatic("2026-03-09", F("Date"), out var date, out _).Should().BeTrue();
        date.Should().Be(new DateTime(2026, 3, 9));
        ImportTypeCompatibility.TryParseStatic("  Hello  ", F("Text"), out var text, out _).Should().BeTrue();
        text.Should().Be("  Hello  ");
    }

    [Fact]
    public void A_fixed_text_longer_than_the_field_allows_is_refused_up_front()
    {
        var field = F("Text");
        field.MaxLength = 5;

        ImportTypeCompatibility.TryParseStatic("toolong", field, out _, out var error).Should().BeFalse();
        error.Should().Contain("at most 5");
    }

    // ---- what a formula may return ----

    [Theory]
    [InlineData(FormulaType.Text, "Text", true)]
    [InlineData(FormulaType.Text, "Number", true)]     // text to number is a safe cast; a value that is not a number is reported per row
    [InlineData(FormulaType.Number, "Text", true)]
    [InlineData(FormulaType.Number, "Currency", true)]
    [InlineData(FormulaType.Bool, "Boolean", true)]
    [InlineData(FormulaType.Bool, "Text", false)]
    [InlineData(FormulaType.Date, "Date", true)]
    [InlineData(FormulaType.Date, "DateTime", false)]
    [InlineData(FormulaType.DateTime, "DateTime", true)]
    [InlineData(FormulaType.DateTime, "Date", false)]
    [InlineData(FormulaType.Text, "Date", false)]
    [InlineData(FormulaType.Number, "Boolean", false)]
    [InlineData(FormulaType.Null, "Date", true)]        // the blank literal produces no value
    [InlineData(FormulaType.Duration, "Number", false)]
    [InlineData(FormulaType.User, "Text", false)]
    [InlineData(FormulaType.TextList, "Text", false)]
    public void A_formula_result_may_only_go_into_a_field_that_can_safely_take_it(FormulaType result, string destination, bool allowed) =>
        (ImportTypeCompatibility.CheckFormulaResult(result, F(destination)) is null).Should().Be(allowed);

    // ---- dates arriving as text from a formula ----

    [Fact]
    public void An_iso_date_from_a_formula_is_stored_as_a_real_date_whatever_the_machine_culture()
    {
        var previous = CultureInfo.CurrentCulture;
        CultureInfo.CurrentCulture = new CultureInfo("de-DE");
        try
        {
            ImportTypeCompatibility.TryConvert("2026-03-09", F("Date"), out var date, out _).Should().BeTrue();
            date.Should().Be(new DateTime(2026, 3, 9));
            ImportTypeCompatibility.TryConvert("2026-03-09T14:30:00", F("DateTime"), out var stamp, out _).Should().BeTrue();
            stamp.Should().Be(new DateTime(2026, 3, 9, 14, 30, 0));
        }
        finally { CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public void A_blank_or_invalid_date_text_is_handled_not_passed_to_the_database()
    {
        ImportTypeCompatibility.TryConvert("  ", F("Date"), out var blank, out var blankError).Should().BeTrue();
        blank.Should().BeNull();
        blankError.Should().BeNull();
        ImportTypeCompatibility.TryConvert("soon", F("Date"), out _, out var error).Should().BeFalse();
        error.Should().Contain("soon");
    }
}
