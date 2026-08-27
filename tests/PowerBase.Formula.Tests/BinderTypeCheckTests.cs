using FluentAssertions;
using PowerBase.Formula;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Tests;

public class BinderTypeCheckTests
{
    private static readonly FormulaEngine Engine = new();

    private static TestSchema Schema() => new TestSchema()
        .Add("Qty", FormulaType.Number)
        .Add("Price", FormulaType.Number)
        .Add("Name", FormulaType.Text)
        .Add("Note", FormulaType.Text)
        .Add("Flag", FormulaType.Bool)
        .Add("Start", FormulaType.Date)
        .Add("End", FormulaType.Date)
        .Add("Span", FormulaType.Duration)
        .Add("Clock", FormulaType.Time)
        .Add("Clock2", FormulaType.Time);

    private static CompiledFormula Compile(string expr, FormulaType? expected = null) => Engine.Compile(expr, Schema(), expected);

    [Fact]
    public void Resolves_fields_and_infers_numeric_result()
    {
        var c = Compile("[Qty] * [Price]");
        c.HasErrors.Should().BeFalse();
        c.ResultType.Should().Be(FormulaType.Number);
        c.ReferencedFieldIds.Should().HaveCount(2);
    }

    [Fact]
    public void Unknown_field_reports_diagnostic()
    {
        Compile("[Nope] + 1").Diagnostics.Should().Contain(d => d.Code == FormulaErrorCode.UnknownField);
    }

    [Fact]
    public void Old_value_reference_is_unsupported()
    {
        Compile("[old.Flag]").Diagnostics.Should().Contain(d => d.Code == FormulaErrorCode.UnsupportedReference);
    }

    [Theory]
    [InlineData("[Name] + 1")]          // text + number
    [InlineData("[Name] & 1")]          // text & number (concat needs text)
    [InlineData("[Qty] and [Flag]")]    // number used as boolean
    [InlineData("[Qty] = [Name]")]      // number = text
    [InlineData("[Qty] < [Name]")]      // number < text
    [InlineData("not [Qty]")]           // not on a number
    public void Type_mismatches_are_reported(string expr)
    {
        Compile(expr).Diagnostics.Should().Contain(d => d.Code == FormulaErrorCode.TypeMismatch);
    }

    [Theory]
    [InlineData("[Qty] < [Price]", FormulaType.Bool)]
    [InlineData("[Qty] = [Price]", FormulaType.Bool)]
    [InlineData("[Flag] and true", FormulaType.Bool)]
    [InlineData("[Name] & [Note]", FormulaType.Text)]
    [InlineData("[Qty] + [Price]", FormulaType.Number)]
    [InlineData("-[Qty]", FormulaType.Number)]
    [InlineData("not [Flag]", FormulaType.Bool)]
    public void Well_typed_expressions_infer_result(string expr, FormulaType expected)
    {
        var c = Compile(expr);
        c.HasErrors.Should().BeFalse();
        c.ResultType.Should().Be(expected);
    }

    [Theory]
    [InlineData("[End] - [Start]", FormulaType.Duration)]  // Date - Date → Duration
    [InlineData("[Start] + [Span]", FormulaType.Date)]     // Date + Duration → Date
    [InlineData("[Span] + [Span]", FormulaType.Duration)]  // Duration + Duration
    [InlineData("[Span] * 2", FormulaType.Duration)]       // Duration * Number
    public void Date_and_duration_arithmetic(string expr, FormulaType expected)
    {
        var c = Compile(expr);
        c.HasErrors.Should().BeFalse();
        c.ResultType.Should().Be(expected);
    }

    [Theory]
    [InlineData("[Clock2] - [Clock]", FormulaType.Duration)]  // Time - Time → Duration
    [InlineData("[Clock] + [Span]", FormulaType.Time)]        // Time + Duration → Time
    [InlineData("[Clock] - [Span]", FormulaType.Time)]        // Time - Duration → Time
    [InlineData("[Clock] < [Clock2]", FormulaType.Bool)]      // Time ordering
    [InlineData("[Clock] = [Clock2]", FormulaType.Bool)]      // Time equality
    public void Time_arithmetic_and_comparison(string expr, FormulaType expected)
    {
        var c = Compile(expr);
        c.HasErrors.Should().BeFalse();
        c.ResultType.Should().Be(expected);
    }

    [Fact]
    public void Result_type_mismatch_against_expected_is_reported()
    {
        Compile("[Qty]", FormulaType.Text).Diagnostics
            .Should().Contain(d => d.Code == FormulaErrorCode.ResultTypeMismatch);
    }

    [Fact]
    public void Unknown_function_is_reported()
    {
        // Functions are registered in the next step; until then any call is unknown.
        Compile("Bogus(1)").Diagnostics.Should().Contain(d => d.Code == FormulaErrorCode.UnknownFunction);
    }
}
