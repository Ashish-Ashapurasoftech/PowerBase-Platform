using FluentAssertions;
using PowerBase.Formula;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Types;

namespace PowerBase.Formula.Tests;

/// <summary>
/// <c>var &lt;type&gt; &lt;name&gt; = &lt;expr&gt;;</c> declarations followed by a result expression,
/// referenced as <c>$name</c> — Quickbase's shape for formulas long enough to need naming their
/// parts, and the form most real exported formulas of any size are written in.
/// </summary>
public class VariableDeclarationTests
{
    private static readonly FormulaEngine Engine = new();

    private static CompiledFormula Compile(string expr) => Engine.Compile(expr, new TestSchema());

    [Fact]
    public void Single_declaration_is_usable_in_the_result()
    {
        FormulaEval.Const("var number tax = 10 * 2; $tax").AsNumber().Should().Be(20);
    }

    [Fact]
    public void Later_declarations_can_use_earlier_ones()
    {
        FormulaEval.Const("var number a = 2; var number b = $a * 3; $a + $b").AsNumber().Should().Be(8);
    }

    [Fact]
    public void Declarations_may_be_followed_by_line_comments()
    {
        // Real exports comment nearly every declaration; the lexer already skipped '//' but the
        // declarations themselves used to stop the parse before it got that far.
        var expr = """
            var number recordId = 7;   //the record we are pointing at
            var text label = "id-";    // prefix
            $label & ToText($recordId)
            """;
        FormulaEval.Const(expr).AsText().Should().Be("id-7");
    }

    [Fact]
    public void Declared_variables_carry_their_initialisers_type()
    {
        // $flag is Bool, so it is usable where a bool is required.
        FormulaEval.Const("var bool flag = 1 > 0; If($flag, 5, 6)").AsNumber().Should().Be(5);
    }

    [Fact]
    public void Variable_referenced_before_declaration_is_reported()
    {
        var c = Compile("var number a = $b; var number b = 1; $a");

        c.HasErrors.Should().BeTrue();
        c.Diagnostics.Should().Contain(d => d.Message.Contains("$b"));
    }

    [Fact]
    public void Unknown_variable_is_reported()
    {
        var c = Compile("var number a = 1; $a + $nope");

        c.HasErrors.Should().BeTrue();
        c.Diagnostics.Should().Contain(d => d.Message.Contains("$nope"));
    }

    [Fact]
    public void Redeclaring_a_variable_is_reported()
    {
        var c = Compile("var number a = 1; var number a = 2; $a");

        c.HasErrors.Should().BeTrue();
        c.Diagnostics.Should().Contain(d => d.Message.Contains("declared more than once"));
    }

    [Fact]
    public void Missing_semicolon_is_reported()
    {
        var c = Compile("var number a = 1 $a");

        c.HasErrors.Should().BeTrue();
        c.Diagnostics.Should().Contain(d => d.Code == FormulaErrorCode.ExpectedToken);
    }

    [Fact]
    public void Declarations_can_read_fields()
    {
        new Bed()
            .Field("Qty", FormulaType.Number, 4m)
            .Field("Price", FormulaType.Number, 2.5m)
            .Eval("var number total = [Qty] * [Price]; $total + 1")
            .AsNumber().Should().Be(11);
    }

    [Fact]
    public void A_variable_referenced_twice_yields_the_same_value_each_time()
    {
        new Bed()
            .Field("Qty", FormulaType.Number, 3m)
            .Eval("""var number q = [Qty] * 2; ToText($q) & "/" & ToText($q)""")
            .AsText().Should().Be("6/6");
    }

    [Fact]
    public void Formulas_without_declarations_are_unaffected()
    {
        FormulaEval.Const("1 + 1").AsNumber().Should().Be(2);
    }
}
