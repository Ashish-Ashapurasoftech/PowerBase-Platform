using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.UnitTests.Formulas;

/// <summary>GetFieldValue(&lt;expr&gt;) where the field ID is only known per record — e.g. read from another field.</summary>
public class GetFieldValueDynamicTests
{
    private static AppField Field(int fid, string label, string typeCode) =>
        new() { Id = fid, Fid = fid, Name = $"C_{fid}", Label = label, TypeCode = typeCode };

    private static readonly AppField[] Fields =
    [
        Field(14, "Quantity", "Number"),
        Field(15, "Due Date", "Date"),
        Field(16, "Customer", "Text"),
        Field(18, "Config", "Number"),
        Field(19, "Config Text", "Text"),
        Field(20, "Price", "Number"),
    ];

    private static readonly AppFieldSchema Schema = new(Fields);
    private static readonly FormulaEngine Engine = new();

    private sealed class MapContext(Dictionary<long, object?> values) : IRecordContext
    {
        public object? GetValue(long fid) => values.TryGetValue(fid, out var v) ? v : null;
    }

    private static CompiledFormula Compile(string expression, FormulaType? expected = null) =>
        Engine.Compile(expression, Schema, expected);

    private static string[] Errors(CompiledFormula compiled) =>
        compiled.Diagnostics.Where(d => d.Severity == FormulaSeverity.Error).Select(d => d.Message).ToArray();

    private static readonly IReadOnlyDictionary<long, FormulaType> FieldTypes = Fields.ToDictionary(
        f => (long)f.Fid!.Value,
        f => f.TypeCode switch { "Number" => FormulaType.Number, "Date" => FormulaType.Date, _ => FormulaType.Text });

    private static EvaluationOptions Options(bool withFieldTypes = true) =>
        new() { FieldTypes = withFieldTypes ? FieldTypes : null };

    private static FormulaValue Run(string expression, Dictionary<long, object?> values, FormulaType? expected = null, bool withFieldTypes = true) =>
        Engine.Evaluate(Compile(expression, expected), new MapContext(values), Options(withFieldTypes));

    private static Action Fails(string expression, Dictionary<long, object?> values, FormulaType? expected = null) =>
        () => Run(expression, values, expected);

    private static readonly DateTime SomeDate = new(2026, 1, 15);

    // ── compiling ────────────────────────────────────────────────────────────

    [Fact]
    public void A_field_read_ID_compiles_without_errors_and_is_flagged_dynamic()
    {
        var compiled = Compile("GetFieldValue([Config])");

        compiled.HasErrors.Should().BeFalse();
        compiled.UsesDynamicFieldReference.Should().BeTrue();
        compiled.ReferencedFieldIds.Should().Equal(18); // only [Config] is known up front
    }

    [Fact]
    public void A_dynamic_call_is_accepted_wherever_a_value_is_needed()
    {
        Compile("GetFieldValue([Config]) * 0.2", FormulaType.Number).HasErrors.Should().BeFalse();
        Compile("GetFieldValue([Config]) > 5", FormulaType.Bool).HasErrors.Should().BeFalse();
        Compile("If(GetFieldValue([Config]) > 5, 1, 0)", FormulaType.Number).HasErrors.Should().BeFalse();
        Compile("GetFieldValue([Config]) & \"x\"", FormulaType.Text).HasErrors.Should().BeFalse();
    }

    [Fact]
    public void A_computed_ID_is_dynamic_too()
    {
        var compiled = Compile("GetFieldValue([Config] + 1)", FormulaType.Number);

        compiled.HasErrors.Should().BeFalse();
        compiled.UsesDynamicFieldReference.Should().BeTrue();
    }

    [Fact]
    public void A_variable_read_from_a_field_makes_the_call_dynamic()
    {
        var compiled = Compile("var number cfg = [Config]; GetFieldValue($cfg) * 2", FormulaType.Number);

        compiled.HasErrors.Should().BeFalse();
        compiled.UsesDynamicFieldReference.Should().BeTrue();
    }

    [Fact]
    public void An_ID_that_is_neither_a_number_nor_text_is_reported()
        => Errors(Compile("GetFieldValue([Due Date])")).Should().Equal("GetFieldValue argument 1 expects Number or Text but got Date.");

    [Fact]
    public void Formulas_without_it_are_not_flagged()
        => Compile("[Quantity] * 2").UsesDynamicFieldReference.Should().BeFalse();

    // ── running: the field is looked up per record ───────────────────────────

    [Fact]
    public void Each_record_reads_the_field_its_own_config_names()
    {
        var onQuantity = Run("GetFieldValue([Config]) * 2", new() { [18] = 14m, [14] = 21m, [20] = 5m }, FormulaType.Number);
        var onPrice = Run("GetFieldValue([Config]) * 2", new() { [18] = 20m, [14] = 21m, [20] = 5m }, FormulaType.Number);

        onQuantity.AsNumber().Should().Be(42m);
        onPrice.AsNumber().Should().Be(10m);
    }

    [Fact]
    public void An_ID_held_as_text_works_in_both_spellings()
    {
        Run("GetFieldValue([Config Text])", new() { [19] = "fid_14", [14] = 7m }).AsNumber().Should().Be(7m);
        Run("GetFieldValue([Config Text])", new() { [19] = "14", [14] = 7m }).AsNumber().Should().Be(7m);
    }

    [Fact]
    public void The_value_keeps_the_type_of_the_field_it_names()
    {
        Run("GetFieldValue([Config Text])", new() { [19] = "16", [16] = "Acme" }).AsText().Should().Be("Acme");
        Run("GetFieldValue([Config Text])", new() { [19] = "15", [15] = SomeDate }).Type.Should().Be(FormulaType.Date);
    }

    [Fact]
    public void A_blank_ID_gives_a_blank_result_not_an_error()
    {
        var result = Run("GetFieldValue([Config])", new() { [14] = 7m });

        result.IsNull.Should().BeTrue();
    }

    [Fact]
    public void A_host_that_does_not_supply_field_types_gets_blank()
    {
        var result = Run("GetFieldValue([Config])", new() { [18] = 14m, [14] = 7m }, withFieldTypes: false);

        result.IsNull.Should().BeTrue();
    }

    [Fact]
    public void An_ID_that_names_no_field_is_an_error()
        => Fails("GetFieldValue([Config])", new() { [18] = 99m })
            .Should().Throw<FormulaEvaluationException>().WithMessage("*fid_99*doesn't exist*");

    [Fact]
    public void An_ID_that_is_not_an_ID_is_an_error()
        => Fails("GetFieldValue([Config Text])", new() { [19] = "abc" })
            .Should().Throw<FormulaEvaluationException>().WithMessage("*isn't a field ID*");

    // ── running: the type is checked against how the value is used ───────────

    [Fact]
    public void A_date_where_a_number_is_needed_reports_the_same_message_the_compiler_would()
    {
        var staticMessage = Errors(Compile("GetFieldValue(15) * 2")).Single();

        Fails("GetFieldValue([Config]) * 2", new() { [18] = 15m, [15] = SomeDate })
            .Should().Throw<FormulaEvaluationException>().WithMessage(staticMessage);
    }

    [Fact]
    public void Comparing_a_number_with_text_reports_the_same_message_the_compiler_would()
    {
        var staticMessage = Errors(Compile("GetFieldValue(14) > \"abc\"")).Single();

        Fails("GetFieldValue([Config]) > \"abc\"", new() { [18] = 14m, [14] = 7m })
            .Should().Throw<FormulaEvaluationException>().WithMessage(staticMessage);
    }

    [Fact]
    public void Adding_text_to_a_number_reports_the_same_message_the_compiler_would()
    {
        var staticMessage = Errors(Compile("GetFieldValue(16) + 1")).Single();

        Fails("GetFieldValue([Config]) + 1", new() { [18] = 16m, [16] = "Acme" })
            .Should().Throw<FormulaEvaluationException>().WithMessage(staticMessage);
    }

    [Fact]
    public void The_result_type_is_checked_against_what_the_formula_must_return()
        => Fails("GetFieldValue([Config])", new() { [18] = 15m, [15] = SomeDate }, FormulaType.Number)
            .Should().Throw<FormulaEvaluationException>().WithMessage("Formula returns Date but Number is required.");

    [Fact]
    public void The_result_is_accepted_when_it_matches_what_the_formula_must_return()
        => Run("GetFieldValue([Config])", new() { [18] = 14m, [14] = 7m }, FormulaType.Number).AsNumber().Should().Be(7m);

    [Fact]
    public void A_wrong_type_given_to_a_function_is_reported_by_that_functions_own_rules()
        => Fails("Round(GetFieldValue([Config]), 0)", new() { [18] = 15m, [15] = SomeDate })
            .Should().Throw<FormulaEvaluationException>().WithMessage("*Round*Number*Date*");

    [Fact]
    public void A_wrong_type_in_a_condition_is_reported()
        => Fails("If(GetFieldValue([Config]) and true, 1, 0)", new() { [18] = 14m, [14] = 7m })
            .Should().Throw<FormulaEvaluationException>().WithMessage("*requires boolean operands*");

    [Fact]
    public void A_variable_declared_as_a_number_rejects_a_date()
        => Fails("var number x = GetFieldValue([Config]); $x + 1", new() { [18] = 15m, [15] = SomeDate })
            .Should().Throw<FormulaEvaluationException>().WithMessage("Expecting number but found date.");

    [Fact]
    public void A_matching_type_flows_through_conditions_and_functions()
    {
        Run("If(GetFieldValue([Config]) > 5, 1, 0)", new() { [18] = 14m, [14] = 7m }, FormulaType.Number).AsNumber().Should().Be(1m);
        Run("Round(GetFieldValue([Config]) * 1.55, 0)", new() { [18] = 14m, [14] = 10m }, FormulaType.Number).AsNumber().Should().Be(16m);
    }

    // ── a formula with no dynamic call is untouched by any of this ───────────

    [Fact]
    public void Ordinary_formulas_do_not_get_the_extra_run_time_checks()
    {
        var compiled = Compile("[Quantity] * 2", FormulaType.Number);

        compiled.UsesDynamicFieldReference.Should().BeFalse();
        Engine.Evaluate(compiled, new MapContext(new() { [14] = 21m }), Options()).AsNumber().Should().Be(42m);
    }

    // ── projector: evaluation order and dependencies ─────────────────────────

    private static FormulaProjector NewProjector()
    {
        var qc = Substitute.For<IQueryContext>();
        qc.UserId.Returns(0L);
        var runtime = Substitute.For<IFormulaRuntimeContext>();
        runtime.UrlRoot.Returns(string.Empty);
        return new FormulaProjector(
            new FormulaEngine(), qc, runtime,
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRecordRepository>(),
            Substitute.For<IAppRepository>());
    }

    private static AppField Formula(int fid, string label, string expression, string resultType = "Number") =>
        new() { Id = fid, Fid = fid, Name = $"C_{fid}", Label = label, TypeCode = "Formula", Settings = $"{{\"resultType\":\"{resultType}\",\"expression\":\"{expression}\"}}" };

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Row(params (int Fid, object? Value)[] values) =>
        [values.ToDictionary(v => PhysicalNaming.ColumnName(v.Fid), v => v.Value)];

    [Fact]
    public void The_projector_reads_the_field_each_record_names()
    {
        var fields = new List<AppField>
        {
            Field(14, "Quantity", "Number"), Field(18, "Config", "Number"),
            Formula(30, "Picked", "GetFieldValue([Config]) * 2"),
        };

        var result = NewProjector().Project(fields, Row((14, 21m), (18, 14m)));

        result[0][30].Should().Be(42m);
    }

    [Fact]
    public void A_dynamic_formula_sees_the_result_of_a_plain_formula_it_reads_even_when_listed_first()
    {
        var fields = new List<AppField>
        {
            Field(18, "Config", "Number"),
            Formula(30, "Picked", "GetFieldValue([Config]) + 1"),   // dynamic, listed before the formula it will read
            Formula(31, "Doubled", "[Quantity] * 2"),                 // plain
            Field(14, "Quantity", "Number"),
        };

        var result = NewProjector().Project(fields, Row((14, 5m), (18, 31m)));

        result[0][30].Should().Be(11m); // Doubled (10) + 1
    }

    [Fact]
    public void A_plain_formula_that_reads_a_dynamic_formula_sees_its_result()
    {
        var fields = new List<AppField>
        {
            Field(14, "Quantity", "Number"), Field(18, "Config", "Number"),
            Formula(31, "AfterPick", "[Picked] + 1"),                 // plain, reads the dynamic one
            Formula(30, "Picked", "GetFieldValue([Config]) * 2"),     // dynamic
        };

        var result = NewProjector().Project(fields, Row((14, 21m), (18, 14m)));

        result[0][30].Should().Be(42m);
        result[0][31].Should().Be(43m);
    }

    [Fact]
    public void A_dynamic_formula_that_names_itself_does_not_loop_or_crash()
    {
        var fields = new List<AppField>
        {
            Field(18, "Config", "Number"),
            Formula(30, "Self", "GetFieldValue([Config])"),
        };

        var result = NewProjector().Project(fields, Row((18, 30m)));

        result[0].Should().ContainKey(30);
    }

    [Fact]
    public void A_record_whose_config_gives_a_type_error_goes_blank_without_affecting_others()
    {
        var fields = new List<AppField>
        {
            Field(14, "Quantity", "Number"), Field(15, "Due Date", "Date"), Field(18, "Config", "Number"),
            Formula(30, "Picked", "GetFieldValue([Config]) * 2"),
        };
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { [PhysicalNaming.ColumnName(14)] = 21m, [PhysicalNaming.ColumnName(18)] = 14m },
            new Dictionary<string, object?> { [PhysicalNaming.ColumnName(15)] = SomeDate, [PhysicalNaming.ColumnName(18)] = 15m },
        };

        var result = NewProjector().Project(fields, rows);

        result[0][30].Should().Be(42m);
        result[1][30].Should().BeNull();
    }

    [Fact]
    public void Dependencies_are_unknown_for_a_formula_that_reads_a_field_chosen_per_record()
    {
        var fields = new List<AppField>
        {
            Field(14, "Quantity", "Number"), Field(18, "Config", "Number"),
            Formula(30, "Picked", "GetFieldValue([Config])"),
            Formula(31, "Plain", "[Quantity] * 2"),
        };
        var projector = NewProjector();

        projector.GetFormulaDependencies(fields, [30]).Should().BeNull();
        projector.GetFormulaDependencies(fields, [31]).Should().BeEquivalentTo(new long[] { 31, 14 });
    }
}
