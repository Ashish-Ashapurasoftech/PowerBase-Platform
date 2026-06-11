using System.Globalization;
using FluentAssertions;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Tests;

public class EvaluatorTests
{
    [Theory]
    // Arithmetic + precedence quirks
    [InlineData("1 + 2 * 3", "7")]
    [InlineData("(1 + 2) * 3", "9")]
    [InlineData("2 ^ 3 ^ 2", "512")]      // right-associative
    [InlineData("-2 ^ 2", "4")]           // unary binds tighter than ^ → (-2)^2
    [InlineData("10 / 4", "2.5")]
    // Math functions
    [InlineData("Abs(-5)", "5")]
    [InlineData("Round(2.5)", "3")]
    [InlineData("Round(-3.5)", "-3")]     // halves round toward +∞
    [InlineData("Round(2.345, 2)", "2.35")]
    [InlineData("RoundDown(2.99)", "2")]
    [InlineData("RoundUp(2.01)", "3")]
    [InlineData("Int(-2.9)", "-2")]
    [InlineData("Mod(7, 3)", "1")]
    [InlineData("Sqrt(9)", "3")]
    [InlineData("Min(3, 1, 2)", "1")]
    [InlineData("Max(3, 1, 2)", "3")]
    [InlineData("Sum(1, 2, 3, 4)", "10")]
    [InlineData("Average(2, 4, 6)", "4")]
    [InlineData("Length(\"hello\")", "5")]
    [InlineData("ToNumber(\"42\")", "42")]
    // Logical / control flow returning numbers
    [InlineData("If(1 > 0, 10, 20)", "10")]
    [InlineData("If(1 < 0, 10, 20)", "20")]
    [InlineData("Nz(ToNumber(\"x\"), 99)", "99")]
    [InlineData("Case(2, 1, 10, 2, 20, 3, 30)", "20")]
    [InlineData("Case(5, 1, 10, 2, 20, 99)", "99")]
    // Date/duration extraction
    [InlineData("Year(ToDate(\"2026-06-11\"))", "2026")]
    [InlineData("Month(ToDate(\"2026-06-11\"))", "6")]
    [InlineData("Quarter(ToDate(\"2026-06-11\"))", "2")]
    [InlineData("ToDays(Hours(48))", "2")]
    [InlineData("DateDiff(ToDate(\"2026-06-11\"), ToDate(\"2026-06-01\"))", "10")]
    public void Numeric_results(string expr, string expected)
    {
        var v = FormulaEval.Const(expr);
        v.Type.Should().Be(FormulaType.Number);
        v.AsNumber().Should().Be(decimal.Parse(expected, CultureInfo.InvariantCulture));
    }

    [Theory]
    [InlineData("\"a\" & \"b\"", "ab")]
    [InlineData("Left(\"hello\", 2)", "he")]
    [InlineData("Right(\"hello\", 2)", "lo")]
    [InlineData("Mid(\"hello\", 2, 3)", "ell")]
    [InlineData("Upper(\"abc\")", "ABC")]
    [InlineData("Lower(\"ABC\")", "abc")]
    [InlineData("Trim(\"  hi  \")", "hi")]
    [InlineData("Replace(\"a-b-c\", \"-\", \"+\")", "a+b+c")]
    [InlineData("Part(\"a-b-c\", 2, \"-\")", "b")]
    [InlineData("Concat(\"a\", \"b\", \"c\")", "abc")]
    [InlineData("List(\", \", \"a\", \"\", \"b\")", "a, b")]  // empties skipped
    [InlineData("ToText(42)", "42")]
    [InlineData("ToText(2.5)", "2.5")]
    [InlineData("ToText(true)", "true")]
    [InlineData("If(true, \"yes\", \"no\")", "yes")]
    [InlineData("\"x\" & ToText(1 + 2)", "x3")]
    [InlineData("Case(\"b\", \"a\", \"Apple\", \"b\", \"Banana\")", "Banana")]
    public void Text_results(string expr, string expected)
    {
        var v = FormulaEval.Const(expr);
        v.Type.Should().Be(FormulaType.Text);
        v.AsText().Should().Be(expected);
    }

    [Theory]
    [InlineData("1 = 1", true)]
    [InlineData("1 <> 2", true)]
    [InlineData("2 > 1 and 3 > 2", true)]
    [InlineData("1 > 2 or 3 > 2", true)]
    [InlineData("not false", true)]
    [InlineData("Contains(\"hello\", \"ell\")", true)]
    [InlineData("Contains(\"hello\", \"xyz\")", false)]
    [InlineData("IsNull(ToNumber(\"x\"))", true)]
    [InlineData("IsNotNull(5)", true)]
    [InlineData("\"a\" = \"a\"", true)]
    [InlineData("\"a\" = \"A\"", false)]              // text equality is case-sensitive
    [InlineData("true and false", false)]
    [InlineData("1 < 2 = true", true)]               // (1<2)=true
    public void Bool_results(string expr, bool expected)
    {
        var v = FormulaEval.Const(expr);
        v.Type.Should().Be(FormulaType.Bool);
        v.AsBool().Should().Be(expected);
    }

    [Fact]
    public void Field_arithmetic_uses_record_values()
    {
        var v = new Bed().Field("Qty", FormulaType.Number, 10).Field("Price", FormulaType.Number, 5)
            .Eval("[Qty] * [Price]");
        v.AsNumber().Should().Be(50);
    }

    [Fact]
    public void Null_field_propagates_through_arithmetic()
    {
        var v = new Bed().Field("Qty", FormulaType.Number, null).Eval("[Qty] + 1");
        v.IsNull.Should().BeTrue();
        v.Type.Should().Be(FormulaType.Number);
    }

    [Fact]
    public void Nz_substitutes_for_null_field()
    {
        new Bed().Field("Qty", FormulaType.Number, null).Eval("Nz([Qty], 0) + 5").AsNumber().Should().Be(5);
    }

    [Fact]
    public void Null_text_field_concatenates_as_empty()
    {
        new Bed().Field("Name", FormulaType.Text, null).Eval("\"x\" & [Name]").AsText().Should().Be("x");
    }

    [Fact]
    public void Comparison_with_null_is_false()
    {
        new Bed().Field("Qty", FormulaType.Number, null).Eval("[Qty] > 5").AsBool().Should().BeFalse();
    }

    [Fact]
    public void Date_plus_duration_field()
    {
        var v = new Bed().Field("Start", FormulaType.Date, "2026-01-01").Eval("[Start] + Days(10)");
        v.Type.Should().Be(FormulaType.Date);
        v.AsDate().Should().Be(new DateOnly(2026, 1, 11));
    }

    [Fact]
    public void Today_uses_the_clock_from_options()
    {
        var opt = new EvaluationOptions { UtcNow = new DateTime(2026, 6, 11, 0, 0, 0, DateTimeKind.Utc) };
        FormulaEval.Const("Year(Today())", opt).AsNumber().Should().Be(2026);
    }

    [Fact]
    public void User_functions_read_current_user()
    {
        var opt = new EvaluationOptions { CurrentUser = new UserRef("u1", "a@b.com") };
        FormulaEval.Const("UserToEmail(User())", opt).AsText().Should().Be("a@b.com");
        FormulaEval.Const("UserToID(User())", opt).AsText().Should().Be("u1");
    }

    [Fact]
    public void Evaluate_throws_on_compile_errors()
    {
        var engine = new FormulaEngine();
        var schema = new TestSchema().Add("Qty", FormulaType.Number);
        var compiled = engine.Compile("[Qty] + \"x\"", schema);

        compiled.HasErrors.Should().BeTrue();
        Action act = () => engine.Evaluate(compiled, EmptyContext.Instance, EvaluationOptions.Default);
        act.Should().Throw<FormulaEvaluationException>();
    }
}
