using FluentAssertions;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Binding;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.UnitTests.Formulas;

public class FieldIdReferenceTests
{
    private static AppField Field(int fid, string name, string label, string typeCode = "Number") =>
        new() { Id = fid, Fid = fid, Name = name, Label = label, TypeCode = typeCode };

    private sealed class MapContext(Dictionary<long, object?> values) : IRecordContext
    {
        public object? GetValue(long fid) => values.TryGetValue(fid, out var v) ? v : null;
    }

    [Theory]
    [InlineData("fid_14", true, 14)]
    [InlineData("FID_14", true, 14)]
    [InlineData(" fid_14 ", true, 14)]
    [InlineData("fid_14|Customer Name", true, 14)]
    [InlineData("fid_", false, 0)]
    [InlineData("fid_|x", false, 0)]
    [InlineData("fid_x1", false, 0)]
    [InlineData("fid_-1", false, 0)]
    [InlineData("fid_1 4", false, 0)]
    [InlineData("fid_99999999999999999999", false, 0)]
    [InlineData("Customer Name", false, 0)]
    [InlineData(null, false, 0)]
    public void TryParse_recognises_only_well_formed_id_references(string? token, bool expected, long fid)
    {
        FieldIdReference.TryParse(token, out var parsed).Should().Be(expected);
        if (expected) parsed.Should().Be(fid);
    }

    [Theory]
    [InlineData("fid_14", true)]
    [InlineData("Fid_7", true)]
    [InlineData("Fid 7", false)]
    [InlineData("fid", false)]
    [InlineData("Customer fid_14", false)]
    public void IsReservedLabel_blocks_labels_shaped_like_an_id_reference(string label, bool reserved)
        => FieldIdReference.IsReservedLabel(label).Should().Be(reserved);

    [Fact]
    public void Id_reference_binds_to_the_field_and_reports_it_as_referenced()
    {
        var schema = new AppFieldSchema(new[] { Field(14, "C_customerNumber", "Customer Number") });

        var compiled = new FormulaEngine().Compile("[fid_14] + 1", schema, FormulaType.Number);

        compiled.HasErrors.Should().BeFalse();
        compiled.ReferencedFieldIds.Should().Equal(14);
    }

    [Fact]
    public void Name_and_id_references_resolve_to_the_same_field()
    {
        var schema = new AppFieldSchema(new[] { Field(14, "C_customerNumber", "Customer Number") });
        var engine = new FormulaEngine();

        var byName = engine.Compile("[Customer Number] * 2", schema, FormulaType.Number);
        var byId = engine.Compile("[fid_14] * 2", schema, FormulaType.Number);

        byName.ReferencedFieldIds.Should().Equal(byId.ReferencedFieldIds);
        var ctx = new MapContext(new() { [14] = 21m });
        var options = new EvaluationOptions();
        engine.Evaluate(byName, ctx, options).Should().Be(engine.Evaluate(byId, ctx, options));
    }

    [Fact]
    public void Id_reference_keeps_working_after_the_field_is_renamed_but_the_old_name_does_not()
    {
        var engine = new FormulaEngine();
        var renamed = new AppFieldSchema(new[] { Field(14, "C_customerNumber", "Client Number") });

        engine.Compile("[fid_14]", renamed, FormulaType.Number).HasErrors.Should().BeFalse();
        engine.Compile("[Customer Number]", renamed, FormulaType.Number).HasErrors.Should().BeTrue();
    }

    [Fact]
    public void Id_reference_with_a_name_snapshot_resolves_by_the_id()
    {
        var schema = new AppFieldSchema(new[] { Field(14, "C_customerNumber", "Client Number") });

        var compiled = new FormulaEngine().Compile("[fid_14|Customer Number]", schema, FormulaType.Number);

        compiled.HasErrors.Should().BeFalse();
        compiled.ReferencedFieldIds.Should().Equal(14);
    }

    [Fact]
    public void Unknown_id_is_reported_as_an_unknown_field()
    {
        var schema = new AppFieldSchema(new[] { Field(14, "C_customerNumber", "Customer Number") });

        var diagnostics = new FormulaEngine().Validate("[fid_99]", schema);

        diagnostics.Should().Contain(d => d.Message.Contains("fid_99"));
    }

    [Fact]
    public void Real_field_name_wins_over_the_id_spelling()
    {
        // Labels shaped like fid_N are rejected on save, but data that predates the rule must not be hijacked.
        var schema = new AppFieldSchema(new[]
        {
            Field(1, "C_a", "fid_2"),
            Field(2, "C_b", "Other"),
        });

        var compiled = new FormulaEngine().Compile("[fid_2]", schema, FormulaType.Number);

        compiled.ReferencedFieldIds.Should().Equal(1);
    }
}
