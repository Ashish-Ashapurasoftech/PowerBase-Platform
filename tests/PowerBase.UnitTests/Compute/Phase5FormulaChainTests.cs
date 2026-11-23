using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FluentAssertions;
using PowerBase.Application.Compute;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula.Types;
using PowerBase.Infrastructure.Compute;
using Xunit;

namespace PowerBase.UnitTests.Compute;

/// <summary>Phase 5: stored formulas, chains (a stored field reading another stored field), unique formulas, search values.</summary>
public class Phase5FormulaChainTests
{
    // Dept (20) <- Emp (21, f_6 -> Dept) <- Task (22, f_6 -> Emp)
    private const long Dept = 20, Emp = 21, Task = 22;

    private static AppField F(long table, int fid, string type, string name, string? settings = null, bool unique = false, bool encrypted = false) =>
        new() { Id = table * 1000 + fid, AppTableId = table, Fid = fid, TypeCode = type, Name = name, Settings = settings, IsUnique = unique, IsEncrypted = encrypted };

    private static string Sum(long child, int target, string resultType = "Number") =>
        System.Text.Json.JsonSerializer.Serialize(new { ChildTableId = child, ReferenceFid = 6, Function = "Sum", TargetFid = target, TargetTypeCode = resultType, ResultTypeCode = resultType });

    private static string Expr(string expression) => System.Text.Json.JsonSerializer.Serialize(new { Expression = expression });

    private static readonly AppField SumOfHours = F(Emp, 11, "Summary", "Sum of Hours", Sum(Task, 8));
    private static readonly AppField SumOfSum = F(Dept, 8, "Summary", "Sum of Sum of Hours", Sum(Emp, 11));
    private static readonly AppField TaskNames = F(Emp, 15, "Summary", "Task Names Max",
        System.Text.Json.JsonSerializer.Serialize(new { ChildTableId = Task, ReferenceFid = 6, Function = "Max", TargetFid = 9, TargetTypeCode = "Text", ResultTypeCode = "Text" }));

    private static ComputeSchema Schema(params AppField[] extra)
    {
        var fields = new List<AppField>
        {
            F(Dept, 6, "Text", "Name"),
            F(Emp, 6, "Reference", "Department"), F(Emp, 7, "Number", "Salary"), F(Emp, 9, "Text", "First"), F(Emp, 10, "Text", "Last"),
            F(Emp, 16, "Text", "Secret", encrypted: true), F(Emp, 17, "Reference", "Manager"),
            F(Task, 6, "Reference", "Employee"), F(Task, 8, "Number", "Hours"), F(Task, 9, "Text", "Title"),
            SumOfHours.Clone(), SumOfSum.Clone(), TaskNames.Clone()
        };
        fields.AddRange(extra);
        return new ComputeSchema(fields, new Dictionary<long, long> { [Dept] = 1, [Emp] = 1, [Task] = 1 }, new HashSet<long>());
    }

    private static MaterializeDecision Decide(ComputeSchema schema, AppField field) =>
        new ComputeModel(schema).Decisions[new FieldKey(field.AppTableId, field.Fid!.Value)].Decision;

    // ── chains ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_summary_of_a_stored_summary_is_allowed_and_reads_its_c_column()
    {
        var d = Decide(Schema(), SumOfSum);
        d.Outcome.Should().Be(MaterializeOutcome.Allowed);
        d.Plan.Should().BeOfType<SummaryPlan>().Which.TargetIsStored.Should().BeTrue();
    }

    [Fact]
    public void Without_the_inputs_decision_a_chain_is_refused()
    {
        var schema = Schema();
        MaterializeRules.Evaluate(SumOfSum, schema).Outcome.Should().Be(MaterializeOutcome.NotAllowed);
    }

    [Fact]
    public void A_lookup_of_a_stored_number_reads_the_parents_c_column()
    {
        var lookup = F(Task, 20, "Lookup", "Employee Hours",
            System.Text.Json.JsonSerializer.Serialize(new { SourceTableId = Emp, ReferenceFid = 6, SourceFid = 11, SourceTypeCode = "Summary", ResultTypeCode = "Number" }));
        var d = Decide(Schema(lookup), lookup);
        d.Outcome.Should().Be(MaterializeOutcome.Allowed);
        ((LookupPlan)d.Plan!).SourceColumn.Should().Be("c_11");
    }

    [Fact]
    public void A_chain_over_stored_text_is_refused_because_text_is_cut_to_400_characters()
    {
        var overText = F(Dept, 9, "Summary", "Max of names",
            System.Text.Json.JsonSerializer.Serialize(new { ChildTableId = Emp, ReferenceFid = 6, Function = "Max", TargetFid = 15, TargetTypeCode = "Text", ResultTypeCode = "Text" }));
        Decide(Schema(overText), overText).Outcome.Should().Be(MaterializeOutcome.NotAllowed);
    }

    // ── formulas ──────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_formula_reading_a_stored_number_is_allowed_with_its_stored_read()
    {
        var f = F(Emp, 14, "Formula_Number", "Double hours", Expr("[Sum of Hours] * 2"));
        var plan = Decide(Schema(f), f).Plan.Should().BeOfType<FormulaPlan>().Subject;
        plan.StoredReads.Should().Equal(11);
        plan.PhysicalReads.Should().BeEmpty();
        plan.ResultType.Should().Be(FormulaType.Number);
    }

    [Theory]
    [InlineData("[Secret] & \"x\"", "encrypted")]
    [InlineData("ToText([Manager])", "Reference")]
    [InlineData("[Task Names Max] & \"!\"", "not stored")]
    [InlineData("URLRoot() & [First]", "URLRoot")]
    [InlineData("UserToName([Salary])", "UserToName")]
    [InlineData("[Nope] * 2", "compile")]
    public void Formulas_that_cannot_be_stored_exactly_are_refused(string expression, string reason)
    {
        var f = F(Emp, 30, "Formula_Text", "Bad", Expr(expression));
        var d = Decide(Schema(f), f);
        d.Outcome.Should().Be(MaterializeOutcome.NotAllowed);
        d.Reason.Should().Contain(reason);
    }

    [Fact]
    public void A_formula_using_an_app_variable_is_refused() =>
        Decide(Schema(F(Emp, 30, "Formula_Number", "Taxed", Expr("[Salary] * [var_5|Tax]"))), F(Emp, 30, "Formula_Number", "Taxed", Expr("[Salary] * [var_5|Tax]")))
            .Reason.Should().Contain("app variable");

    [Fact]
    public void A_time_formula_is_not_stored_in_this_version()
    {
        var f = F(Emp, 30, "Formula_Time", "Start", Expr("ToTimeOfDay(\"09:00\")"));
        Decide(Schema(f), f).Outcome.Should().Be(MaterializeOutcome.NotAllowed);
    }

    [Fact]
    public void A_unique_formula_may_only_read_its_own_record()
    {
        var sameRecord = F(Emp, 13, "Formula_Text", "Full name", Expr("[First] & \" \" & [Last]"), unique: true);
        var chained = F(Emp, 14, "Formula_Number", "Double hours", Expr("[Sum of Hours] * 2"), unique: true);
        var schema = Schema(sameRecord, chained);

        Decide(schema, sameRecord).Plan.Should().BeOfType<FormulaPlan>().Which.IsUnique.Should().BeTrue();
        Decide(schema, chained).Reason.Should().Contain("same record");
    }

    [Fact]
    public void Stored_types_of_formula_results()
    {
        FormulaAnalysis.StoredSqlType(FormulaType.Text).Should().Be("NVARCHAR(400)");
        FormulaAnalysis.StoredSqlType(FormulaType.Number).Should().Be("DECIMAL(38,8)");
        FormulaAnalysis.StoredSqlType(FormulaType.Date).Should().Be("DATE");
        FormulaAnalysis.StoredSqlType(FormulaType.DateTime).Should().Be("DATETIME2(0)");
        FormulaAnalysis.StoredSqlType(FormulaType.Bool).Should().Be("BIT");
        FormulaAnalysis.StoredSqlType(FormulaType.Time).Should().BeNull();
        SqlColumnType.IsAllowed("DECIMAL(38,8)").Should().BeTrue();
        SqlColumnType.IsAllowed("DATETIME2(0)").Should().BeTrue();
    }

    [Fact]
    public void Formula_value_type_for_reads()
    {
        MaterializedReadFields.ValueTypeOf(F(Emp, 30, "Formula_Number", "n", Expr("1"))).Should().Be("Number");
        MaterializedReadFields.ValueTypeOf(F(Emp, 30, "Formula_Text", "t", Expr("\"a\""))).Should().Be("Text");
        MaterializedReadFields.ValueTypeOf(F(Emp, 30, "Formula_Bool", "b", Expr("true"))).Should().Be("Boolean");
    }

    // ── evaluator: the same engine and contexts the API uses ─────────────────────────────────────────────

    [Fact]
    public void The_evaluator_computes_from_the_row_and_the_seeded_stored_inputs()
    {
        var f = F(Emp, 14, "Formula_Number", "Mix", Expr("[Salary] * 12 + [Sum of Hours]"));
        var schema = Schema(f);
        var plan = (FormulaPlan)Decide(schema, f).Plan!;
        var rows = new List<IReadOnlyDictionary<string, object?>>
        {
            new Dictionary<string, object?> { ["Id"] = 1L, ["f_7"] = 100m, ["c_11"] = 5m },
            new Dictionary<string, object?> { ["Id"] = 2L, ["f_7"] = null, ["c_11"] = 3m },
        };

        var values = new StoredFormulaEvaluator().Evaluate(plan, schema.FieldsOf(Emp), rows);

        values[0].Should().Be((1L, (object?)1205m));
        values[1].Id.Should().Be(2);
    }

    [Fact]
    public void Stored_values_are_typed_and_text_is_cut()
    {
        StoredFormulaEvaluator.ToStored(new string('a', 450), FormulaType.Text).Should().BeOfType<string>().Which.Length.Should().Be(400);
        StoredFormulaEvaluator.ToStored("2026-11-19", FormulaType.Date).Should().Be(new DateTime(2026, 11, 19));
        StoredFormulaEvaluator.ToStored(12.5m, FormulaType.Number).Should().Be(12.5m);
        StoredFormulaEvaluator.ToStored(null, FormulaType.Number).Should().BeNull();
    }

    [Fact]
    public void Unique_values_are_compared_trimmed_and_blank_counts_as_no_value()
    {
        StoredFormulaEvaluator.Normalize("  John Smith ").Should().Be("John Smith");
        StoredFormulaEvaluator.Normalize("   ").Should().BeNull();
        StoredFormulaEvaluator.Normalize(5m).Should().Be(5m);
    }

    // ── writer and builder ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_chained_summary_aggregates_the_childrens_stored_column()
    {
        var plan = (SummaryPlan)Decide(Schema(), SumOfSum).Plan!;
        var parts = MaterializedColumnWriter.SummaryBatchParts(plan, "DECIMAL(38,4)", Schema().FieldsByFid(Emp), new DynamicParameters());
        parts.From.Should().Contain("c.c_11 AS TargetValue").And.Contain("FROM data.t_21 c");
    }

    [Fact]
    public void A_formula_batch_updates_from_the_values_the_engine_produced()
    {
        var f = F(Emp, 12, "Formula_Number", "Annual", Expr("[Salary] * 12"));
        var plan = (FormulaPlan)Decide(Schema(f), f).Plan!;
        var sql = MaterializedColumnWriter.UpdateSql("c_12", "DECIMAL(38,8)", MaterializedColumnWriter.FormulaBatchParts(plan));
        sql.Should().Contain("JOIN #cv v ON v.Id = x.Id").And.Contain("OUTPUT inserted.Id").And.Contain("fx.MaterializeVersion = @version");
    }

    [Fact]
    public void A_chained_field_waits_for_the_stored_fields_it_reads()
    {
        var plan = (SummaryPlan)Decide(Schema(), SumOfSum).Plan!;
        MaterializeBuilder.StoredInputs(plan).Should().Equal(new FieldKey(Emp, 11));
        var f = F(Emp, 14, "Formula_Number", "Double hours", Expr("[Sum of Hours] * 2"));
        MaterializeBuilder.StoredInputs(Decide(Schema(f), f).Plan!).Should().Equal(new FieldKey(Emp, 11));
    }

    [Fact]
    public void Inputs_are_decided_before_what_reads_them()
    {
        var model = new ComputeModel(Schema());
        var order = model.Graph.TopologicalOrder.ToList();
        order.IndexOf(new FieldKey(Emp, 11)).Should().BeLessThan(order.IndexOf(new FieldKey(Dept, 8)));
    }

    [Fact]
    public void A_stored_value_change_queues_its_stored_dependents()
    {
        var schema = Schema();
        foreach (var field in schema.Fields.Where(x => x.Fid is 11 or 8 && x.TypeCode == "Summary"))
        {
            field.MaterializeMode = MaterializeModes.On;
            field.MaterializeState = MaterializeStates.Ready;
        }
        var model = new ComputeModel(schema);
        model.Graph.DependentsOf(new FieldKey(Emp, 11)).Should().Contain(new FieldKey(Dept, 8));
    }
}
