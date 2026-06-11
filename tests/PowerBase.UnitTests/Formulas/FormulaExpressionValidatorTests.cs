using FluentAssertions;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Types;

namespace PowerBase.UnitTests.Formulas;

public class FormulaExpressionValidatorTests
{
    private static FormulaExpressionValidator NewValidator() => new(new FormulaEngine());

    private static AppField Field(int fid, string name, string typeCode) =>
        new() { Id = fid, Fid = fid, Name = name, TypeCode = typeCode };

    [Fact]
    public void Valid_boolean_expression_has_no_errors()
    {
        var fields = new[] { Field(1, "Status", "Text") };
        NewValidator().Validate("[Status] = \"Open\"", fields, FormulaType.Bool).Should().BeEmpty();
    }

    [Fact]
    public void Non_boolean_expression_is_rejected_when_bool_expected()
    {
        var fields = new[] { Field(1, "Qty", "Number") };
        NewValidator().Validate("[Qty] + 1", fields, FormulaType.Bool).Should().NotBeEmpty();
    }

    [Fact]
    public void Unknown_field_is_reported()
    {
        var fields = new[] { Field(1, "Qty", "Number") };
        NewValidator().Validate("[Nope] = 1", fields, FormulaType.Bool).Should().Contain(m => m.Contains("Nope"));
    }
}
