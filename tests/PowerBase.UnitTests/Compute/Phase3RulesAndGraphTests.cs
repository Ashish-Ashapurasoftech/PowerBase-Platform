using System;
using System.Collections.Generic;
using System.Linq;
using Dapper;
using FluentAssertions;
using PowerBase.Application.Compute;
using PowerBase.Application.Reports;
using PowerBase.ComputeWorker;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Compute;
using Xunit;

namespace PowerBase.UnitTests.Compute;

/// <summary>Phase 3: what may be stored (every "not allowed" rule), the dependency graph, the set-based SQL, the guard and the scheduling.</summary>
public class Phase3RulesAndGraphTests
{
    // Tables: 10 = Department (parent), 11 = Employee (child, f_6 -> Department). App 1 plain, app 2 encrypted (table 20).
    private const long Dept = 10, Emp = 11, Secret = 20;

    private static AppField F(long table, int fid, string type, string? settings = null, bool encrypted = false, long id = 0) =>
        new() { Id = id == 0 ? table * 1000 + fid : id, AppTableId = table, Fid = fid, TypeCode = type, Name = $"t{table}f{fid}", Settings = settings, IsEncrypted = encrypted };

    private static string SummaryJson(string function, int? target = 7, string? filter = null, long child = Emp, string? subField = null) =>
        System.Text.Json.JsonSerializer.Serialize(new { ChildTableId = child, ReferenceFid = 6, Function = function, TargetFid = target, FilterTree = filter, TargetSubField = subField });

    private static string LookupJson(int sourceFid = 6, long parent = Dept, string? subField = null) =>
        System.Text.Json.JsonSerializer.Serialize(new { SourceTableId = parent, ReferenceFid = 6, SourceFid = sourceFid, SourceSubField = subField });

    private static string Filter(string op = "eq", string? mode = null, long fid = 9, string? value = "x", long? valueFid = null) =>
        System.Text.Json.JsonSerializer.Serialize(new FilterGroup
        {
            Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = fid, Operator = op, Value = value, ValueMode = mode, ValueFieldId = valueFid } }]
        });

    private static ComputeSchema Schema(params AppField[] extra)
    {
        var fields = new List<AppField>
        {
            F(Dept, 6, "Text"), F(Dept, 7, "MultiSelect"), F(Dept, 9, "Summary", SummaryJson("Sum")), F(Dept, 10, "Text", encrypted: true),
            F(Emp, 6, "Reference"), F(Emp, 7, "Number"), F(Emp, 9, "Text"), F(Emp, 10, "User"), F(Emp, 11, "Formula"), F(Emp, 12, "Text", encrypted: true),
            F(Emp, 13, "Reference"), F(Emp, 14, "Address"),
            F(Secret, 6, "Reference"), F(Secret, 7, "Number"), F(Secret, 8, "Text")
        };
        fields.AddRange(extra);
        return new ComputeSchema(fields, new Dictionary<long, long> { [Dept] = 1, [Emp] = 1, [Secret] = 2 }, new HashSet<long> { 2 });
    }

    private static MaterializeDecision Decide(AppField field, ComputeSchema? schema = null) =>
        MaterializeRules.Evaluate(field, schema ?? Schema(field));

    // ── allowed ────────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void A_plain_lookup_is_allowed_with_a_plan()
    {
        var d = Decide(F(Emp, 20, "Lookup", LookupJson()));
        d.Outcome.Should().Be(MaterializeOutcome.Allowed);
        d.Plan.Should().BeEquivalentTo(new LookupPlan(Emp, 20, Emp * 1000 + 20, 6, Dept, 6, "f_6"));
    }

    [Fact]
    public void A_lookup_of_a_system_field_reads_its_column()
    {
        var d = Decide(F(Emp, 20, "Lookup", LookupJson(sourceFid: 1)));
        ((LookupPlan)d.Plan!).SourceColumn.Should().Be("CreatedOn");
    }

    [Theory]
    [InlineData("Sum"), InlineData("Avg"), InlineData("Min"), InlineData("Max"), InlineData("DistinctCount")]
    public void Plain_summaries_are_allowed(string function) =>
        Decide(F(Dept, 20, "Summary", SummaryJson(function))).Outcome.Should().Be(MaterializeOutcome.Allowed);

    [Theory]
    [InlineData("Count"), InlineData("Exists")]
    public void Count_and_exists_need_no_target(string function) =>
        Decide(F(Dept, 20, "Summary", SummaryJson(function, target: null))).Outcome.Should().Be(MaterializeOutcome.Allowed);

    [Fact]
    public void A_summary_with_fixed_criteria_is_allowed_and_keeps_them()
    {
        var d = Decide(F(Dept, 20, "Summary", SummaryJson("Count", null, Filter("eq", "literal"))));
        d.Outcome.Should().Be(MaterializeOutcome.Allowed);
        ((SummaryPlan)d.Plan!).Filter!.Nodes.Should().HaveCount(1);
    }

    [Fact]
    public void Criteria_comparing_two_child_fields_are_fixed() =>
        Decide(F(Dept, 20, "Summary", SummaryJson("Count", null, Filter("gt", "field", 7, null, 7)))).Outcome.Should().Be(MaterializeOutcome.Allowed);

    // ── every "not allowed" rule ───────────────────────────────────────────────────────────────────────

    [Fact]
    public void Not_allowed_without_a_fid() =>
        Decide(new AppField { Id = 1, AppTableId = Emp, TypeCode = "Lookup" }).Outcome.Should().Be(MaterializeOutcome.NotAllowed);

    [Theory]
    [InlineData("ReportLink"), InlineData("ActionButton"), InlineData("Text")]
    public void Not_allowed_for_types_with_nothing_to_store(string type) =>
        Decide(F(Emp, 30, type)).Outcome.Should().Be(MaterializeOutcome.NotAllowed);

    // ── encrypted apps: stored, the worker decrypts what it reads (plan.DecryptAppId) and keeps the value plain ──

    [Fact]
    public void A_field_of_an_encrypted_app_is_stored()
    {
        var d = Decide(F(Secret, 30, "Summary", SummaryJson("Sum", child: Emp)));
        d.Outcome.Should().Be(MaterializeOutcome.Allowed);
        d.Plan!.DecryptAppId.Should().BeNull();   // it sums a Number of a plain app: nothing to decrypt
    }

    [Fact]
    public void A_field_flagged_encrypted_is_still_stored() =>
        Decide(F(Emp, 30, "Lookup", LookupJson(), encrypted: true)).Outcome.Should().Be(MaterializeOutcome.Allowed);

    [Fact]
    public void Not_allowed_in_a_dependency_cycle()
    {
        var field = F(Emp, 30, "Lookup", LookupJson());
        MaterializeRules.Evaluate(field, Schema(field), new HashSet<FieldKey> { new(Emp, 30) })
            .Reason.Should().Contain("cycle");
    }

    [Theory]
    [InlineData("Today()"), InlineData("Now()"), InlineData("User()"), InlineData("UserRoles()"), InlineData("GetRecords(\"{3.EX.1}\")"),
     InlineData("GetRecord(1)"), InlineData("GetRecordByUniqueField(1, 2)"), InlineData("SumValues(x)"), InlineData("GetFieldValues(x)"),
     InlineData("If([Due] < today (), 1, 0)")]
    public void Formulas_with_moving_values_are_not_allowed(string expression)
    {
        var d = Decide(F(Emp, 30, "Formula_Number", System.Text.Json.JsonSerializer.Serialize(new { Expression = expression })));
        d.Outcome.Should().Be(MaterializeOutcome.NotAllowed);
    }

    [Fact]
    public void A_plain_formula_is_allowed_with_what_it_reads()
    {
        var d = Decide(F(Emp, 30, "Formula_Number", "{\"Expression\":\"[t11f7] * 12\"}"));
        d.Outcome.Should().Be(MaterializeOutcome.Allowed);
        var plan = d.Plan.Should().BeOfType<FormulaPlan>().Subject;
        plan.PhysicalReads.Should().Equal(7);
        plan.StoredReads.Should().BeEmpty();
    }

    [Fact]
    public void A_field_name_that_only_contains_now_is_not_a_call() =>
        Decide(F(Emp, 30, "Formula_Text", "{\"Expression\":\"\\\"Known issues: \\\" & [t11f9]\"}")).Outcome.Should().Be(MaterializeOutcome.Allowed);

    [Fact]
    public void Lookup_with_incomplete_settings_is_not_allowed() =>
        Decide(F(Emp, 30, "Lookup", "{\"SourceTableId\":10}")).Outcome.Should().Be(MaterializeOutcome.NotAllowed);

    [Fact]
    public void Lookup_of_an_address_part_is_not_allowed() =>
        Decide(F(Emp, 30, "Lookup", LookupJson(subField: "city"))).Outcome.Should().Be(MaterializeOutcome.NotAllowed);

    [Fact]
    public void Lookup_through_a_missing_reference_is_not_allowed()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(new { SourceTableId = Dept, ReferenceFid = 99, SourceFid = 6 });
        Decide(F(Emp, 30, "Lookup", json)).Reason.Should().Contain("reference");
    }

    [Fact]
    public void Lookup_of_a_calculated_field_is_not_allowed_yet() =>
        Decide(F(Emp, 30, "Lookup", LookupJson(sourceFid: 9))).Reason.Should().Contain("calculated");

    [Fact]
    public void Lookup_of_an_encrypted_field_is_decrypted_with_the_parent_app_key()
    {
        var d = Decide(F(Emp, 30, "Lookup", LookupJson(sourceFid: 10)));
        d.Outcome.Should().Be(MaterializeOutcome.Allowed);
        d.Plan!.DecryptAppId.Should().Be(1);
    }

    [Fact]
    public void Lookup_of_text_in_an_encrypted_app_is_decrypted()
    {
        var d = Decide(F(Emp, 30, "Lookup", LookupJson(sourceFid: 8, parent: Secret)));
        d.Outcome.Should().Be(MaterializeOutcome.Allowed);
        d.Plan!.DecryptAppId.Should().Be(2);
    }

    [Fact]
    public void Lookup_of_a_number_in_an_encrypted_app_needs_no_decryption()
    {
        // Numbers, dates, booleans and references are never encrypted, so SQL copies them as they are.
        var d = Decide(F(Emp, 30, "Lookup", LookupJson(sourceFid: 7, parent: Secret)));
        d.Outcome.Should().Be(MaterializeOutcome.Allowed);
        d.Plan!.DecryptAppId.Should().BeNull();
    }

    [Fact]
    public void Lookup_of_a_type_that_does_not_sort_as_shown_is_not_allowed() =>
        Decide(F(Emp, 30, "Lookup", LookupJson(sourceFid: 7))).Reason.Should().Contain("MultiSelect");

    [Fact]
    public void Combined_text_is_not_allowed() =>
        Decide(F(Dept, 30, "Summary", SummaryJson("CombinedText", 9))).Reason.Should().Contain("Combined Text");

    [Fact]
    public void Summary_with_incomplete_settings_is_not_allowed() =>
        Decide(F(Dept, 30, "Summary", "{\"Function\":\"Sum\"}")).Outcome.Should().Be(MaterializeOutcome.NotAllowed);

    [Fact]
    public void Summary_over_a_missing_child_table_is_not_allowed() =>
        Decide(F(Dept, 30, "Summary", SummaryJson("Count", null, child: 999))).Outcome.Should().Be(MaterializeOutcome.NotAllowed);

    [Fact]
    public void Summary_of_a_calculated_target_is_not_allowed_yet() =>
        Decide(F(Dept, 30, "Summary", SummaryJson("Sum", 11))).Reason.Should().Contain("calculated");

    [Fact]
    public void Summary_of_an_encrypted_target_is_decrypted_with_the_child_app_key()
    {
        var d = Decide(F(Dept, 30, "Summary", SummaryJson("DistinctCount", 12)));
        d.Outcome.Should().Be(MaterializeOutcome.Allowed);
        d.Plan!.DecryptAppId.Should().Be(1);
    }

    [Fact]
    public void Summary_with_criteria_on_an_encrypted_field_is_decrypted()
    {
        var d = Decide(F(Dept, 30, "Summary", SummaryJson("Count", null, Filter(fid: 12))));
        d.Outcome.Should().Be(MaterializeOutcome.Allowed);
        d.Plan!.DecryptAppId.Should().Be(1);
    }

    [Fact]
    public void Summary_with_plain_criteria_needs_no_decryption() =>
        Decide(F(Dept, 30, "Summary", SummaryJson("Count", null, Filter(fid: 9)))).Plan!.DecryptAppId.Should().BeNull();

    [Fact]
    public void Summary_of_a_reference_is_not_allowed() =>
        Decide(F(Dept, 30, "Summary", SummaryJson("Max", 13))).Reason.Should().Contain("Reference");

    [Fact]
    public void Summary_of_an_address_part_is_not_allowed() =>
        Decide(F(Dept, 30, "Summary", SummaryJson("Max", 14, subField: "city"))).Outcome.Should().Be(MaterializeOutcome.NotAllowed);

    [Fact]
    public void Sum_without_a_target_is_not_allowed() =>
        Decide(F(Dept, 30, "Summary", SummaryJson("Sum", null))).Outcome.Should().Be(MaterializeOutcome.NotAllowed);

    [Theory]
    [InlineData("today"), InlineData("yesterday"), InlineData("pastDays"), InlineData("duringCurrent"), InlineData("parentField"), InlineData("ask")]
    public void Criteria_with_a_moving_or_outside_value_are_not_allowed(string mode) =>
        Decide(F(Dept, 30, "Summary", SummaryJson("Count", null, Filter("eq", mode)))).Outcome.Should().Be(MaterializeOutcome.NotAllowed);

    [Theory]
    [InlineData("isCurrentUser"), InlineData("isNotCurrentUser"), InlineData("during"), InlineData("notDuring")]
    public void Criteria_with_a_viewer_or_clock_operator_are_not_allowed(string op) =>
        Decide(F(Dept, 30, "Summary", SummaryJson("Count", null, Filter(op)))).Outcome.Should().Be(MaterializeOutcome.NotAllowed);

    [Theory]
    [InlineData(10L, "User")]        // user picks resolve per viewer
    [InlineData(11L, "calculated")]
    [InlineData(13L, "Reference")]
    [InlineData(99L, "no longer exists")]
    public void Criteria_on_fields_sql_cannot_read_fixed_are_not_allowed(long fid, string reason) =>
        Decide(F(Dept, 30, "Summary", SummaryJson("Count", null, Filter(fid: fid)))).Reason.Should().Contain(reason);

    [Fact]
    public void Unreadable_criteria_are_not_allowed() =>
        Decide(F(Dept, 30, "Summary", SummaryJson("Count", null, "{not json"))).Outcome.Should().Be(MaterializeOutcome.NotAllowed);

    // ── dependency graph ───────────────────────────────────────────────────────────────────────────────

    private static ComputeSchema GraphSchema() => Schema(
        F(Emp, 20, "Lookup", LookupJson()),                                  // Employee.Dept Name  <- Dept.f_6 through Emp.f_6
        F(Dept, 21, "Summary", SummaryJson("Sum", 7, Filter(fid: 9))));     // Dept.Total Salary   <- Emp.f_6, Emp.f_7, Emp.f_9 (criteria)

    [Fact]
    public void A_childs_salary_change_makes_the_parent_summary_stale()
    {
        var graph = DependencyGraph.Build(GraphSchema());
        graph.AffectedBy(Emp, [7], rowsChanged: false).Should().Contain(new FieldKey(Dept, 21)).And.NotContain(new FieldKey(Emp, 20));
    }

    [Fact]
    public void A_criteria_field_change_makes_the_summary_stale() =>
        DependencyGraph.Build(GraphSchema()).AffectedBy(Emp, [9], false).Should().Contain(new FieldKey(Dept, 21));

    [Fact]
    public void Moving_a_child_to_another_parent_touches_both_summary_and_lookup() =>
        DependencyGraph.Build(GraphSchema()).AffectedBy(Emp, [6], false).Should().Contain([new FieldKey(Dept, 21), new FieldKey(Emp, 20)]);

    [Fact]
    public void A_parent_rename_makes_the_child_lookup_stale() =>
        DependencyGraph.Build(GraphSchema()).AffectedBy(Dept, [6], false).Should().Equal(new FieldKey(Emp, 20));

    [Fact]
    public void Creating_or_deleting_children_makes_the_summary_stale() =>
        DependencyGraph.Build(GraphSchema()).AffectedBy(Emp, [], rowsChanged: true).Should().Contain(new FieldKey(Dept, 21));

    [Fact]
    public void Deleting_a_parent_blanks_the_lookup() =>
        DependencyGraph.Build(GraphSchema()).AffectedBy(Dept, [], rowsChanged: true).Should().Contain(new FieldKey(Emp, 20));

    [Fact]
    public void An_unrelated_field_change_makes_no_lookup_or_summary_stale() =>
        DependencyGraph.Build(GraphSchema()).AffectedBy(Emp, [12], false)
            .Should().NotContain([new FieldKey(Emp, 20), new FieldKey(Dept, 21), new FieldKey(Dept, 9)]);

    [Fact]
    public void Formulas_are_ordered_after_what_they_read_and_chains_are_followed()
    {
        // Salary(7) -> Annual(31, formula) -> Dept Total Annual (22, summary of 31) -> Budget status (32, formula on Dept)
        var schema = Schema(
            F(Emp, 31, "Formula_Number"),
            F(Dept, 22, "Summary", SummaryJson("Sum", 31)),
            F(Dept, 32, "Formula_Text"));
        IReadOnlyCollection<long>? Reads(AppField f) => f.Fid switch { 31 => [7L], 32 => [22L], _ => [] };
        var graph = DependencyGraph.Build(schema, Reads);

        var affected = graph.AffectedBy(Emp, [7], false);
        affected.Should().ContainInOrder(new FieldKey(Emp, 31), new FieldKey(Dept, 22), new FieldKey(Dept, 32));
        graph.InCycle.Should().BeEmpty();
    }

    [Fact]
    public void Formulas_reading_each_other_are_a_cycle()
    {
        var schema = Schema(F(Emp, 31, "Formula_Number"), F(Emp, 32, "Formula_Number"));
        var graph = DependencyGraph.Build(schema, f => f.Fid switch { 31 => [32L], 32 => [31L], _ => [] });
        graph.InCycle.Should().BeEquivalentTo([new FieldKey(Emp, 31), new FieldKey(Emp, 32)]);
        graph.TopologicalOrder.Should().NotContain(new FieldKey(Emp, 31));
    }

    [Fact]
    public void A_formula_whose_reads_are_unknown_is_stale_on_any_change_of_its_table()
    {
        var graph = DependencyGraph.Build(Schema(F(Emp, 31, "Formula_Number")), _ => null);
        graph.UnknownInputs.Should().Contain(new FieldKey(Emp, 31));
        graph.AffectedBy(Emp, [12], false).Should().Contain(new FieldKey(Emp, 31));
    }

    // ── set-based SQL ──────────────────────────────────────────────────────────────────────────────────

    private static SummaryPlan SumPlan(string function = "Sum", FilterGroup? filter = null) =>
        new(Dept, 21, 5021, Emp, 6, function, function is "Count" or "Exists" ? null : 7, null, filter);

    [Fact]
    public void Summary_sql_reads_live_children_through_the_reference_and_carries_the_version_fence()
    {
        var p = new DynamicParameters();
        var parts = MaterializedColumnWriter.SummaryBatchParts(SumPlan(), "DECIMAL(38,4)", Schema().FieldsByFid(Emp), p);
        var sql = MaterializedColumnWriter.UpdateSql("c_21", "DECIMAL(38,4)", parts);

        // The keys are parsed once into a keyed table — OPENJSON inside the IN made a 100k-row build take minutes.
        sql.Should().StartWith(MaterializedColumnWriter.KeysSql + "UPDATE x SET x.c_21 = CAST(a.Value AS DECIMAL(38,4))\nOUTPUT inserted.Id");
        sql.Split("OPENJSON").Length.Should().Be(2, "the id list is parsed exactly once");
        sql.Should().Contain("SUM(CAST(TargetValue AS DECIMAL(18,4)))");
        sql.Should().Contain("FROM data.t_11 c");
        sql.Should().Contain("WHERE c.IsDeleted = 0 AND c.f_6 BETWEEN @lo AND @hi AND c.f_6 IN (SELECT k FROM @k)");
        sql.Should().Contain("LEFT JOIN (").And.Contain("GROUP BY ParentKey").And.Contain("a.ParentKey = x.Id");
        sql.Should().Contain("WHERE x.Id BETWEEN @lo AND @hi AND x.Id IN (SELECT k FROM @k)");
        sql.Should().Contain("fx.MaterializeVersion = @version AND fx.MaterializeMode = 1");
        sql.Should().NotContain("+ ").And.NotContain("+=");   // recompute, never increment
    }

    [Theory]
    [InlineData("Count", "COALESCE(a.Value, 0)")]
    [InlineData("DistinctCount", "COALESCE(a.Value, 0)")]
    [InlineData("Exists", "COALESCE(a.Value, CAST(0 AS BIT))")]
    public void Parents_without_children_get_zero_or_false(string function, string value) =>
        MaterializedColumnWriter.SummaryBatchParts(SumPlan(function), "INT", Schema().FieldsByFid(Emp), new DynamicParameters()).Value.Should().Be(value);

    [Fact]
    public void Summary_criteria_become_parameters_not_literals()
    {
        var filter = System.Text.Json.JsonSerializer.Deserialize<FilterGroup>(Filter("eq", null, 9, "O'Brien"));
        var p = new DynamicParameters();
        var parts = MaterializedColumnWriter.SummaryBatchParts(SumPlan("Count", filter), "INT", Schema().FieldsByFid(Emp), p);
        parts.From.Should().NotContain("O'Brien");
        p.ParameterNames.Should().NotBeEmpty();
    }

    [Fact]
    public void Lookup_sql_gives_null_for_a_deleted_parent()
    {
        var plan = new LookupPlan(Emp, 20, 11020, 6, Dept, 6, "f_6");
        var sql = MaterializedColumnWriter.UpdateSql("c_20", "NVARCHAR(400)", MaterializedColumnWriter.LookupBatchParts(plan, "NVARCHAR(400)"));
        sql.Should().Contain("LEFT JOIN data.t_10 lk ON lk.Id = x.f_6 AND lk.IsDeleted = 0");
        sql.Should().Contain("CAST(LEFT(CAST(lk.f_6 AS NVARCHAR(MAX)), 400) AS NVARCHAR(400))");
        sql.Should().Contain("fx.MaterializeVersion = @version");
    }

    [Fact]
    public void Only_rows_whose_value_differs_are_written_text_compared_exactly_and_nulls_both_ways()
    {
        var plan = new LookupPlan(Emp, 20, 11020, 6, Dept, 6, "f_6");
        var sql = MaterializedColumnWriter.UpdateSql("c_20", "NVARCHAR(400)", MaterializedColumnWriter.LookupBatchParts(plan, "NVARCHAR(400)"));
        sql.Should().Contain("OUTPUT inserted.Id").And.Contain("COLLATE Latin1_General_BIN2")
            .And.Contain("x.c_20 IS NULL AND").And.Contain("x.c_20 IS NOT NULL AND");
        MaterializedColumnWriter.DiffersSql("c_9", "INT", "COALESCE(a.Value, 0)").Should().NotContain("COLLATE");
    }

    [Theory]
    [InlineData("Count", "INT"), InlineData("DistinctCount", "INT"), InlineData("Exists", "BIT"), InlineData("Sum", "DECIMAL(38,4)"), InlineData("Avg", "DECIMAL(38,6)"),
     InlineData("Min", null), InlineData("Max", null)]
    public void Stored_types_match_what_sql_server_returns(string function, string? type) =>
        MaterializedColumnWriter.SummaryStoredType(function).Should().Be(type);

    [Theory]
    [InlineData("NVARCHAR(MAX)", "NVARCHAR(400)"), InlineData("NVARCHAR(500)", "NVARCHAR(400)"), InlineData("VARCHAR(MAX)", "NVARCHAR(400)"),
     InlineData("DECIMAL(18,4)", "DECIMAL(18,4)"), InlineData("DATETIME2(3)", "DATETIME2(3)"), InlineData("DATE", "DATE"), InlineData("BIT", "BIT"),
     InlineData("BIGINT", "BIGINT"), InlineData("DATETIME", "DATETIME2(3)"), InlineData("FLOAT", null), InlineData("VARBINARY(MAX)", null)]
    public void Stored_type_for_a_source_column(string source, string? stored) =>
        SqlColumnType.StoredTypeFor(source).Should().Be(stored);

    [Theory]
    [InlineData("INT; DROP TABLE x"), InlineData("NVARCHAR(MAX)"), InlineData("DECIMAL(18,4) NULL; --")]
    public void The_schema_engine_refuses_any_other_type(string type) =>
        SqlColumnType.IsAllowed(type).Should().BeFalse();

    [Fact]
    public void Column_types_are_read_back_in_one_spelling()
    {
        SqlColumnType.Format("nvarchar", -1, 0, 0).Should().Be("NVARCHAR(MAX)");
        SqlColumnType.Format("nvarchar", 800, 0, 0).Should().Be("NVARCHAR(400)");
        SqlColumnType.Format("decimal", 9, 18, 4).Should().Be("DECIMAL(18,4)");
        SqlColumnType.Format("datetime2", 8, 23, 3).Should().Be("DATETIME2(3)");
    }

    // ── guard and scheduling ───────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(10.0, 5.0, 2.0, GuardDecision.Proceed)]
    [InlineData(61.0, 5.0, 2.0, GuardDecision.Slow)]
    [InlineData(10.0, 70.0, 2.0, GuardDecision.Slow)]
    [InlineData(10.0, 5.0, 85.0, GuardDecision.Stop)]
    [InlineData(null, null, null, GuardDecision.Proceed)]   // not Azure SQL: no stats
    public void The_busiest_resource_decides(double? cpu, double? io, double? log, GuardDecision expected) =>
        DatabaseGuard.Decide(cpu, io, log, new ComputeOptions()).Should().Be(expected);

    [Theory]
    [InlineData(-1, -1, 14, true)]   // no window = any time
    [InlineData(1, 5, 3, true)]
    [InlineData(1, 5, 5, false)]
    [InlineData(22, 5, 23, true)]    // window across midnight
    [InlineData(22, 5, 2, true)]
    [InlineData(22, 5, 12, false)]
    public void Big_jobs_only_run_off_peak(int start, int end, int hour, bool expected) =>
        ComputeConfig.IsOffPeak(new ComputeOptions { OffPeakStartHourUtc = start, OffPeakEndHourUtc = end }, new DateTime(2026, 11, 19, hour, 0, 0, DateTimeKind.Utc))
            .Should().Be(expected);

    [Fact]
    public void Reconciliation_runs_once_a_day_after_its_hour()
    {
        var day = new DateTime(2026, 11, 19, 2, 0, 0, DateTimeKind.Utc);
        ReconciliationWorker.IsDue(day, 1, null).Should().BeTrue();
        ReconciliationWorker.IsDue(day, 1, DateOnly.FromDateTime(day)).Should().BeFalse();
        ReconciliationWorker.IsDue(day.AddHours(-2), 1, null).Should().BeFalse();
    }

    // ── which rows a live change recomputes ────────────────────────────────────────────────────────────

    private static ComputeModel LiveModel()
    {
        var schema = GraphSchema();
        foreach (var f in schema.Fields.Where(f => f.Fid is 20 or 21))
        {
            f.MaterializeMode = MaterializeModes.On;
            f.MaterializeState = MaterializeStates.Ready;
        }
        // Dept.f_9 (a Summary in the base schema) stays off: switched-off fields are never recomputed.
        return new ComputeModel(schema);
    }

    [Fact]
    public void A_child_moving_parents_recomputes_old_and_new_parent_and_its_own_lookup()
    {
        var change = new PowerBase.Infrastructure.Compute.ComputeTableProcessor.ChangeSet(false, [6], false, [500],
            new Dictionary<int, IReadOnlySet<long>> { [6] = new HashSet<long> { 1 } });
        var targets = PowerBase.Infrastructure.Compute.ComputeTableProcessor.Targets(LiveModel(), Emp, change);

        var summary = targets.Single(t => t.Plan.Fid == 21).Scope.Should().BeOfType<ParentsPending>().Subject;
        summary.ChildIds.Should().Equal(500);
        summary.OldParents.Should().Contain(1);
        targets.Single(t => t.Plan.Fid == 20).Scope.Should().BeEquivalentTo(new WriteScope.Rows([500]));
    }

    [Fact]
    public void A_parent_rename_recomputes_its_children()
    {
        var change = new PowerBase.Infrastructure.Compute.ComputeTableProcessor.ChangeSet(false, [6], false, [1], new Dictionary<int, IReadOnlySet<long>>());
        var targets = PowerBase.Infrastructure.Compute.ComputeTableProcessor.Targets(LiveModel(), Dept, change);
        targets.Should().ContainSingle().Which.Scope.Should().BeEquivalentTo(new WriteScope.ChildrenOf([1]));
    }

    [Fact]
    public void A_table_wide_change_recomputes_whole_fields()
    {
        var change = new PowerBase.Infrastructure.Compute.ComputeTableProcessor.ChangeSet(true, null, true, [], new Dictionary<int, IReadOnlySet<long>>());
        var targets = PowerBase.Infrastructure.Compute.ComputeTableProcessor.Targets(LiveModel(), Emp, change);
        targets.Should().HaveCount(2).And.OnlyContain(t => t.Scope is WriteScope.All);
    }

    [Fact]
    public void Fields_that_are_off_or_not_built_are_not_recomputed_live()
    {
        var model = new ComputeModel(GraphSchema());   // nothing switched on
        var change = new PowerBase.Infrastructure.Compute.ComputeTableProcessor.ChangeSet(true, null, true, [], new Dictionary<int, IReadOnlySet<long>>());
        PowerBase.Infrastructure.Compute.ComputeTableProcessor.Targets(model, Emp, change).Should().BeEmpty();
    }
}
