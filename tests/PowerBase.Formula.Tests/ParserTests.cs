using FluentAssertions;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Syntax;

namespace PowerBase.Formula.Tests;

public class ParserTests
{
    private static string ParsePrint(string src) => AstPrinter.Print(Parser.Parse(src).Root);

    [Theory]
    // Multiplicative binds tighter than additive
    [InlineData("1 + 2 * 3", "(1 + (2 * 3))")]
    [InlineData("1 * 2 + 3", "((1 * 2) + 3)")]
    // Additive is left-associative
    [InlineData("1 + 2 + 3", "((1 + 2) + 3)")]
    // & (concat) sits at additive level, left-associative
    [InlineData("[a] & [b] & [c]", "(([a] & [b]) & [c])")]
    // ^ is right-associative
    [InlineData("2 ^ 3 ^ 2", "(2 ^ (3 ^ 2))")]
    // Unary binds tighter than ^  →  -2^2 == (-2)^2
    [InlineData("-2 ^ 2", "((- 2) ^ 2)")]
    [InlineData("2 ^ -3", "(2 ^ (- 3))")]
    // Comparison binds tighter than equality
    [InlineData("1 < 2 = true", "((1 < 2) = true)")]
    // and binds tighter than or; equality tighter than and
    [InlineData("1 = 2 and 3 = 4 or 5 = 6", "(((1 = 2) and (3 = 4)) or (5 = 6))")]
    // not binds tighter than and
    [InlineData("not true and false", "((not true) and false)")]
    // Parentheses override precedence
    [InlineData("(1 + 2) * 3", "((1 + 2) * 3)")]
    public void Respects_precedence_and_associativity(string src, string expected)
    {
        ParsePrint(src).Should().Be(expected);
    }

    [Fact]
    public void Parses_function_call_with_args()
    {
        ParsePrint("If([x] > 1, \"a\", \"b\")").Should().Be("If(([x] > 1), \"a\", \"b\")");
    }

    [Fact]
    public void Parses_zero_arg_function()
    {
        ParsePrint("Today()").Should().Be("Today()");
    }

    [Fact]
    public void Clean_formula_has_no_diagnostics()
    {
        Parser.Parse("If([x] > 1, [x] * 2, 0)").HasErrors.Should().BeFalse();
    }

    [Fact]
    public void Empty_formula_reports_diagnostic()
    {
        Parser.Parse("   ").Diagnostics.Should().ContainSingle(d => d.Code == FormulaErrorCode.EmptyExpression);
    }

    [Fact]
    public void Trailing_operator_reports_diagnostic_without_hanging()
    {
        Parser.Parse("1 +").HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Function_without_parens_reports_expected_token()
    {
        Parser.Parse("Today").Diagnostics.Should().Contain(d => d.Code == FormulaErrorCode.ExpectedToken);
    }
}
