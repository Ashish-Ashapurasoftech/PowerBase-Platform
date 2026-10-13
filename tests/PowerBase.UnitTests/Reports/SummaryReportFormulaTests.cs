using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Reports;

/// <summary>
/// A Summary report that groups by / aggregates a Formula field used to fail ("Invalid column name": a
/// formula has no column to GROUP BY / SUM in SQL), so the report never loaded. These run the real
/// RunReportQueryHandler with the real FormulaProjector over an in-memory "table" and check the in-memory
/// summary against a plain LINQ calculation, including across several read batches.
/// </summary>
public class SummaryReportFormulaTests
{
    private const int RegionFid = 1;
    private const int QtyFid = 2;
    private const int PriceFid = 3;
    private const int TotalFid = 4;   // Formula (Number): [Qty] * [Price]
    private const int SizeFid = 5;    // Formula (Text):   If([Qty] > 5, "Big", "Small")

    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly IAppUserRepository _appUserRepo = Substitute.For<IAppUserRepository>();
    private readonly IRelationalProjector _relationalProjector = Substitute.For<IRelationalProjector>();
    private readonly IQueryContext _queryContext = Substitute.For<IQueryContext>();

    private readonly AppTable _table = new() { Id = 5, PublicId = Guid.NewGuid(), Name = "Orders", PhysicalTableName = PhysicalNaming.TableName(5) };
    private readonly List<AppField> _fields =
    [
        Field(RegionFid, "Region", "Text"),
        Field(QtyFid, "Qty", "Number"),
        Field(PriceFid, "Price", "Number"),
        Field(TotalFid, "Total", "Formula", "{\"resultType\":\"Number\",\"expression\":\"[Qty] * [Price]\"}"),
        Field(SizeFid, "Size", "Formula", "{\"resultType\":\"Text\",\"expression\":\"If([Qty] > 5, \\\"Big\\\", \\\"Small\\\")\"}"),
    ];

    public SummaryReportFormulaTests()
    {
        _queryContext.TenantId.Returns(1L);
        _queryContext.UserId.Returns(1L);
        _appUserRepo.GetUserAppPermissionsAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { PermissionCodes.ReportsRead, PermissionCodes.ReportsCreate, PermissionCodes.ReportsUpdate });

        // No Lookup/Summary fields on this table — an empty map per row.
        _relationalProjector.ProjectAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(), Arg.Any<CancellationToken>())
            .Returns(ci => (IReadOnlyList<IReadOnlyDictionary<long, object?>>)ci.Arg<IReadOnlyList<IReadOnlyDictionary<string, object?>>>()
                .Select(_ => (IReadOnlyDictionary<long, object?>)new Dictionary<long, object?>()).ToList());

        _tableRepo.GetByIdAsync(_table.Id).Returns(_table);
        _fieldRepo.ListByTableAsync(_table.Id).Returns(_fields);
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new TableAccessContext
            {
                Unrestricted = true,
                VisibleFields = ci.Arg<IReadOnlyList<AppField>>(),
            }));
    }

    private static AppField Field(int fid, string name, string typeCode, string? settings = null) => new()
    {
        Id = fid, Fid = fid, Name = name, TypeCode = typeCode, Settings = settings, IsReportable = true,
    };

    private static FormulaProjector NewFormulaProjector()
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

    private sealed record Order(long Id, string Region, decimal Qty, decimal Price)
    {
        public decimal Total => Qty * Price;
        public string Size => Qty > 5 ? "Big" : "Small";
    }

    private static List<Order> MakeOrders(int count)
    {
        var regions = new[] { "North", "South", "East" };
        var rows = new List<Order>(count);
        for (var i = 1; i <= count; i++)
            rows.Add(new Order(i, regions[i % 3], i % 9 + 1, (i % 7 + 1) * 2.5m));
        return rows;
    }

    /// <summary>Serves the orders through ListBatchAfterIdAsync exactly as the repository does: Id &gt; afterId,
    /// ordered by Id, at most batchSize rows.</summary>
    private void ArrangeTable(IReadOnlyList<Order> orders)
    {
        var stored = orders.Select(o => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = o.Id,
            [PhysicalNaming.ColumnName(RegionFid)] = o.Region,
            [PhysicalNaming.ColumnName(QtyFid)] = o.Qty,
            [PhysicalNaming.ColumnName(PriceFid)] = o.Price,
        }).ToList();

        _recordRepo.ListBatchAfterIdAsync(
                Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<long>(), Arg.Any<int>(),
                Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var afterId = ci.ArgAt<long>(2);
                var batchSize = ci.ArgAt<int>(3);
                return Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
                    stored.Where(r => Convert.ToInt64(r["Id"]) > afterId).Take(batchSize).ToList());
            });
    }

    private async Task<PagedReportRunResult> RunAsync(
        ReportDefinition definition, string reportType = "Summary",
        IReadOnlyList<(long FieldId, string Value, string? SubField)>? runtimeFilters = null)
    {
        var report = new Report
        {
            Id = 10, PublicId = Guid.NewGuid(), AppTableId = _table.Id, Name = "R", ReportType = reportType,
            Visibility = "Personal", Definition = JsonSerializer.Serialize(definition), CreatedOn = DateTime.UtcNow,
        };
        _reportRepo.GetVisibleReportAsync(report.PublicId, Arg.Any<CancellationToken>()).Returns(report);

        var sut = new RunReportQueryHandler(
            _reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, NewFormulaProjector(), _relationalProjector,
            Substitute.For<IAzureSearchService>(), _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>());
        return await sut.HandleAsync(new RunReportQuery(report.PublicId, 1, 20, runtimeFilters));
    }

    private static SummaryAggregation Agg(long fid, string function) => new() { FieldId = fid, Function = function };

    // ── Aggregating a formula (the reported failure) ───────────────────────────

    [Fact]
    public async Task Sum_of_a_formula_grouped_by_a_stored_field_matches_the_data_across_batches()
    {
        var orders = MakeOrders(2_500);   // read in several batches
        ArrangeTable(orders);

        var result = await RunAsync(new ReportDefinition
        {
            GroupByFieldId = RegionFid,
            Aggregations = [Agg(TotalFid, "Sum"), Agg(TotalFid, "Avg"), Agg(TotalFid, "Min"), Agg(TotalFid, "Max")],
        });

        result.Columns.Select(c => c.Name).Should().Equal("Region", "Count", "Sum of Total", "Avg of Total", "Min of Total", "Max of Total");
        result.Items.Should().HaveCount(3);

        foreach (var item in result.Items)
        {
            var mine = orders.Where(o => o.Region == (string)item.Fields[RegionFid.ToString()]!).ToList();
            item.Fields["0"].Should().Be((long)mine.Count);
            item.Fields["agg0_4"].Should().Be(mine.Sum(o => o.Total));
            item.Fields["agg1_4"].Should().Be(Math.Round(mine.Sum(o => o.Total) / mine.Count, 6, MidpointRounding.AwayFromZero));
            item.Fields["agg2_4"].Should().Be(mine.Min(o => o.Total));
            item.Fields["agg3_4"].Should().Be(mine.Max(o => o.Total));
        }
        // default order: ascending by the group value
        result.Items.Select(i => (string)i.Fields[RegionFid.ToString()]!).Should().Equal("East", "North", "South");
        // the table was streamed in more than one batch, not read in a single capped query
        _recordRepo.ReceivedCalls().Count(c => c.GetMethodInfo().Name == nameof(IRecordRepository.ListBatchAfterIdAsync))
            .Should().BeGreaterThan(2);
    }

    [Fact]
    public async Task Median_StdDev_and_DistinctCount_of_a_formula_are_exact()
    {
        var orders = MakeOrders(2_500);
        ArrangeTable(orders);

        var result = await RunAsync(new ReportDefinition
        {
            GroupByFieldId = RegionFid,
            Aggregations = [Agg(TotalFid, "Median"), Agg(TotalFid, "StdDev"), Agg(TotalFid, "DistinctCount")],
        });

        foreach (var item in result.Items)
        {
            var totals = orders.Where(o => o.Region == (string)item.Fields[RegionFid.ToString()]!).Select(o => o.Total).OrderBy(t => t).ToList();
            var median = totals.Count % 2 == 1
                ? (double)totals[totals.Count / 2]
                : ((double)totals[totals.Count / 2 - 1] + (double)totals[totals.Count / 2]) / 2d;
            var mean = totals.Average(t => (double)t);
            var stdDev = Math.Sqrt(totals.Sum(t => Math.Pow((double)t - mean, 2)) / (totals.Count - 1));

            ((double)item.Fields["agg0_4"]!).Should().BeApproximately(median, 1e-9);
            ((double)item.Fields["agg1_4"]!).Should().BeApproximately(stdDev, 1e-6);
            item.Fields["agg2_4"].Should().Be(totals.Distinct().Count());
        }
    }

    // ── Grouping by a formula ──────────────────────────────────────────────────

    [Fact]
    public async Task Grouping_by_a_text_formula_counts_and_sums_per_value()
    {
        var orders = MakeOrders(2_500);
        ArrangeTable(orders);

        var result = await RunAsync(new ReportDefinition
        {
            GroupByFieldId = SizeFid,
            Aggregations = [Agg(QtyFid, "Sum"), Agg(TotalFid, "Sum")],
        });

        result.Items.Select(i => (string)i.Fields[SizeFid.ToString()]!).Should().Equal("Big", "Small");
        foreach (var item in result.Items)
        {
            var mine = orders.Where(o => o.Size == (string)item.Fields[SizeFid.ToString()]!).ToList();
            item.Fields["0"].Should().Be((long)mine.Count);
            item.Fields["agg0_2"].Should().Be(mine.Sum(o => o.Qty));
            item.Fields["agg1_4"].Should().Be(mine.Sum(o => o.Total));
        }
    }

    [Fact]
    public async Task A_formula_group_level_can_be_nested_under_a_stored_one()
    {
        var orders = MakeOrders(300);
        ArrangeTable(orders);

        var result = await RunAsync(new ReportDefinition
        {
            RowGroupLevels = [new RowGroupLevel { FieldId = RegionFid }, new RowGroupLevel { FieldId = SizeFid }],
            Aggregations = [Agg(TotalFid, "Sum")],
        });

        result.Items.Should().HaveCount(6);   // 3 regions x {Big, Small}
        result.Items.Sum(i => (decimal)i.Fields["agg0_4"]!).Should().Be(orders.Sum(o => o.Total));
        result.Items.Sum(i => (long)i.Fields["0"]!).Should().Be(300);
    }

    // ── Filters, sorting, and the untouched SQL path ───────────────────────────

    [Fact]
    public async Task A_filter_on_a_formula_is_applied_in_memory_and_kept_out_of_the_SQL_filter()
    {
        var orders = MakeOrders(2_500);
        ArrangeTable(orders);

        var result = await RunAsync(new ReportDefinition
        {
            GroupByFieldId = RegionFid,
            Aggregations = [Agg(TotalFid, "Sum")],
            FilterTree = new FilterGroup
            {
                Logic = "and",
                Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = TotalFid, Operator = "gt", Value = "50" } }],
            },
        });

        var matching = orders.Where(o => o.Total > 50).ToList();
        result.Items.Sum(i => (long)i.Fields["0"]!).Should().Be(matching.Count);
        result.Items.Sum(i => (decimal)i.Fields["agg0_4"]!).Should().Be(matching.Sum(o => o.Total));
        await _recordRepo.Received().ListBatchAfterIdAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<long>(), Arg.Any<int>(),
            null, Arg.Any<long?>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task A_dynamic_filter_on_a_number_formula_matches_the_exact_value_not_a_substring()
    {
        var orders = MakeOrders(500);
        ArrangeTable(orders);
        var wanted = orders.First().Total;   // e.g. 7.5 — "contains 7.5" would also match 17.5, 27.5, ...
        orders.Any(o => o.Total != wanted && o.Total.ToString("0.##").Contains(wanted.ToString("0.##"))).Should().BeTrue("the data must contain a lookalike");

        var result = await RunAsync(
            new ReportDefinition { GroupByFieldId = RegionFid, Aggregations = [Agg(TotalFid, "Sum")] },
            runtimeFilters: [(TotalFid, wanted.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture), null)]);

        var matching = orders.Where(o => o.Total == wanted).ToList();
        result.Items.Sum(i => (long)i.Fields["0"]!).Should().Be(matching.Count);
        result.Items.Sum(i => (decimal)i.Fields["agg0_4"]!).Should().Be(matching.Sum(o => o.Total));
    }

    [Fact]
    public async Task Rows_can_be_sorted_by_an_aggregation_of_a_formula()
    {
        var orders = MakeOrders(2_500);
        ArrangeTable(orders);

        var result = await RunAsync(new ReportDefinition
        {
            GroupByFieldId = RegionFid,
            Aggregations = [Agg(TotalFid, "Sum")],
            SummarySortFields = [new SummarySortField { Target = "Aggregation", AggregationFieldId = TotalFid, AggregationFunction = "Sum", Desc = true }],
        });

        var expected = orders.GroupBy(o => o.Region).OrderByDescending(g => g.Sum(o => o.Total)).Select(g => g.Key);
        result.Items.Select(i => (string)i.Fields[RegionFid.ToString()]!).Should().Equal(expected);
    }

    [Fact]
    public async Task A_Table_report_with_a_formula_filter_reads_every_matching_record_with_no_row_cap()
    {
        var orders = MakeOrders(500);
        var stored = orders.Select(o => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = o.Id,
            ["PublicId"] = Guid.NewGuid(),
            ["CreatedOn"] = DateTime.UtcNow,
            [PhysicalNaming.ColumnName(RegionFid)] = o.Region,
            [PhysicalNaming.ColumnName(QtyFid)] = o.Qty,
            [PhysicalNaming.ColumnName(PriceFid)] = o.Price,
        }).ToList();
        _recordRepo.ListAllMatchingAsync(
                Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>(),
                Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(stored));

        var result = await RunAsync(
            new ReportDefinition
            {
                FilterTree = new FilterGroup
                {
                    Logic = "and",
                    Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = TotalFid, Operator = "gt", Value = "100" } }],
                },
            },
            reportType: "Table");

        result.TotalCount.Should().Be(orders.Count(o => o.Total > 100));
    }

    [Fact]
    public async Task Is_the_current_user_means_includes_on_a_user_list_formula_and_equals_on_a_single_user_formula()
    {
        var fields = new Dictionary<long, AppField>
        {
            [20] = new() { Id = 20, Fid = 20, Name = "Team", TypeCode = "Formula_ListUser" },
            [21] = new() { Id = 21, Fid = 21, Name = "Owner", TypeCode = "Formula_User" },
        };
        var tree = new FilterGroup
        {
            Logic = "and",
            Nodes =
            [
                new FilterNode { Condition = new FilterCondition { FieldId = 20, Operator = "isCurrentUser" } },
                new FilterNode { Condition = new FilterCondition { FieldId = 21, Operator = "isCurrentUser" } },
            ],
        };

        var resolved = await RunReportQueryHandler.ResolveUserFieldValuesAsync(tree, fields, 7, new Dictionary<Guid, long>(), _userRepo, default);

        resolved!.Nodes[0].Condition.Should().BeEquivalentTo(new { FieldId = 20L, Operator = "includes", Value = "7" });
        resolved.Nodes[1].Condition.Should().BeEquivalentTo(new { FieldId = 21L, Operator = "eq", Value = "7" });
    }

    [Fact]
    public async Task An_empty_table_gives_no_rows_but_still_reports_its_columns()
    {
        ArrangeTable([]);

        var result = await RunAsync(new ReportDefinition { GroupByFieldId = RegionFid, Aggregations = [Agg(TotalFid, "Sum")] });

        result.Items.Should().BeEmpty();
        result.Columns.Select(c => c.Name).Should().Equal("Region", "Count", "Sum of Total");
    }

    [Fact]
    public async Task A_Chart_report_over_a_formula_is_summarized_in_memory_too()
    {
        var orders = MakeOrders(500);
        ArrangeTable(orders);

        var result = await RunAsync(
            new ReportDefinition { GroupByFieldId = RegionFid, Aggregations = [Agg(TotalFid, "Sum")], Chart = new ChartConfig { ChartType = "Bar" } },
            reportType: "Chart");

        result.Items.Sum(i => (decimal)i.Fields["agg0_4"]!).Should().Be(orders.Sum(o => o.Total));
    }

    [Fact]
    public async Task A_report_over_stored_fields_only_still_uses_the_SQL_summary()
    {
        _recordRepo.SummarizeAsync(
                Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<(AppField Field, string Mode)>>(), Arg.Any<IReadOnlyList<SummaryAggregation>>(),
                Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<AppField?>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<SummarizeSortSpec>?>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<IReadOnlyList<IReadOnlyDictionary<string, object?>>>(
            [
                new Dictionary<string, object?> { ["GroupValue0"] = "North", ["Count"] = 7, ["Sum_Qty"] = 42m },
            ]));

        var result = await RunAsync(new ReportDefinition { GroupByFieldId = RegionFid, Aggregations = [Agg(QtyFid, "Sum")] });

        result.Items.Should().ContainSingle().Which.Fields["agg0_2"].Should().Be(42m);
        await _recordRepo.DidNotReceiveWithAnyArgs().ListBatchAfterIdAsync(default!, default!, default, default, default, default, default);
    }
}
