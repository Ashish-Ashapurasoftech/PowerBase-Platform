using FluentAssertions;
using PowerBase.Formula;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Tests;

/// <summary>
/// A <c>var &lt;type&gt; &lt;name&gt; = …;</c> declaration enforces its declared type:
/// <c>var number a = "mahesh";</c> is "Expecting number but found text".
/// </summary>
public class VariableTypeEnforcementTests
{
    private static readonly FormulaEngine Engine = new();

    private static CompiledFormula Compile(string expr)
    {
        var schema = new TestSchema().Add("Qty", FormulaType.Number).Add("Name", FormulaType.Text);
        return Engine.Compile(expr, schema);
    }

    [Fact]
    public void Text_given_to_a_number_variable_is_an_error_anchored_on_the_value()
    {
        const string src = "var number t=\"mahesh\"; $t";
        var c = Compile(src);

        var d = c.Diagnostics.Should().ContainSingle(x => x.Code == FormulaErrorCode.VariableTypeMismatch).Subject;
        d.Message.Should().Be("Expecting number but found text.");
        src.Substring(d.Span.Start, d.Span.Length).Should().Be("\"mahesh\"");
        d.Span.Start.Should().Be(13); // the "column: 13" in the reference screenshot
    }

    [Theory]
    [InlineData("var number a = 1; $a")]
    [InlineData("var text a = \"x\"; $a")]
    [InlineData("var bool a = true; $a")]
    [InlineData("var date a = Today(); $a")]
    [InlineData("var timestamp a = Now(); $a")]
    [InlineData("var duration a = Days(2); $a")]
    [InlineData("var user a = User(); $a")]
    [InlineData("var list-user a = ToUserList(User()); $a")]
    [InlineData("var number a = [Qty] * 2; $a")]
    [InlineData("VAR NUMBER a = 1; $a")]
    public void Matching_declared_types_compile(string src)
    {
        var c = Compile(src);
        c.HasErrors.Should().BeFalse(because: string.Join("; ", c.Diagnostics));
    }

    [Theory]
    [InlineData("var text a = 1; $a", "Expecting text but found number.")]
    [InlineData("var bool a = 1; $a", "Expecting bool but found number.")]
    [InlineData("var number a = Today(); $a", "Expecting number but found date.")]
    [InlineData("var list-user a = User(); $a", "Expecting list-user but found user.")]
    public void Each_mismatch_names_both_types(string src, string message)
    {
        Compile(src).Diagnostics.Should().Contain(d => d.Code == FormulaErrorCode.VariableTypeMismatch && d.Message == message);
    }

    [Fact]
    public void Unknown_type_keyword_is_reported_on_the_keyword()
    {
        const string src = "var foo a = 1; $a";
        var c = Compile(src);

        var d = c.Diagnostics.Should().ContainSingle(x => x.Message.StartsWith("Unknown variable type 'foo'")).Subject;
        src.Substring(d.Span.Start, d.Span.Length).Should().Be("foo");
    }

    [Fact]
    public void A_bad_variable_reports_once_not_at_every_use()
    {
        var c = Compile("var number a = \"x\"; $a + $a");

        c.Diagnostics.Count(d => d.Severity == FormulaSeverity.Error).Should().Be(1);
    }

    [Fact]
    public void A_variable_carries_its_declared_type_into_the_result()
    {
        Compile("var number a = 1; $a").ResultType.Should().Be(FormulaType.Number);
    }

    [Fact]
    public void Declarations_with_no_result_expression_say_so()
    {
        var c = Compile("var text textString = \"Refresh Projects\";");

        c.HasErrors.Should().BeTrue();
        c.Diagnostics.Should().ContainSingle(d => d.Message == "A formula must end with a result expression after the var declarations.");
    }

    [Fact]
    public void The_hyphen_in_list_user_does_not_break_ordinary_subtraction()
    {
        FormulaEval.Const("var number a = 5; $a - 2").AsNumber().Should().Be(3);
    }
}
