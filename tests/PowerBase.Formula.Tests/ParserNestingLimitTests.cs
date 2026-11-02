using FluentAssertions;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Syntax;

namespace PowerBase.Formula.Tests;

public class ParserNestingLimitTests
{
    private static string NestedIf(int depth) =>
        string.Concat(Enumerable.Repeat("If(true,", depth)) + "\"x\"" + string.Concat(Enumerable.Repeat(",\"y\")", depth));

    [Fact]
    public void Parse_NestingAtTheLimit_ParsesWithoutErrors()
    {
        Parser.Parse(NestedIf(Parser.MAX_NESTING_DEPTH - 1)).HasErrors.Should().BeFalse();
    }

    [Theory]
    [InlineData(Parser.MAX_NESTING_DEPTH + 50)]
    [InlineData(5000)]
    public void Parse_NestingBeyondTheLimit_ReturnsOneTooComplexErrorInsteadOfOverflowingTheStack(int depth)
    {
        // Run on a thread with a small stack: before the limit existed this crashed the whole process.
        ParseResult? result = null;
        var thread = new Thread(() => result = Parser.Parse(NestedIf(depth)), 256 * 1024);
        thread.Start();
        thread.Join();

        result!.Diagnostics.Should().ContainSingle(d => d.Code == FormulaErrorCode.FormulaTooComplex);
        result.Diagnostics.Should().HaveCount(1, "follow-on errors from the skipped input would only hide the real reason");
    }

    [Fact]
    public void Parse_LongChainOfUnaryOperators_IsRefusedNotOverflowed()
    {
        var result = Parser.Parse(string.Concat(Enumerable.Repeat("not ", 100_000)) + "true");
        result.Diagnostics.Should().Contain(d => d.Code == FormulaErrorCode.FormulaTooComplex);
    }
}
