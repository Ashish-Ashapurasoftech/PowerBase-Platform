using FluentAssertions;
using PowerBase.Application.Imports;

namespace PowerBase.Imports.Tests;

public class ImportFormulaGuardTests
{
    [Theory]
    [InlineData("Upper([Name])")]
    [InlineData("If(And(Gt([Qty], 1), Lt([Qty], 9)), \"a\", \"b\")")]
    [InlineData("\"(((((((((((((((((((((((((((((((((((((((((((((((((((((((((((\"")]
    [InlineData("[Unit price (((((((((((((((((((((((((((((((((((((((((((((((((((((((((((]")]
    [InlineData("")]
    public void Ordinary_formulas_pass(string formula) => ImportFormulaGuard.IsTooDeep(formula).Should().BeFalse();

    [Fact]
    public void Exactly_the_limit_passes_and_one_more_does_not()
    {
        string Nest(int n) => new string('(', n) + "1" + new string(')', n);
        ImportFormulaGuard.IsTooDeep(Nest(ImportFormulaGuard.MaxNesting)).Should().BeFalse();
        ImportFormulaGuard.IsTooDeep(Nest(ImportFormulaGuard.MaxNesting + 1)).Should().BeTrue();
    }

    [Fact]
    public void An_unbalanced_run_of_openers_is_caught_too() => ImportFormulaGuard.IsTooDeep(new string('(', 5000)).Should().BeTrue();

    [Fact]
    public void Siblings_do_not_add_up() => ImportFormulaGuard.IsTooDeep(string.Concat(Enumerable.Repeat("F(1)+", 500)) + "1").Should().BeFalse();
}
