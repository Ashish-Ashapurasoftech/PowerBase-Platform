using System;
using System.Collections.Generic;
using System.Linq;
using FluentAssertions;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using PowerBase.Application.Compute;
using PowerBase.ComputeWorker;
using PowerBase.Domain.Entities;
using PowerBase.Infrastructure.Compute;
using PowerBase.Infrastructure.Services;
using Xunit;

namespace PowerBase.UnitTests.Compute;

/// <summary>Indexes of stored columns (design 4.3): only for fields saved reports / role rules sort, group or filter on.</summary>
public class StoredIndexTests
{
    private const long Dept = 10, Emp = 11;

    private static AppField F(long table, int fid, long id) => new() { Id = id, AppTableId = table, Fid = fid, Name = $"f{fid}" };

    private static readonly IReadOnlyDictionary<long, IReadOnlyList<AppField>> Fields = new Dictionary<long, IReadOnlyList<AppField>>
    {
        [Dept] = [F(Dept, 6, 106), F(Dept, 8, 108), F(Dept, 9, 109), F(Dept, 10, 110)],
        [Emp] = [F(Emp, 6, 206), F(Emp, 8, 208)],
    };

    private static readonly IReadOnlySet<long> Stored = new HashSet<long> { 108, 109, 110, 208 };

    private static IReadOnlySet<long> Plan(params string[] deptReports) =>
        StoredIndexPlanner.FieldsToIndex(Stored, Fields, deptReports.Select(r => (Dept, (string?)r)), []);

    [Fact]
    public void A_column_only_use_or_an_aggregation_does_not_need_an_index() =>
        Plan("""{"Columns":[6,8,9],"Aggregations":[{"FieldId":10,"Function":"Sum"}]}""").Should().BeEmpty();

    [Theory]
    [InlineData("""{"SortFields":[{"FieldId":8,"Desc":true}]}""")]
    [InlineData("""{"TableSortGroup":[{"FieldId":8,"Desc":false,"IsGroup":true}]}""")]
    [InlineData("""{"SortFieldId":8}""")]
    [InlineData("""{"GroupByFieldId":8}""")]
    [InlineData("""{"RowGroupLevels":[{"FieldId":8}]}""")]
    [InlineData("""{"FilterTree":{"Logic":"or","Nodes":[{"Group":{"Logic":"and","Nodes":[{"Condition":{"FieldId":8,"Operator":"gt","Value":"5"}}]}}]}}""")]
    [InlineData("""{"CustomDynamicFilterFields":[8]}""")]
    [InlineData("""{"Chart":{"SeriesFieldId":8}}""")]
    public void Sorting_grouping_or_filtering_on_a_stored_field_asks_for_its_index(string definition) =>
        Plan(definition).Should().Equal(108);

    [Fact]
    public void Physical_fields_and_fields_of_another_table_are_never_picked()
    {
        // fid 6 is a plain column; Emp's fid 8 is stored but this report is on Departments.
        Plan("""{"SortFields":[{"FieldId":6}],"FilterTree":{"Nodes":[{"Condition":{"FieldId":6,"Value":"x"}}]}}""").Should().BeEmpty();
        StoredIndexPlanner.FieldsToIndex(Stored, Fields, [(Emp, """{"SortFields":[{"FieldId":8}]}""")], []).Should().Equal(208);
    }

    [Fact]
    public void A_role_rule_filter_asks_for_an_index_but_the_legacy_shape_and_bad_json_are_ignored()
    {
        var rules = new (long, string?)[]
        {
            (Dept, """{"logic":"and","nodes":[{"condition":{"fieldId":9,"operator":"gt","value":"3"}}]}"""),
            (Dept, """[{"FieldPublicId":"00000000-0000-0000-0000-000000000001","Operator":"eq","Value":"1"}]"""),
            (Dept, "{not json"),
        };
        StoredIndexPlanner.FieldsToIndex(Stored, Fields, [(Dept, "{not json either")], rules).Should().Equal(109);
    }

    [Fact]
    public void Yes_no_columns_are_not_worth_an_index()
    {
        StoredIndexPlanner.IsWorthIndexing("BIT").Should().BeFalse();
        foreach (var t in new[] { "DECIMAL(18,4)", "INT", "BIGINT", "DATE", "DATETIME2(3)", "NVARCHAR(400)" })
            StoredIndexPlanner.IsWorthIndexing(t).Should().BeTrue(t);
    }

    [Fact]
    public void The_index_is_filtered_like_the_table_and_built_online_when_possible()
    {
        SchemaEngineService.CreateMaterializedIndexSql(17, 8, online: true)
            .Should().Be("CREATE NONCLUSTERED INDEX IX_t_17_c_8 ON data.t_17 (c_8) WHERE IsDeleted = 0 WITH (ONLINE = ON);");
        SchemaEngineService.CreateMaterializedIndexSql(17, 8, online: false)
            .Should().Be("CREATE NONCLUSTERED INDEX IX_t_17_c_8 ON data.t_17 (c_8) WHERE IsDeleted = 0;");
    }

    [Fact]
    public void Index_sync_runs_after_a_field_becomes_ready_and_otherwise_on_its_interval()
    {
        var runner = new BackfillRunner(null!, new ConfigurationBuilder().Build(), null!, NullLogger<BackfillRunner>.Instance);
        var options = new ComputeOptions { IndexSyncMinutes = 10 };
        var now = new DateTime(2026, 11, 20, 10, 0, 0, DateTimeKind.Utc);

        runner.IndexSyncDue(45, fieldBecameReady: false, options, now).Should().BeTrue("never synced");
        runner.MarkIndexSynced(45, now);
        runner.IndexSyncDue(45, fieldBecameReady: false, options, now.AddMinutes(5)).Should().BeFalse();
        runner.IndexSyncDue(45, fieldBecameReady: true, options, now.AddMinutes(5)).Should().BeTrue();
        runner.IndexSyncDue(45, fieldBecameReady: false, options, now.AddMinutes(10)).Should().BeTrue();
        runner.IndexSyncDue(46, fieldBecameReady: false, options, now.AddMinutes(1)).Should().BeTrue("per tenant");
    }

    [Fact]
    public void Index_settings_default_on_and_read_from_config()
    {
        var o = ComputeConfig.ReadCompute(new ConfigurationBuilder().Build());
        o.IndexesEnabled.Should().BeTrue();
        o.IndexSyncMinutes.Should().Be(10);
        var c = ComputeConfig.ReadCompute(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["Compute:IndexesEnabled"] = "false", ["Compute:IndexSyncMinutes"] = "0" }).Build());
        c.IndexesEnabled.Should().BeFalse();
        c.IndexSyncMinutes.Should().Be(1, "at least a minute");
    }
}
