using System.Text.Json;
using FluentAssertions;
using PowerBase.Application.FieldReferences;
using PowerBase.Application.FieldReferences.Extractors;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Formula;

namespace PowerBase.UnitTests.FieldReferences;

public class FieldReferenceExtractorTests
{
    private const long TableId = 7;
    private static readonly FormulaEngine Engine = new();

    // Fid 1..N get AppField.Id 1001..100N: reports/forms/rules/settings all speak Fid, the index
    // stores AppField.Id, so a mix-up between the two can never pass by coincidence.
    private static AppField Field(int fid, string name, string typeCode = "Number", string? settings = null) =>
        new() { Id = 1000 + fid, Fid = fid, AppTableId = TableId, Name = name, Label = name, TypeCode = typeCode, Settings = settings };

    private static TableFieldIndex Table(params AppField[] fields) => new(TableId, fields);

    private static FieldReferenceCollector Collector(string sourceType = FieldReferenceSourceTypes.Report) => new(sourceType, 55, TableId);

    private static (long Target, string Usage)[] Pairs(FieldReferenceCollector c) =>
        c.Rows.Select(r => (r.TargetFieldId, r.Usage)).OrderBy(p => p.TargetFieldId).ThenBy(p => p.Usage).ToArray();

    // ── Reports ──────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Report_collects_columns_sort_group_aggregation_and_dynamic_filters_as_AppField_ids()
    {
        var table = Table(Field(1, "A"), Field(2, "B"), Field(3, "C"), Field(4, "D"), Field(5, "E"));
        var definition = new ReportDefinition
        {
            Columns = [1, 2],
            TableSortGroup = [new SortGroupLevel { FieldId = 3, IsGroup = true }],
            Aggregations = [new SummaryAggregation { FieldId = 4 }],
            CustomDynamicFilterItems = [new CustomDynamicFilterItem { FieldId = 5, SubField = "city" }],
        };
        var c = Collector();

        ReportReferenceExtractor.Extract(JsonSerializer.Serialize(definition), table, c);

        Pairs(c).Should().Equal(
            (1001, FieldReferenceUsages.Column),
            (1002, FieldReferenceUsages.Column),
            (1003, FieldReferenceUsages.GroupBy),
            (1003, FieldReferenceUsages.Sort),
            (1004, FieldReferenceUsages.Aggregation),
            (1005, FieldReferenceUsages.DynamicFilter));
    }

    [Fact]
    public void Report_walks_nested_filters_and_the_compare_to_field_value()
    {
        var table = Table(Field(1, "A"), Field(2, "B"), Field(3, "C"));
        var definition = new ReportDefinition
        {
            FilterTree = new FilterGroup
            {
                Nodes =
                [
                    new FilterNode { Condition = new FilterCondition { FieldId = 1, ValueMode = "field", ValueFieldId = 2 } },
                    new FilterNode { Group = new FilterGroup { Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 3 } }] } },
                ],
            },
        };
        var c = Collector();

        ReportReferenceExtractor.Extract(JsonSerializer.Serialize(definition), table, c);

        Pairs(c).Should().Equal(
            (1001, FieldReferenceUsages.Filter),
            (1002, FieldReferenceUsages.FilterValue),
            (1003, FieldReferenceUsages.Filter));
    }

    [Fact]
    public void Report_includes_chart_fields_and_legacy_single_field_settings()
    {
        var table = Table(Field(1, "A"), Field(2, "B"), Field(3, "C"), Field(4, "D"));
        var definition = new ReportDefinition
        {
            SortFieldId = 1,
            GroupByFieldId = 2,
            Chart = new ChartConfig { SeriesFieldId = 3, GaugeFieldId = 4 },
        };
        var c = Collector();

        ReportReferenceExtractor.Extract(JsonSerializer.Serialize(definition), table, c);

        Pairs(c).Should().Equal(
            (1001, FieldReferenceUsages.Sort),
            (1002, FieldReferenceUsages.GroupBy),
            (1003, FieldReferenceUsages.Chart),
            (1004, FieldReferenceUsages.Chart));
    }

    [Fact]
    public void Report_ignores_unknown_fids_collapses_duplicates_and_tolerates_bad_json()
    {
        var table = Table(Field(1, "A"));
        var c = Collector();

        ReportReferenceExtractor.Extract(JsonSerializer.Serialize(new ReportDefinition { Columns = [1, 1, 99] }), table, c);
        ReportReferenceExtractor.Extract("{ not json", table, c);
        ReportReferenceExtractor.Extract(null, table, c);

        Pairs(c).Should().Equal((1001, FieldReferenceUsages.Column));
    }

    // ── Forms ────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Form_elements_hold_a_Fid_and_only_Field_elements_count()
    {
        var table = Table(Field(1, "A"), Field(2, "B"));
        var layout = new[]
        {
            new FormSection
            {
                Blocks =
                [
                    new FormSectionBlock
                    {
                        Elements =
                        [
                            new FormElement { ElementType = "Field", AppFieldId = 2 },
                            new FormElement { ElementType = "StaticText", AppFieldId = null },
                            new FormElement { ElementType = "Field", AppFieldId = 99 },
                        ],
                    },
                ],
            },
        };
        var c = Collector(FieldReferenceSourceTypes.Form);

        FormReferenceExtractor.Extract(layout, table, c);

        Pairs(c).Should().Equal((1002, FieldReferenceUsages.FormElement));
    }

    // ── Form rules ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public void FormRule_collects_conditions_expression_action_targets_and_action_formulas()
    {
        var table = Table(Field(1, "Qty"), Field(2, "Price"), Field(3, "Total"), Field(4, "Note", "Text"), Field(5, "Limit"));
        var rule = new FormRule
        {
            IsExpressionMode = true,
            ExpressionText = "[Qty] * [Price] > 10",
            Conditions = [new FormRuleCondition { AppFieldId = 1, ValueFieldId = 5 }],
            Actions =
            [
                new FormRuleAction { ActionType = "Show", TargetElementId = 500 },
                new FormRuleAction { ActionType = "ChangeValue", TargetElementId = 501, IsExpressionValue = true, ActionValue = "[Total] + 1" },
                new FormRuleAction { ActionType = "Show", TargetElementId = 999 },   // not in the layout → ignored
            ],
        };
        var elementFids = new Dictionary<long, long?> { [500] = 4, [501] = 3 };
        var c = Collector(FieldReferenceSourceTypes.FormRule);

        FormRuleReferenceExtractor.Extract(rule, elementFids, table, Engine, c);

        Pairs(c).Should().Equal(
            (1001, FieldReferenceUsages.RuleCondition),
            (1001, FieldReferenceUsages.RuleExpression),
            (1002, FieldReferenceUsages.RuleExpression),
            (1003, FieldReferenceUsages.RuleActionFormula),
            (1003, FieldReferenceUsages.RuleTarget),
            (1004, FieldReferenceUsages.RuleTarget),
            (1005, FieldReferenceUsages.RuleConditionValue));
    }

    [Fact]
    public void FormRule_expression_is_ignored_unless_the_rule_is_in_expression_mode()
    {
        var table = Table(Field(1, "Qty"));
        var rule = new FormRule { IsExpressionMode = false, ExpressionText = "[Qty] > 1" };
        var c = Collector(FieldReferenceSourceTypes.FormRule);

        FormRuleReferenceExtractor.Extract(rule, new Dictionary<long, long?>(), table, Engine, c);

        c.Rows.Should().BeEmpty();
    }

    // ── Field settings ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Formula_field_references_the_fields_its_expression_reads_but_not_itself()
    {
        var qty = Field(1, "Qty");
        var price = Field(2, "Price");
        var total = Field(3, "Total", "Formula_Number", "{\"expression\":\"[Qty] * [Price] + [Total]\"}");
        var table = Table(qty, price, total);
        var c = new FieldReferenceCollector(FieldReferenceSourceTypes.Field, total.Id, TableId);

        await FieldSettingsReferenceExtractor.ExtractAsync(total, table, new NoOtherTables(), Engine, c);

        Pairs(c).Should().Equal((1001, FieldReferenceUsages.Formula), (1002, FieldReferenceUsages.Formula));
    }

    [Fact]
    public async Task Lookup_references_its_reference_field_and_the_source_field_on_the_parent_table()
    {
        var parent = new TableFieldIndex(8, [new AppField { Id = 2001, Fid = 1, AppTableId = 8, Name = "Customer name", TypeCode = "Text" }]);
        var customer = Field(1, "Customer", "Reference");
        var lookup = Field(2, "Customer name", "Lookup", "{\"referenceFid\":1,\"sourceTableId\":8,\"sourceFid\":1}");
        var table = Table(customer, lookup);
        var c = new FieldReferenceCollector(FieldReferenceSourceTypes.Field, lookup.Id, TableId);

        await FieldSettingsReferenceExtractor.ExtractAsync(lookup, table, new NoOtherTables { { 8, parent } }, Engine, c);

        Pairs(c).Should().Equal((1001, FieldReferenceUsages.Lookup), (2001, FieldReferenceUsages.Lookup));
    }

    [Fact]
    public async Task Summary_references_the_child_tables_fields_and_its_filter()
    {
        var child = new TableFieldIndex(9,
        [
            new AppField { Id = 3001, Fid = 1, AppTableId = 9, Name = "Order", TypeCode = "Reference" },
            new AppField { Id = 3002, Fid = 2, AppTableId = 9, Name = "Amount", TypeCode = "Number" },
            new AppField { Id = 3003, Fid = 3, AppTableId = 9, Name = "Status", TypeCode = "Text" },
        ]);
        var filter = JsonSerializer.Serialize(new FilterGroup { Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 3 } }] });
        var settings = JsonSerializer.Serialize(new { childTableId = 9, referenceFid = 1, function = "Sum", targetFid = 2, filterTree = filter });
        var summary = Field(1, "Order total", "Summary", settings);
        var c = new FieldReferenceCollector(FieldReferenceSourceTypes.Field, summary.Id, TableId);

        await FieldSettingsReferenceExtractor.ExtractAsync(summary, Table(summary), new NoOtherTables { { 9, child } }, Engine, c);

        Pairs(c).Should().Equal(
            (3001, FieldReferenceUsages.Summary),
            (3002, FieldReferenceUsages.Summary),
            (3003, FieldReferenceUsages.Summary));
    }

    [Fact]
    public async Task ActionButton_references_capture_fields_value_sources_and_formulas()
    {
        var status = Field(1, "Status", "Text");
        var signedOn = Field(2, "Signed on", "DateTime");
        var owner = Field(3, "Owner", "Text");
        var amount = Field(4, "Amount");
        var settings = JsonSerializer.Serialize(new
        {
            variant = "Signature",
            captureFid = 1,
            timestampFid = 2,
            buttonLabel = new { kind = "formula", formula = "\"Sign \" & [Owner]" },
            addData = new[] { new { targetFid = 4, value = new { kind = "field", fieldFid = 3 } } },
        });
        var button = Field(5, "Sign", "ActionButton_Signature", settings);
        var table = Table(status, signedOn, owner, amount, button);
        var c = new FieldReferenceCollector(FieldReferenceSourceTypes.Field, button.Id, TableId);

        await FieldSettingsReferenceExtractor.ExtractAsync(button, table, new NoOtherTables(), Engine, c);

        // Owner (1003) is read twice — as an Add Data source field and inside the label formula.
        Pairs(c).Should().Equal(
            (1001, FieldReferenceUsages.ActionButton),
            (1002, FieldReferenceUsages.ActionButton),
            (1003, FieldReferenceUsages.ActionButton),
            (1003, FieldReferenceUsages.Formula),
            (1004, FieldReferenceUsages.ActionButton));
    }

    [Fact]
    public async Task Fields_without_references_or_with_malformed_settings_yield_nothing()
    {
        var plain = Field(1, "Plain", "Text");
        var broken = Field(2, "Broken", "Summary", "{ nope");
        var c = Collector(FieldReferenceSourceTypes.Field);

        await FieldSettingsReferenceExtractor.ExtractAsync(plain, Table(plain, broken), new NoOtherTables(), Engine, c);
        await FieldSettingsReferenceExtractor.ExtractAsync(broken, Table(plain, broken), new NoOtherTables(), Engine, c);

        c.Rows.Should().BeEmpty();
    }

    // ── Collector ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public void Collector_ignores_missing_ids_and_collapses_identical_rows()
    {
        var c = Collector();

        c.Add(null, FieldReferenceUsages.Column);
        c.Add(0, FieldReferenceUsages.Column);
        c.Add(5, FieldReferenceUsages.Column);
        c.Add(5, FieldReferenceUsages.Column);
        c.Add(5, FieldReferenceUsages.Sort);

        Pairs(c).Should().Equal((5, FieldReferenceUsages.Column), (5, FieldReferenceUsages.Sort));
    }

    /// <summary>Other tables a Lookup/Summary/Report Link reaches into, keyed by table id.</summary>
    private sealed class NoOtherTables : Dictionary<long, TableFieldIndex>, IFieldIndexProvider
    {
        public Task<TableFieldIndex?> GetByIdAsync(long tableId, CancellationToken ct = default) =>
            Task.FromResult(TryGetValue(tableId, out var t) ? t : null);

        public Task<TableFieldIndex?> GetByPublicIdAsync(Guid tablePublicId, CancellationToken ct = default) =>
            Task.FromResult<TableFieldIndex?>(null);
    }
}
