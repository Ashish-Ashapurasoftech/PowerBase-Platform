using System.Text.Json;
using FluentAssertions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.FieldReferences;
using PowerBase.Application.FieldReferences.Extractors;
using PowerBase.Application.Pages;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.FieldReferences;

/// <summary>What custom rules, relationships and dashboards count as using a field.</summary>
public class NewSourceExtractorTests
{
    private const long TableId = 7;
    private static readonly Guid PublicIdOfPrice = Guid.NewGuid();
    private static readonly Guid PublicIdOfQty = Guid.NewGuid();

    private static readonly TableFieldIndex Table = new(TableId,
    [
        new AppField { Id = 1001, Fid = 6, AppTableId = TableId, Name = "Qty", Label = "Qty", TypeCode = "Number", PublicId = PublicIdOfQty },
        new AppField { Id = 1002, Fid = 7, AppTableId = TableId, Name = "Price", Label = "Price", TypeCode = "Number", PublicId = PublicIdOfPrice },
    ]);

    // ── Custom rules ────────────────────────────────────────────────────────────────────────────

    private static FilterGroup Group(params long[] fids) => new()
    {
        Logic = "and",
        Nodes = fids.Select(f => new FilterNode { Condition = new FilterCondition { FieldId = f, Operator = "eq", Value = "1" } }).ToList(),
    };

    private static FieldReferenceCollector CustomRuleCollector() => new(FieldReferenceSourceTypes.CustomRule, 90, TableId);

    [Fact]
    public void A_view_rule_in_the_group_format_counts_as_a_view_rule_on_each_field_it_filters()
    {
        var rule = new CustomRuleSource(90, TableId, "View", RoleRecordFilterJson.SerializeGroup(Group(6, 7)));
        var collector = CustomRuleCollector();

        CustomRuleReferenceExtractor.Extract(rule, Table, collector);

        collector.Rows.Select(r => (r.TargetFieldId, r.Usage)).Should().BeEquivalentTo(new[]
        {
            (1001L, FieldReferenceUsages.ViewRule), (1002L, FieldReferenceUsages.ViewRule),
        });
    }

    [Fact]
    public void A_modify_rule_is_kept_apart_from_a_view_rule()
    {
        var rule = new CustomRuleSource(90, TableId, "Modify", RoleRecordFilterJson.SerializeGroup(Group(6)));
        var collector = CustomRuleCollector();

        CustomRuleReferenceExtractor.Extract(rule, Table, collector);

        collector.Rows.Should().ContainSingle().Which.Usage.Should().Be(FieldReferenceUsages.ModifyRule);
    }

    [Fact]
    public void A_nested_group_and_a_compare_to_another_field_value_are_both_followed()
    {
        var inner = new FilterGroup { Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = 7, Operator = "eq", ValueMode = "field", ValueFieldId = 6 } }] };
        var outer = new FilterGroup { Nodes = [new FilterNode { Group = inner }] };
        var collector = CustomRuleCollector();

        CustomRuleReferenceExtractor.Extract(new CustomRuleSource(90, TableId, "View", RoleRecordFilterJson.SerializeGroup(outer)), Table, collector);

        collector.Rows.Select(r => r.TargetFieldId).Should().BeEquivalentTo([1002L, 1001L]);
    }

    [Fact]
    public void A_legacy_flat_rule_resolves_fields_by_public_id()
    {
        var legacy = JsonSerializer.Serialize(new[] { new RoleRecordFilterCondition(PublicIdOfPrice, "eq", "5", false) });
        var collector = CustomRuleCollector();

        CustomRuleReferenceExtractor.Extract(new CustomRuleSource(90, TableId, "View", legacy), Table, collector);

        collector.Rows.Should().ContainSingle().Which.TargetFieldId.Should().Be(1002);
    }

    [Theory]
    [InlineData("")]
    [InlineData("[]")]
    [InlineData("not json")]
    public void An_empty_or_unreadable_rule_references_nothing(string json)
    {
        var collector = CustomRuleCollector();

        CustomRuleReferenceExtractor.Extract(new CustomRuleSource(90, TableId, "View", json), Table, collector);

        collector.Rows.Should().BeEmpty();
    }

    [Fact]
    public void A_rule_on_a_field_that_no_longer_exists_is_ignored()
    {
        var collector = CustomRuleCollector();

        CustomRuleReferenceExtractor.Extract(new CustomRuleSource(90, TableId, "View", RoleRecordFilterJson.SerializeGroup(Group(99))), Table, collector);

        collector.Rows.Should().BeEmpty();
    }

    // ── Relationships ───────────────────────────────────────────────────────────────────────────

    private static FieldReferenceCollector RelationshipCollector() => new(FieldReferenceSourceTypes.Relationship, 5, TableId);

    private static RelationshipSource Relationship(long? proxy = null, long? displayKey = null) =>
        new(5, Guid.NewGuid(), ParentTableId: 3, ChildTableId: TableId, ReferenceFieldId: 2001, ProxyFieldId: proxy, DisplayKeyFieldId: displayKey);

    [Fact]
    public void A_relationship_uses_its_reference_field_and_the_parents_key()
    {
        var collector = RelationshipCollector();

        RelationshipReferenceExtractor.Extract(Relationship(), parentKeyFieldId: 3001, collector);

        collector.Rows.Select(r => (r.TargetFieldId, r.Usage)).Should().BeEquivalentTo(new[]
        {
            (2001L, FieldReferenceUsages.RelationshipReference), (3001L, FieldReferenceUsages.RelationshipKey),
        });
    }

    [Fact]
    public void A_display_key_override_replaces_the_parents_own_key()
    {
        var collector = RelationshipCollector();

        RelationshipReferenceExtractor.Extract(Relationship(displayKey: 3002), parentKeyFieldId: 3001, collector);

        collector.Rows.Should().Contain(r => r.TargetFieldId == 3002 && r.Usage == FieldReferenceUsages.RelationshipDisplayKey);
        collector.Rows.Should().NotContain(r => r.TargetFieldId == 3001);
    }

    [Fact]
    public void The_display_label_lookup_is_recorded()
    {
        var collector = RelationshipCollector();

        RelationshipReferenceExtractor.Extract(Relationship(proxy: 2002), parentKeyFieldId: null, collector);

        collector.Rows.Select(r => (r.TargetFieldId, r.Usage)).Should().BeEquivalentTo(new[]
        {
            (2001L, FieldReferenceUsages.RelationshipReference), (2002L, FieldReferenceUsages.RelationshipDisplayLabel),
        });
    }

    [Fact]
    public void Every_relationship_row_is_filed_under_the_child_table()
    {
        var collector = RelationshipCollector();

        RelationshipReferenceExtractor.Extract(Relationship(proxy: 2002, displayKey: 3002), 3001, collector);

        collector.Rows.Should().OnlyContain(r => r.SourceTableId == TableId && r.SourceType == FieldReferenceSourceTypes.Relationship);
    }

    // ── Dashboards ──────────────────────────────────────────────────────────────────────────────

    private sealed class FakeResolver : DashboardReferenceExtractor.IResolver
    {
        public Dictionary<Guid, long> ReportTables { get; } = [];
        public Dictionary<Guid, long> TablesByPublicId { get; } = [];
        public Dictionary<long, TableFieldIndex> Tables { get; } = [];

        public long? TableOfReport(Guid reportPublicId) => ReportTables.TryGetValue(reportPublicId, out var id) ? id : null;
        public Task<long?> TableByPublicIdAsync(Guid tablePublicId, CancellationToken ct) =>
            Task.FromResult<long?>(TablesByPublicId.TryGetValue(tablePublicId, out var id) ? id : null);
        public Task<TableFieldIndex?> TableAsync(long tableId, CancellationToken ct) =>
            Task.FromResult(Tables.TryGetValue(tableId, out var table) ? table : null);
    }

    private static DashboardSource Page(DashboardDefinition definition) =>
        new(Id: 50, PublicId: Guid.NewGuid(), AppId: 1, Name: "Sales", Definition: JsonSerializer.Serialize(definition));

    private static DashboardDefinition WithWidgets(params DashboardWidget[] widgets) =>
        new() { Tabs = [new DashboardTab { Id = "t1", Name = "Tab", Widgets = [.. widgets] }] };

    [Fact]
    public async Task A_filter_slot_bound_to_a_field_counts_against_the_widgets_report_table()
    {
        var reportId = Guid.NewGuid();
        var resolver = new FakeResolver();
        resolver.ReportTables[reportId] = TableId;
        resolver.Tables[TableId] = Table;
        var widget = new DashboardWidget
        {
            Id = "w1", ReportPublicId = reportId,
            FilterBindings = { ["status"] = new DashboardFilterBinding { FieldId = 7 } },
        };

        var rows = await DashboardReferenceExtractor.ExtractAsync(Page(WithWidgets(widget)), resolver, default);

        var row = rows.Should().ContainSingle().Subject;
        row.TargetFieldId.Should().Be(1002);
        row.Usage.Should().Be(FieldReferenceUsages.DashboardFilter);
        row.SourceTableId.Should().Be(TableId);
        row.SourceType.Should().Be(FieldReferenceSourceTypes.Dashboard);
        row.SourceId.Should().Be(50);
    }

    [Fact]
    public async Task A_search_widget_limited_to_chosen_fields_counts_those_fields()
    {
        var tablePublicId = Guid.NewGuid();
        var resolver = new FakeResolver();
        resolver.TablesByPublicId[tablePublicId] = TableId;
        resolver.Tables[TableId] = Table;
        var widget = new DashboardWidget
        {
            Id = "s1", WidgetType = DashboardWidgetTypes.Search, SearchTargetTableId = tablePublicId,
            SearchScope = "SelectedFields", SearchFieldIds = [6, 7],
        };

        var rows = await DashboardReferenceExtractor.ExtractAsync(Page(WithWidgets(widget)), resolver, default);

        rows.Select(r => (r.TargetFieldId, r.Usage)).Should().BeEquivalentTo(new[]
        {
            (1001L, FieldReferenceUsages.DashboardSearch), (1002L, FieldReferenceUsages.DashboardSearch),
        });
    }

    [Fact]
    public async Task A_search_widget_over_all_fields_names_no_field()
    {
        var tablePublicId = Guid.NewGuid();
        var resolver = new FakeResolver();
        resolver.TablesByPublicId[tablePublicId] = TableId;
        resolver.Tables[TableId] = Table;
        var widget = new DashboardWidget
        {
            Id = "s1", WidgetType = DashboardWidgetTypes.Search, SearchTargetTableId = tablePublicId,
            SearchScope = "AllFields", SearchFieldIds = [6],
        };

        var rows = await DashboardReferenceExtractor.ExtractAsync(Page(WithWidgets(widget)), resolver, default);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task A_dashboard_over_two_tables_files_each_row_under_its_own_table()
    {
        var reportA = Guid.NewGuid();
        var reportB = Guid.NewGuid();
        var resolver = new FakeResolver();
        resolver.ReportTables[reportA] = TableId;
        resolver.ReportTables[reportB] = 8;
        resolver.Tables[TableId] = Table;
        resolver.Tables[8] = new TableFieldIndex(8, [new AppField { Id = 4001, Fid = 6, AppTableId = 8, Name = "Region", TypeCode = "Text" }]);
        var widgets = new[]
        {
            new DashboardWidget { Id = "a", ReportPublicId = reportA, FilterBindings = { ["x"] = new DashboardFilterBinding { FieldId = 6 } } },
            new DashboardWidget { Id = "b", ReportPublicId = reportB, FilterBindings = { ["x"] = new DashboardFilterBinding { FieldId = 6 } } },
        };

        var rows = await DashboardReferenceExtractor.ExtractAsync(Page(WithWidgets(widgets)), resolver, default);

        rows.Select(r => (r.SourceTableId, r.TargetFieldId)).Should().BeEquivalentTo(new[] { (TableId, 1001L), (8L, 4001L) });
    }

    [Fact]
    public async Task A_widget_whose_report_was_deleted_is_skipped_without_failing()
    {
        var resolver = new FakeResolver(); // no report registered
        var widget = new DashboardWidget
        {
            Id = "w1", ReportPublicId = Guid.NewGuid(),
            FilterBindings = { ["status"] = new DashboardFilterBinding { FieldId = 7 } },
        };

        var rows = await DashboardReferenceExtractor.ExtractAsync(Page(WithWidgets(widget)), resolver, default);

        rows.Should().BeEmpty();
    }

    [Fact]
    public async Task An_unreadable_definition_references_nothing()
    {
        var page = new DashboardSource(50, Guid.NewGuid(), 1, "Broken", "{ this is not json");

        var rows = await DashboardReferenceExtractor.ExtractAsync(page, new FakeResolver(), default);

        rows.Should().BeEmpty();
        DashboardReferenceExtractor.ReportIds(page.Definition).Should().BeEmpty();
    }

    [Fact]
    public void ReportIds_lists_only_the_reports_of_widgets_that_have_filter_bindings()
    {
        var bound = Guid.NewGuid();
        var unbound = Guid.NewGuid();
        var page = Page(WithWidgets(
            new DashboardWidget { Id = "a", ReportPublicId = bound, FilterBindings = { ["x"] = new DashboardFilterBinding { FieldId = 6 } } },
            new DashboardWidget { Id = "b", ReportPublicId = unbound }));

        DashboardReferenceExtractor.ReportIds(page.Definition).Should().BeEquivalentTo([bound]);
    }
}
