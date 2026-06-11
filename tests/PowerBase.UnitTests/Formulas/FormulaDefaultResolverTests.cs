using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Formulas;

public class FormulaDefaultResolverTests
{
    private static FormulaDefaultResolver NewResolver()
    {
        var qc = Substitute.For<IQueryContext>();
        qc.UserId.Returns(0L);
        return new FormulaDefaultResolver(new FormulaEngine(), qc);
    }

    private static AppField Field(int fid, string name, string typeCode) =>
        new() { Id = fid, Fid = fid, Name = name, TypeCode = typeCode };

    [Fact]
    public void Literal_default_is_returned_unchanged()
    {
        var f = Field(1, "Status", "Text");
        NewResolver().Resolve("Open", f, new[] { f }, new Dictionary<long, object?>())
            .Should().Be("Open");
    }

    [Fact]
    public void Formula_default_is_evaluated_against_record_values()
    {
        var qty = Field(1, "Qty", "Number");
        var total = Field(2, "Total", "Number");
        var values = new Dictionary<long, object?> { [1] = 5m };

        NewResolver().Resolve("=[Qty] * 10", total, new[] { qty, total }, values)
            .Should().Be(50m);
    }

    [Fact]
    public void Invalid_formula_default_resolves_to_null()
    {
        var f = Field(1, "X", "Number");
        NewResolver().Resolve("=[NoSuchField] + 1", f, new[] { f }, new Dictionary<long, object?>())
            .Should().BeNull();
    }
}
