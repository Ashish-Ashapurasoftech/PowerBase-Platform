using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Common.Models;
using PowerBase.Application.FieldReferences;
using PowerBase.Application.Pages;
using PowerBase.Application.Reports;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using PowerBase.Formula;

namespace PowerBase.UnitTests.FieldReferences;

/// <summary>The indexer's handling of relationships, dashboards and custom rules: single-source reindex,
/// whole-table rebuild, and the batch "which tables need a backfill" pass.</summary>
public class NewSourceIndexerTests
{
    private const long AppId = 1;
    private const long TableId = 7;
    private const long ParentTableId = 3;

    private readonly IFieldReferenceRepository _references = Substitute.For<IFieldReferenceRepository>();
    private readonly IAppFieldRepository _fields = Substitute.For<IAppFieldRepository>();
    private readonly IAppTableRepository _tables = Substitute.For<IAppTableRepository>();
    private readonly IReportRepository _reports = Substitute.For<IReportRepository>();
    private readonly IFormRepository _forms = Substitute.For<IFormRepository>();
    private readonly IFormRuleRepository _rules = Substitute.For<IFormRuleRepository>();
    private readonly Guid _tablePublicId = Guid.NewGuid();

    public NewSourceIndexerTests()
    {
        _fields.ListByTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 1001, Fid = 6, AppTableId = TableId, Name = "Qty", Label = "Qty", TypeCode = "Number" },
            new() { Id = 1002, Fid = 7, AppTableId = TableId, Name = "Price", Label = "Price", TypeCode = "Number" },
        });
        _tables.GetByIdAsync(TableId, Arg.Any<CancellationToken>())
            .Returns(new AppTable { Id = TableId, PublicId = _tablePublicId, AppId = AppId });
        _tables.GetByIdAsync(ParentTableId, Arg.Any<CancellationToken>())
            .Returns(new AppTable { Id = ParentTableId, AppId = AppId, KeyFieldId = 3001 });
        _references.ListReportSourcesAsync(TableId, Arg.Any<CancellationToken>()).Returns(new List<Report>());
        _forms.ListByTableAsync(_tablePublicId, Arg.Any<CancellationToken>()).Returns(new List<Form>());
        _references.ListRelationshipsOfChildTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(new List<RelationshipSource>());
        _references.ListDashboardSourcesAsync(AppId, Arg.Any<CancellationToken>()).Returns(new List<DashboardSource>());
        _references.ListCustomRulesOfTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(new List<CustomRuleSource>());
        _references.GetReportTableIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, long>());
    }

    private FieldReferenceIndexer NewIndexer() =>
        new(_references, _fields, _tables, _reports, _forms, _rules, new FormulaEngine(), NullLogger<FieldReferenceIndexer>.Instance);

    private static string RuleJson(params long[] fids) => RoleRecordFilterJson.SerializeGroup(new FilterGroup
    {
        Nodes = fids.Select(f => new FilterNode { Condition = new FilterCondition { FieldId = f, Operator = "eq", Value = "1" } }).ToList(),
    });

    // ── Relationships ───────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReindexRelationship_replaces_the_relationships_rows_filed_under_its_child_table()
    {
        var publicId = Guid.NewGuid();
        _references.GetRelationshipSourceAsync(publicId, Arg.Any<CancellationToken>())
            .Returns(new RelationshipSource(5, publicId, ParentTableId, TableId, ReferenceFieldId: 2001, ProxyFieldId: 2002, DisplayKeyFieldId: null));

        await NewIndexer().ReindexRelationshipAsync(publicId);

        await _references.Received(1).ReplaceForSourceAsync(
            FieldReferenceSourceTypes.Relationship, 5,
            Arg.Is<IReadOnlyCollection<FieldReferenceRow>>(rows =>
                rows.Count == 3
                && rows.All(r => r.SourceTableId == TableId)
                && rows.Any(r => r.TargetFieldId == 2001 && r.Usage == FieldReferenceUsages.RelationshipReference)
                && rows.Any(r => r.TargetFieldId == 3001 && r.Usage == FieldReferenceUsages.RelationshipKey)
                && rows.Any(r => r.TargetFieldId == 2002 && r.Usage == FieldReferenceUsages.RelationshipDisplayLabel)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReindexRelationship_of_a_deleted_relationship_removes_its_rows_instead()
    {
        var publicId = Guid.NewGuid();
        _references.GetRelationshipSourceAsync(publicId, Arg.Any<CancellationToken>()).Returns((RelationshipSource?)null);

        await NewIndexer().ReindexRelationshipAsync(publicId);

        await _references.Received(1).DeleteBySourcePublicIdAsync(FieldReferenceSourceTypes.Relationship, publicId, Arg.Any<CancellationToken>());
        await _references.DidNotReceiveWithAnyArgs().ReplaceForSourceAsync(default!, default, default!, default);
    }

    // ── Dashboards ──────────────────────────────────────────────────────────────────────────────

    private DashboardSource DashboardBoundTo(Guid reportId, long fid)
    {
        var definition = new DashboardDefinition
        {
            Tabs = [new DashboardTab { Id = "t", Widgets = [new DashboardWidget
            {
                Id = "w", ReportPublicId = reportId,
                FilterBindings = { ["k"] = new DashboardFilterBinding { FieldId = fid } },
            }] }],
        };
        _references.GetReportTableIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, long> { [reportId] = TableId });
        return new DashboardSource(50, Guid.NewGuid(), AppId, "Sales", JsonSerializer.Serialize(definition));
    }

    [Fact]
    public async Task ReindexDashboard_replaces_every_row_of_the_page()
    {
        var page = DashboardBoundTo(Guid.NewGuid(), 7);
        _references.GetDashboardSourceAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);

        await NewIndexer().ReindexDashboardAsync(page.PublicId);

        await _references.Received(1).ReplaceForSourceAsync(
            FieldReferenceSourceTypes.Dashboard, 50,
            Arg.Is<IReadOnlyCollection<FieldReferenceRow>>(rows => rows.Single().TargetFieldId == 1002 && rows.Single().Usage == FieldReferenceUsages.DashboardFilter),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReindexDashboard_of_a_deleted_page_removes_its_rows_instead()
    {
        var publicId = Guid.NewGuid();
        _references.GetDashboardSourceAsync(publicId, Arg.Any<CancellationToken>()).Returns((DashboardSource?)null);

        await NewIndexer().ReindexDashboardAsync(publicId);

        await _references.Received(1).DeleteBySourcePublicIdAsync(FieldReferenceSourceTypes.Dashboard, publicId, Arg.Any<CancellationToken>());
    }

    // ── Custom rules ────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReindexCustomRules_replaces_all_of_the_roles_rows_at_once_under_the_role_id()
    {
        var rolePublicId = Guid.NewGuid();
        _references.GetRoleIdAsync(rolePublicId, Arg.Any<CancellationToken>()).Returns(90L);
        _references.ListCustomRulesOfRoleAsync(90, Arg.Any<CancellationToken>()).Returns(new List<CustomRuleSource>
        {
            new(90, TableId, "View", RuleJson(6)),
            new(90, TableId, "Modify", RuleJson(7)),
        });

        await NewIndexer().ReindexCustomRulesAsync(rolePublicId);

        await _references.Received(1).ReplaceForSourceAsync(
            FieldReferenceSourceTypes.CustomRule, 90,
            Arg.Is<IReadOnlyCollection<FieldReferenceRow>>(rows =>
                rows.Count == 2
                && rows.Any(r => r.TargetFieldId == 1001 && r.Usage == FieldReferenceUsages.ViewRule)
                && rows.Any(r => r.TargetFieldId == 1002 && r.Usage == FieldReferenceUsages.ModifyRule)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReindexCustomRules_for_a_role_with_no_rules_left_clears_its_rows()
    {
        var rolePublicId = Guid.NewGuid();
        _references.GetRoleIdAsync(rolePublicId, Arg.Any<CancellationToken>()).Returns(90L);
        _references.ListCustomRulesOfRoleAsync(90, Arg.Any<CancellationToken>()).Returns(new List<CustomRuleSource>());

        await NewIndexer().ReindexCustomRulesAsync(rolePublicId);

        await _references.Received(1).ReplaceForSourceAsync(
            FieldReferenceSourceTypes.CustomRule, 90,
            Arg.Is<IReadOnlyCollection<FieldReferenceRow>>(rows => rows.Count == 0),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task ReindexCustomRules_of_a_deleted_role_removes_its_rows_instead()
    {
        var rolePublicId = Guid.NewGuid();
        _references.GetRoleIdAsync(rolePublicId, Arg.Any<CancellationToken>()).Returns((long?)null);

        await NewIndexer().ReindexCustomRulesAsync(rolePublicId);

        await _references.Received(1).DeleteBySourcePublicIdAsync(FieldReferenceSourceTypes.CustomRule, rolePublicId, Arg.Any<CancellationToken>());
    }

    // ── Failures never reach the caller ─────────────────────────────────────────────────────────

    [Fact]
    public async Task A_failing_lookup_is_swallowed_so_the_users_save_still_succeeds()
    {
        _references.GetRoleIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).ThrowsAsync(new InvalidOperationException("db down"));

        var act = () => NewIndexer().ReindexCustomRulesAsync(Guid.NewGuid());

        await act.Should().NotThrowAsync();
    }

    // ── Whole-table rebuild ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Rebuild_puts_back_only_this_tables_dashboard_rows_and_never_replaces_the_rest_of_the_page()
    {
        var reportId = Guid.NewGuid();
        var page = DashboardBoundTo(reportId, 7);
        _references.ListDashboardSourcesAsync(AppId, Arg.Any<CancellationToken>()).Returns(new List<DashboardSource> { page });

        await NewIndexer().RebuildTableAsync(TableId);

        await _references.Received().InsertRowsAsync(
            Arg.Is<IReadOnlyCollection<FieldReferenceRow>>(rows =>
                rows.Count == 1 && rows.Single().SourceType == FieldReferenceSourceTypes.Dashboard && rows.Single().SourceTableId == TableId),
            Arg.Any<CancellationToken>());
        await _references.DidNotReceive().ReplaceForSourceAsync(FieldReferenceSourceTypes.Dashboard, Arg.Any<long>(), Arg.Any<IReadOnlyCollection<FieldReferenceRow>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rebuild_restores_the_tables_custom_rules_by_insert()
    {
        _references.ListCustomRulesOfTableAsync(TableId, Arg.Any<CancellationToken>()).Returns(new List<CustomRuleSource>
        {
            new(90, TableId, "View", RuleJson(6)),
        });

        await NewIndexer().RebuildTableAsync(TableId);

        await _references.Received().InsertRowsAsync(
            Arg.Is<IReadOnlyCollection<FieldReferenceRow>>(rows =>
                rows.Count == 1 && rows.Single().SourceType == FieldReferenceSourceTypes.CustomRule && rows.Single().SourceId == 90 && rows.Single().TargetFieldId == 1001),
            Arg.Any<CancellationToken>());
        await _references.DidNotReceive().ReplaceForSourceAsync(FieldReferenceSourceTypes.CustomRule, Arg.Any<long>(), Arg.Any<IReadOnlyCollection<FieldReferenceRow>>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rebuild_reindexes_the_relationships_that_sit_on_this_table()
    {
        var publicId = Guid.NewGuid();
        _references.ListRelationshipsOfChildTableAsync(TableId, Arg.Any<CancellationToken>())
            .Returns(new List<RelationshipSource> { new(5, publicId, ParentTableId, TableId, 2001, null, null) });

        await NewIndexer().RebuildTableAsync(TableId);

        await _references.Received(1).ReplaceForSourceAsync(
            FieldReferenceSourceTypes.Relationship, 5,
            Arg.Is<IReadOnlyCollection<FieldReferenceRow>>(rows => rows.Any(r => r.TargetFieldId == 2001)),
            Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Rebuild_marks_the_table_as_indexed_once_everything_is_in()
    {
        await NewIndexer().RebuildTableAsync(TableId);

        await _references.Received(1).MarkTableIndexedAsync(TableId, Arg.Any<CancellationToken>());
    }

    // ── Batch backfill ──────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task EnsureTablesIndexed_rebuilds_only_the_tables_that_are_not_indexed_yet()
    {
        _references.ListIndexedTableIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<long> { 8 });

        await NewIndexer().EnsureTablesIndexedAsync([TableId, 8]);

        await _references.Received(1).ClearTableAsync(TableId, Arg.Any<CancellationToken>());
        await _references.DidNotReceive().ClearTableAsync(8, Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task EnsureTablesIndexed_with_every_table_already_indexed_does_no_rebuild()
    {
        _references.ListIndexedTableIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<long> { TableId });

        await NewIndexer().EnsureTablesIndexedAsync([TableId]);

        await _references.DidNotReceiveWithAnyArgs().ClearTableAsync(default, default);
    }

    [Fact]
    public async Task EnsureTablesIndexed_keeps_going_when_one_table_fails_to_rebuild()
    {
        _references.ListIndexedTableIdsAsync(Arg.Any<IReadOnlyCollection<long>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<long>());
        _tables.GetByIdAsync(9, Arg.Any<CancellationToken>()).ThrowsAsync(new NotFoundException("Table", 9));

        await NewIndexer().EnsureTablesIndexedAsync([9, TableId]);

        await _references.Received(1).MarkTableIndexedAsync(TableId, Arg.Any<CancellationToken>());
        await _references.DidNotReceive().MarkTableIndexedAsync(9, Arg.Any<CancellationToken>());
    }
}
