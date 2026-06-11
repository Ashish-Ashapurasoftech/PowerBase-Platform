using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Formulas;

public class FormulaProjectorTests
{
    private static FormulaProjector NewProjector()
    {
        var qc = Substitute.For<IQueryContext>();
        qc.UserId.Returns(0L);
        return new FormulaProjector(new FormulaEngine(), qc);
    }

    private static AppField Field(int fid, string name, string typeCode, string? settings = null) => new()
    {
        Id = fid,
        Fid = fid,
        Name = name,
        TypeCode = typeCode,
        Settings = settings,
    };

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows(params Dictionary<string, object?>[] rows) =>
        rows.Cast<IReadOnlyDictionary<string, object?>>().ToList();

    [Fact]
    public void Projects_numeric_formula_over_field_values()
    {
        var fields = new List<AppField>
        {
            Field(1, "Qty", "Number"),
            Field(2, "Total", "Formula", "{\"resultType\":\"Number\",\"expression\":\"[Qty] * 2\"}"),
        };
        var rows = Rows(new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = 10m });

        var result = NewProjector().Project(fields, rows);

        result.Should().HaveCount(1);
        result[0][2].Should().Be(20m);
    }

    [Fact]
    public void Text_formula_concatenates_fields()
    {
        var fields = new List<AppField>
        {
            Field(1, "First", "Text"),
            Field(2, "Last", "Text"),
            Field(3, "Full", "Formula", "{\"resultType\":\"Text\",\"expression\":\"[First] & [Last]\"}"),
        };
        var rows = Rows(new Dictionary<string, object?>
        {
            [PhysicalNaming.ColumnName(1)] = "Ada",
            [PhysicalNaming.ColumnName(2)] = "Lovelace",
        });

        NewProjector().Project(fields, rows)[0][3].Should().Be("AdaLovelace");
    }

    [Fact]
    public void Returns_empty_maps_when_no_formula_fields()
    {
        var fields = new List<AppField> { Field(1, "Qty", "Number") };
        var rows = Rows(new Dictionary<string, object?> { [PhysicalNaming.ColumnName(1)] = 5m });

        NewProjector().Project(fields, rows)[0].Should().BeEmpty();
    }

    [Fact]
    public void Invalid_expression_yields_null_not_exception()
    {
        var fields = new List<AppField>
        {
            Field(1, "Total", "Formula", "{\"resultType\":\"Number\",\"expression\":\"[Missing] + 1\"}"),
        };
        var rows = Rows(new Dictionary<string, object?>());

        NewProjector().Project(fields, rows)[0][1].Should().BeNull();
    }
}
