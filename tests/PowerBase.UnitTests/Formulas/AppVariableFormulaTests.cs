using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Apps.Commands.CreateAppVariable;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula;
using PowerBase.Formula.Diagnostics;
using PowerBase.Formula.Evaluation;
using PowerBase.Formula.Types;

namespace PowerBase.UnitTests.Formulas;

/// <summary>App variables in formulas: <c>[Tax]</c> reads the variable (Quickbase's rule — it wins over a field of the
/// same name), always as text, and a saved formula names it by id so later changes can't re-point it.</summary>
public class AppVariableFormulaTests
{
    private static AppField Field(int fid, string label, string typeCode = "Number") =>
        new() { Id = fid, Fid = fid, Name = $"C_{fid}", Label = label, TypeCode = typeCode };

    private static AppVariable Variable(long id, string name, string value) => new() { Id = id, Name = name, Value = value };

    // A field "Quantity" and a field "Tax" (so "Tax" is both a field and a variable); variables Tax and Rate.
    private static readonly AppField[] Fields = [Field(14, "Quantity"), Field(20, "Tax")];
    private static readonly AppVariable[] Variables = [Variable(5, "Tax", "0.18"), Variable(6, "Rate", "5")];

    private static readonly AppFieldSchema FieldSchema = new(Fields);
    private static readonly AppVariableSchema VariableSchema = new(Variables);
    private static readonly FormulaEngine Engine = new();

    private sealed class MapContext(Dictionary<long, object?> values) : IRecordContext
    {
        public object? GetValue(long fid) => values.TryGetValue(fid, out var v) ? v : null;
    }

    private static CompiledFormula Compile(string expression, FormulaType? expected = null, bool withVariables = true) =>
        Engine.Compile(expression, FieldSchema, expected, variableSchema: withVariables ? VariableSchema : null);

    private static string[] Errors(CompiledFormula c) =>
        c.Diagnostics.Where(d => d.Severity == FormulaSeverity.Error).Select(d => d.Message).ToArray();

    private static FormulaDiagnostic[] Warnings(CompiledFormula c) =>
        c.Diagnostics.Where(d => d.Severity == FormulaSeverity.Warning).ToArray();

    // ── a variable is text ───────────────────────────────────────────────────

    [Fact]
    public void A_variable_is_a_text_value_with_no_field_dependency()
    {
        var compiled = Compile("[Rate]");

        compiled.HasErrors.Should().BeFalse();
        compiled.ResultType.Should().Be(FormulaType.Text);
        compiled.ReferencedFieldIds.Should().BeEmpty();
    }

    [Fact]
    public void Evaluating_gives_the_variables_value_as_text()
    {
        var value = Engine.Evaluate(Compile("[Rate]"), new MapContext(new()), new EvaluationOptions());

        value.Type.Should().Be(FormulaType.Text);
        value.AsText().Should().Be("5");
    }

    [Fact]
    public void Using_a_variable_as_a_number_without_converting_it_is_the_usual_type_error()
        => Errors(Compile("[Rate] * 2", FormulaType.Number)).Should().Contain("Operator '*' cannot be applied to Text and Number.");

    [Fact]
    public void A_formula_returning_a_variable_where_a_number_is_needed_reports_the_type()
        => Errors(Compile("[Rate]", FormulaType.Number)).Should().Contain("Formula returns Text but Number is required.");

    [Fact]
    public void A_variable_converted_with_ToNumber_works_in_arithmetic()
    {
        var compiled = Compile("ToNumber([Rate]) * 2", FormulaType.Number);

        compiled.HasErrors.Should().BeFalse();
        Engine.Evaluate(compiled, new MapContext(new()), new EvaluationOptions()).AsNumber().Should().Be(10m);
    }

    [Fact]
    public void A_variable_can_be_mixed_with_fields()
    {
        var compiled = Compile("[Quantity] * ToNumber([Rate])", FormulaType.Number);

        compiled.HasErrors.Should().BeFalse();
        compiled.ReferencedFieldIds.Should().Equal(14);
        Engine.Evaluate(compiled, new MapContext(new() { [14] = 4m }), new EvaluationOptions()).AsNumber().Should().Be(20m);
    }

    [Fact]
    public void A_variable_name_is_case_insensitive()
        => Compile("[rate]").HasErrors.Should().BeFalse();

    [Fact]
    public void Without_variables_a_variable_name_is_an_unknown_field_as_before()
        => Errors(Compile("[Rate]", withVariables: false)).Should().Contain("Unknown field [Rate].");

    // ── a variable and a field with the same name ────────────────────────────

    [Fact]
    public void The_variable_wins_over_a_field_of_the_same_name()
    {
        var compiled = Compile("ToNumber([Tax]) * 0.2", FormulaType.Number);

        compiled.HasErrors.Should().BeFalse();
        compiled.ReferencedFieldIds.Should().BeEmpty(); // it read the variable, not field 20
        Engine.Evaluate(compiled, new MapContext(new() { [20] = 999m }), new EvaluationOptions()).AsNumber().Should().Be(0.036m);
    }

    [Fact]
    public void When_both_exist_a_note_says_which_one_is_used_and_the_formula_stays_valid()
    {
        var compiled = Compile("ToNumber([Tax])");

        compiled.HasErrors.Should().BeFalse();
        var warning = Warnings(compiled).Should().ContainSingle().Subject;
        warning.Message.Should().Be("Note: [Tax] is both an App Variable and a Field on this table. This formula will use the App Variable.");
        warning.Code.Should().Be(FormulaErrorCode.AmbiguousReference);
    }

    [Fact]
    public void The_note_points_at_the_reference_it_is_about()
    {
        const string expression = "ToNumber([Tax])";
        var warning = Warnings(Compile(expression)).Single();

        expression.Substring(warning.Span.Start, warning.Span.Length).Should().Be("[Tax]");
    }

    [Fact]
    public void There_is_no_note_when_only_the_variable_exists()
        => Warnings(Compile("[Rate]")).Should().BeEmpty();

    [Fact]
    public void The_field_is_still_reachable_by_its_id()
    {
        var compiled = Compile("[fid_20] * 2", FormulaType.Number);

        compiled.HasErrors.Should().BeFalse();
        compiled.ReferencedFieldIds.Should().Equal(20);
        Warnings(compiled).Should().BeEmpty();
    }

    // ── the by-id spelling a saved formula uses ──────────────────────────────

    [Theory]
    [InlineData("[var_5]")]
    [InlineData("[var_5|Tax]")]
    [InlineData("[VAR_5|anything the name used to be]")]
    public void A_variable_named_by_id_resolves_with_no_note_even_when_a_field_has_that_name(string expression)
    {
        var compiled = Compile(expression);

        compiled.HasErrors.Should().BeFalse();
        Warnings(compiled).Should().BeEmpty();
        Engine.Evaluate(compiled, new MapContext(new()), new EvaluationOptions()).AsText().Should().Be("0.18");
    }

    [Fact]
    public void An_id_that_names_no_variable_is_an_unknown_field()
        => Errors(Compile("[var_99]")).Should().Contain(m => m.Contains("var_99"));

    [Fact]
    public void A_saved_reference_keeps_working_after_the_variable_is_renamed()
    {
        var renamed = new AppVariableSchema([Variable(6, "Interest", "5")]);

        var compiled = Engine.Compile("[var_6|Rate]", FieldSchema, variableSchema: renamed);

        compiled.HasErrors.Should().BeFalse();
        Engine.Evaluate(compiled, new MapContext(new()), new EvaluationOptions()).AsText().Should().Be("5");
    }

    // ── the lookup itself ────────────────────────────────────────────────────

    [Fact]
    public void The_schema_resolves_by_name_or_by_id_and_trims()
    {
        VariableSchema.TryResolve(" tax ", out var byName).Should().BeTrue();
        VariableSchema.TryResolve("var_6", out var byId).Should().BeTrue();

        byName.Id.Should().Be(5);
        byId.Name.Should().Be("Rate");
        VariableSchema.TryResolve("Nope", out _).Should().BeFalse();
    }

    // ── a saved formula is read with the variables loaded for its app ─────────

    private static FormulaProjector NewProjector(IAppVariableRepository? variables)
    {
        var qc = Substitute.For<IQueryContext>();
        qc.UserId.Returns(0L);
        var runtime = Substitute.For<IFormulaRuntimeContext>();
        runtime.UrlRoot.Returns(string.Empty);
        var tables = Substitute.For<IAppTableRepository>();
        tables.GetByIdAsync(Arg.Any<long>(), Arg.Any<CancellationToken>()).Returns(new AppTable { AppId = 7 });
        return new FormulaProjector(
            new FormulaEngine(), qc, runtime, tables, Substitute.For<IAppFieldRepository>(), Substitute.For<IRecordRepository>(),
            Substitute.For<IAppRepository>(), variables);
    }

    private static AppField FormulaField(int fid, string typeCode, string expression) =>
        new() { Id = fid, Fid = fid, Name = $"C_{fid}", Label = $"F{fid}", TypeCode = typeCode, Settings = $"{{\"expression\":\"{expression}\"}}", AppTableId = 3 };

    private static IReadOnlyList<IReadOnlyDictionary<string, object?>> Row(params (int Fid, object? Value)[] values) =>
        [values.ToDictionary(v => PhysicalNaming.ColumnName(v.Fid), v => v.Value)];

    [Fact]
    public void The_projector_evaluates_a_saved_variable_reference_with_the_apps_variables()
    {
        var repo = Substitute.For<IAppVariableRepository>();
        repo.ListAsync(7, Arg.Any<CancellationToken>()).Returns(Variables);
        var fields = new List<AppField>
        {
            Field(14, "Quantity"),
            FormulaField(30, "Formula_Number", "ToNumber([var_5|Tax]) * [fid_14|Quantity]"),
            FormulaField(31, "Formula_Text", "[var_6|Rate]"),
        };

        var result = NewProjector(repo).Project(fields, Row((14, 100m)));

        result[0][30].Should().Be(18m);
        result[0][31].Should().Be("5");
    }

    [Fact]
    public void A_table_whose_formulas_use_no_variable_never_asks_for_them()
    {
        var repo = Substitute.For<IAppVariableRepository>();
        var fields = new List<AppField> { Field(14, "Quantity"), FormulaField(30, "Formula_Number", "[fid_14|Quantity] * 2") };

        NewProjector(repo).Project(fields, Row((14, 4m)))[0][30].Should().Be(8m);

        repo.DidNotReceive().ListAsync(Arg.Any<long>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public void A_variable_formula_with_no_variable_repository_fails_soft_to_blank()
    {
        var fields = new List<AppField> { FormulaField(30, "Formula_Text", "[var_6|Rate]") };

        NewProjector(null).Project(fields, Row())[0][30].Should().BeNull();
    }

    // ── what a variable may be called ────────────────────────────────────────

    [Theory]
    [InlineData("Tax", true)]
    [InlineData("Tax Rate 2024", true)]
    [InlineData("Tax [old]", false)]
    [InlineData("Tax]", false)]
    [InlineData("Back\\slash", false)]
    [InlineData("var_5", false)]
    [InlineData("VAR_12", false)]
    [InlineData("fid_14", false)]
    [InlineData("variable_5", true)]
    public void A_variable_name_is_checked_when_it_is_created(string name, bool allowed)
        => new CreateAppVariableCommandValidator().Validate(new CreateAppVariableCommand(Guid.NewGuid(), name, "1", null))
            .IsValid.Should().Be(allowed);
}
