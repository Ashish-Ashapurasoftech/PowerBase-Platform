using FluentAssertions;
using PowerBase.Application.Imports;
using PowerBase.Application.Reports;
using PowerBase.Formula;
using PowerBase.Formula.Evaluation;

namespace PowerBase.Imports.Tests;

/// <summary>A variable or a formula on either side of an import filter condition: worked out once per run, then the condition reads as an ordinary one.</summary>
public class ImportFilterOperandsTests
{
    private static readonly EvaluationOptions Clock = new() { UtcNow = new DateTime(2026, 3, 15, 10, 30, 0, DateTimeKind.Utc) };

    private static ImportVariable Fixed(string name, string value) => new() { Name = name, Value = value };
    private static ImportVariable Calc(string name, string formula) => new() { Name = name, Kind = ImportVariables.Formula, Formula = formula };

    private static ImportDefinitionConfig Config(FilterCondition condition, params ImportVariable[] variables) => new()
    {
        Conditions = new FilterGroup { Nodes = [new FilterNode { Condition = condition }] }, Variables = [.. variables]
    };

    private static FilterCondition Left(string kind, string left, string op, string? value = null, string? mode = null, long? valueField = null) =>
        new() { FieldId = 0, LeftKind = kind, LeftValue = left, Operator = op, Value = value, ValueMode = mode, ValueFieldId = valueField };

    private static FilterCondition Applied(ImportDefinitionConfig cfg)
    {
        var error = ImportFilterOperands.TryApply(cfg, new FormulaEngine(), Clock, out var applied);
        error.Should().BeNull();
        return applied.Conditions!.Nodes.Single().Condition!;
    }

    private static string? ErrorOf(ImportDefinitionConfig cfg) => ImportFilterOperands.TryApply(cfg, new FormulaEngine(), Clock, out _);

    // ---- nothing to do ----

    [Fact]
    public void An_import_without_variable_or_formula_operands_is_returned_untouched()
    {
        var cfg = Config(new FilterCondition { FieldId = 6, Operator = "eq", Value = "x" });

        ImportFilterOperands.Uses(cfg).Should().BeFalse();
        ImportFilterOperands.TryApply(cfg, new FormulaEngine(), Clock, out var applied).Should().BeNull();
        applied.Should().BeSameAs(cfg);
    }

    // ---- a formula on the right ----

    [Fact]
    public void A_formula_on_the_right_becomes_the_value_it_gives()
    {
        var c = Applied(Config(new FilterCondition { FieldId = 8, Operator = "gt", Value = "Today() - Days(30)", ValueMode = "formula" }));

        c.FieldId.Should().Be(8);
        c.ValueMode.Should().BeNull();
        c.Value.Should().StartWith("2026-02-13", "thirty days before the run's clock");
    }

    [Fact]
    public void A_formula_on_the_right_can_use_a_variable()
    {
        var c = Applied(Config(new FilterCondition { FieldId = 7, Operator = "gte", Value = "[var_base] * 2", ValueMode = "formula" }, Calc("var_base", "5 + 5")));

        c.Value.Should().Be("20");
    }

    // ---- a variable or a formula on the left, against a field ----

    [Theory]
    [InlineData("gt", "lt")]
    [InlineData("gte", "lte")]
    [InlineData("lt", "gt")]
    [InlineData("lte", "gte")]
    [InlineData("eq", "eq")]
    [InlineData("ne", "ne")]
    public void A_variable_on_the_left_of_a_field_swaps_sides_and_turns_the_operator_round(string op, string turned)
    {
        var c = Applied(Config(Left("variable", "var_limit", op, mode: "field", valueField: 7), Fixed("var_limit", "100")));

        c.FieldId.Should().Be(7);
        c.Operator.Should().Be(turned);
        c.Value.Should().Be("100");
        c.ValueMode.Should().BeNull();
        c.LeftKind.Should().BeNull("the rewritten condition is an ordinary one");
    }

    [Fact]
    public void A_formula_on_the_left_of_a_field_is_worked_out_and_swapped_too()
    {
        var c = Applied(Config(Left("formula", "10 * 3", "lt", mode: "field", valueField: 7)));

        c.FieldId.Should().Be(7);
        c.Operator.Should().Be("gt", "30 is less than the field means the field is greater than 30");
        c.Value.Should().Be("30");
    }

    [Fact]
    public void A_constant_on_the_left_cannot_contain_or_start_with_a_field_so_it_is_refused_with_a_reason()
    {
        var error = ErrorOf(Config(Left("variable", "var_text", "contains", mode: "field", valueField: 6), Fixed("var_text", "hello")));

        error.Should().Contain("on the left").And.Contain("is equal to");
    }

    // ---- a constant against another constant ----

    [Theory]
    [InlineData("eq", "5", true)]
    [InlineData("eq", "6", false)]
    [InlineData("gt", "4", true)]
    [InlineData("gt", "5", false)]
    [InlineData("lte", "5", true)]
    [InlineData("lt", "5", false)]
    [InlineData("ne", "6", true)]
    public void A_variable_against_a_typed_value_is_decided_once_for_every_row(string op, string value, bool holds)
    {
        var c = Applied(Config(Left("variable", "var_n", op, value), Calc("var_n", "5")));

        c.FieldId.Should().Be(3, "an always / never condition is on the Record ID#, which every source has");
        c.Operator.Should().Be(holds ? "gt" : "lt");
        c.Value.Should().Be("0");
    }

    [Theory]
    [InlineData("contains", "ell", true)]
    [InlineData("contains", "xyz", false)]
    [InlineData("startsWith", "he", true)]
    [InlineData("notContains", "xyz", true)]
    public void Text_operators_work_between_two_constants(string op, string value, bool holds)
    {
        var c = Applied(Config(Left("variable", "var_t", op, value), Fixed("var_t", "hello")));

        c.Operator.Should().Be(holds ? "gt" : "lt");
    }

    [Fact]
    public void A_variable_against_a_variable_given_as_a_value_is_compared_after_the_value_is_filled_in()
    {
        // {{var_b}} on the right is already replaced by the variable's text before the operands are worked out.
        var cfg = Config(Left("variable", "var_a", "eq", "{{var_b}}"), Fixed("var_a", "same"), Fixed("var_b", "same"));
        cfg = ImportVariables.Apply(cfg, ImportVariables.Evaluate(cfg.Variables, new FormulaEngine(), Clock).Values);

        Applied(cfg).Operator.Should().Be("gt", "the two variables are equal");
    }

    [Fact]
    public void A_formula_against_a_formula_is_compared_with_the_row_filters_own_rules()
    {
        var c = Applied(Config(Left("formula", "2 + 2", "eq", "4")));

        c.Operator.Should().Be("gt");
    }

    // ---- what is wrong ----

    [Fact]
    public void An_unknown_variable_on_the_left_is_named()
    {
        ErrorOf(Config(Left("variable", "var_missing", "eq", "1"))).Should().Contain("Unknown variable var_missing");
    }

    [Fact]
    public void A_formula_that_cannot_work_out_says_it_is_a_filter_formula_and_that_fields_are_not_allowed()
    {
        var error = ErrorOf(Config(new FilterCondition { FieldId = 7, Operator = "gt", Value = "[Salary] * 2", ValueMode = "formula" }));

        error.Should().StartWith("A filter formula has an error").And.Contain("not the fields of the table");
        error.Should().NotContain("var___op");
    }

    [Fact]
    public void Ask_the_user_is_not_available_with_a_constant_on_the_left()
    {
        ErrorOf(Config(Left("variable", "var_n", "eq", mode: "ask"), Fixed("var_n", "1"))).Should().Contain("Ask the user");
    }

    [Fact]
    public void Every_filter_of_the_import_is_rewritten_the_import_the_table_the_sheets_and_the_extra_tables()
    {
        var cond = () => new FilterGroup { Nodes = [new FilterNode { Condition = Left("formula", "1 + 1", "eq", "2") }] };
        var cfg = new ImportDefinitionConfig
        {
            Conditions = cond(), TableConditions = cond(),
            SheetMappings = [new ImportSheetMapping { Sheet = "s", Conditions = cond() }],
            AdditionalTargets = [new ImportTargetConfig { Conditions = cond(), SheetMappings = [new ImportSheetMapping { Sheet = "t", Conditions = cond() }] }]
        };

        ImportFilterOperands.TryApply(cfg, new FormulaEngine(), Clock, out var applied).Should().BeNull();

        var groups = new[] { applied.Conditions, applied.TableConditions, applied.SheetMappings[0].Conditions, applied.AdditionalTargets[0].Conditions, applied.AdditionalTargets[0].SheetMappings[0].Conditions };
        groups.Should().OnlyContain(g => g!.Nodes[0].Condition!.LeftKind == null && g.Nodes[0].Condition!.FieldId == 3);
    }

    [Fact]
    public void The_left_side_survives_a_save_and_load()
    {
        var cfg = Config(Left("variable", "var_a", "eq", "1"));

        var back = ImportJson.Deserialize<ImportDefinitionConfig>(ImportJson.Serialize(cfg))!;

        back.Conditions!.Nodes[0].Condition!.LeftKind.Should().Be("variable");
        back.Conditions.Nodes[0].Condition!.LeftValue.Should().Be("var_a");
    }
}
