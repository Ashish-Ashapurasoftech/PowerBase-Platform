using FluentAssertions;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.UnitTests.Formulas;

/// <summary>GetFieldValue(&lt;field id&gt;) — the value of a field named by its ID, typed as that field.</summary>
public class GetFieldValueTests
{
    private static AppField Field(int fid, string label, string typeCode) =>
        new() { Id = fid, Fid = fid, Name = $"C_{fid}", Label = label, TypeCode = typeCode };

    private static readonly AppFieldSchema Schema = new(new[]
    {
        Field(14, "Quantity", "Number"),
        Field(15, "Due Date", "Date"),
        Field(16, "Customer", "Text"),
        Field(17, "Approved", "Boolean"),
    });

    private static readonly FormulaEngine Engine = new();

    private sealed class MapContext(Dictionary<long, object?> values) : IRecordContext
    {
        public object? GetValue(long fid) => values.TryGetValue(fid, out var v) ? v : null;
    }

    private static CompiledFormula Compile(string expression, FormulaType? expected = null) =>
        Engine.Compile(expression, Schema, expected);

    private static string[] Errors(CompiledFormula compiled) =>
        compiled.Diagnostics.Where(d => d.Severity == FormulaSeverity.Error).Select(d => d.Message).ToArray();

    // ── typing: the field's type decides what the call is ────────────────────

    [Theory]
    [InlineData("GetFieldValue(14)", FormulaType.Number)]
    [InlineData("GetFieldValue(15)", FormulaType.Date)]
    [InlineData("GetFieldValue(16)", FormulaType.Text)]
    [InlineData("GetFieldValue(17)", FormulaType.Bool)]
    public void The_call_takes_the_type_of_the_field_it_names(string expression, FormulaType expected)
    {
        var compiled = Compile(expression);

        compiled.HasErrors.Should().BeFalse();
        compiled.ResultType.Should().Be(expected);
    }

    [Fact]
    public void A_date_field_in_a_number_formula_reports_the_type_mismatch()
        => Errors(Compile("GetFieldValue(15)", FormulaType.Number)).Should().Contain("Formula returns Date but Number is required.");

    [Fact]
    public void A_number_field_in_a_date_formula_reports_the_type_mismatch()
        => Errors(Compile("GetFieldValue(14)", FormulaType.Date)).Should().Contain("Formula returns Number but Date is required.");

    [Fact]
    public void Comparing_a_number_field_with_text_is_reported()
        => Errors(Compile("GetFieldValue(14) > \"abc\"")).Should().Contain(m => m.Contains("cannot compare Number and Text"));

    [Fact]
    public void Adding_a_text_field_to_a_number_is_reported()
        => Errors(Compile("GetFieldValue(16) + 1")).Should().Contain(m => m.Contains("cannot be applied to Text and Number"));

    [Fact]
    public void It_works_inside_other_expressions_like_a_field_reference_would()
    {
        var compiled = Compile("If(GetFieldValue(14) > 10, GetFieldValue(14) * 2, 0)", FormulaType.Number);

        compiled.HasErrors.Should().BeFalse();
    }

    // ── the ID spellings ─────────────────────────────────────────────────────

    [Theory]
    [InlineData("GetFieldValue(14)")]
    [InlineData("GetFieldValue(\"14\")")]
    [InlineData("GetFieldValue(\"fid_14\")")]
    [InlineData("GetFieldValue(\"FID_14\")")]
    [InlineData("getfieldvalue(14)")]
    public void Every_spelling_of_the_id_and_the_name_of_the_function_resolves_to_the_field(string expression)
    {
        var compiled = Compile(expression, FormulaType.Number);

        compiled.HasErrors.Should().BeFalse();
        compiled.ReferencedFieldIds.Should().Equal(14);
    }

    [Fact]
    public void The_field_counts_as_a_dependency_just_like_a_bracket_reference()
        => Compile("GetFieldValue(14) + [Quantity]").ReferencedFieldIds.Should().Equal(14);

    // ── evaluating ───────────────────────────────────────────────────────────

    [Fact]
    public void Evaluating_returns_the_fields_value_for_the_record()
    {
        var compiled = Compile("GetFieldValue(14) * 2", FormulaType.Number);
        var context = new MapContext(new() { [14] = 21m });

        var value = Engine.Evaluate(compiled, context, new EvaluationOptions());

        value.AsNumber().Should().Be(42m);
    }

    [Fact]
    public void A_blank_field_evaluates_to_null_of_its_type()
    {
        var compiled = Compile("GetFieldValue(14)", FormulaType.Number);

        var value = Engine.Evaluate(compiled, new MapContext(new()), new EvaluationOptions());

        value.IsNull.Should().BeTrue();
        value.Type.Should().Be(FormulaType.Number);
    }

    [Fact]
    public void Evaluating_gives_the_same_result_as_the_bracket_reference()
    {
        var context = new MapContext(new() { [16] = "Acme" });
        var options = new EvaluationOptions();

        var byCall = Engine.Evaluate(Compile("GetFieldValue(16)"), context, options);
        var byReference = Engine.Evaluate(Compile("[Customer]"), context, options);

        byCall.Should().Be(byReference);
    }

    // ── mistakes ─────────────────────────────────────────────────────────────

    [Fact]
    public void An_unknown_field_id_is_reported_once()
        => Errors(Compile("GetFieldValue(99)")).Should().Equal("Unknown field [fid_99].");

    [Theory]
    [InlineData("GetFieldValue(1.5)")]
    [InlineData("GetFieldValue(\"abc\")")]
    [InlineData("GetFieldValue(true)")]
    [InlineData("var text cfg = \"abc\"; GetFieldValue($cfg)")]
    public void A_literal_that_is_not_a_field_id_is_reported(string expression)
        => Errors(Compile(expression)).Should().ContainSingle().Which.Should().Contain("GetFieldValue expects a field ID like 14");

    // ── a variable set to a plain value is as good as writing the ID in the call ──

    [Fact]
    public void A_variable_set_to_a_number_resolves_the_field_like_a_literal()
    {
        var compiled = Compile("var number cfg = 14; GetFieldValue($cfg) * 0.2", FormulaType.Number);

        compiled.HasErrors.Should().BeFalse();
        compiled.ReferencedFieldIds.Should().Equal(14);
        compiled.UsesDynamicFieldReference.Should().BeFalse();
    }

    [Fact]
    public void A_variable_set_to_fid_text_resolves_the_field()
    {
        var compiled = Compile("var text cfg = \"fid_14\"; GetFieldValue($cfg) * 2", FormulaType.Number);

        compiled.HasErrors.Should().BeFalse();
        compiled.ReferencedFieldIds.Should().Equal(14);
    }

    [Fact]
    public void A_variable_that_names_a_date_field_reports_the_type_mismatch()
        => Errors(Compile("var number cfg = 15; GetFieldValue($cfg)", FormulaType.Number))
            .Should().Contain("Formula returns Date but Number is required.");

    [Fact]
    public void A_constant_can_be_passed_along_through_another_variable()
    {
        var compiled = Compile("var number a = 14; var number b = $a; GetFieldValue($b) + 1", FormulaType.Number);

        compiled.HasErrors.Should().BeFalse();
        compiled.ReferencedFieldIds.Should().Equal(14);
    }

    [Fact]
    public void A_variable_naming_an_unknown_field_is_reported()
        => Errors(Compile("var number cfg = 99; GetFieldValue($cfg)")).Should().Equal("Unknown field [fid_99].");

    [Theory]
    [InlineData("GetFieldValue()", "expects 1 argument(s) but got 0")]
    [InlineData("GetFieldValue(14, 15)", "expects 1 argument(s) but got 2")]
    public void Wrong_argument_counts_are_reported(string expression, string expectedText)
        => Errors(Compile(expression)).Should().ContainSingle().Which.Should().Contain(expectedText);

    [Fact]
    public void Other_functions_are_unaffected()
    {
        Compile("Round(1.5, 0)", FormulaType.Number).HasErrors.Should().BeFalse();
        Errors(Compile("NoSuchFunction(1)")).Should().ContainSingle().Which.Should().Contain("Unknown function");
    }
}
