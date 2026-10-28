using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Formulas;
using PowerBase.Application.Relationships;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Application.Reports.Validation;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Formula;

namespace PowerBase.UnitTests.Reports;

/// <summary>
/// Report formulas: virtual, never-stored fields (negative id) that belong to one report and can be its columns, filters,
/// sorts, group-by levels and aggregations. These run the real RunReportQueryHandler with the real FormulaProjector over an
/// in-memory "table", and the real save-time validator.
/// </summary>
public class ReportFormulasTests
{
    private const int RegionFid = 1;
    private const int QtyFid = 2;
    private const int PriceFid = 3;
    private const long TotalId = ReportFormulaFields.FirstId;   // Formula_Number: [Qty] * [Price]
    private const long SizeId = ReportFormulaFields.FirstId + 1;    // Formula_Text:   If([Qty] > 5, "Big", "Small")

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
    ];

    public ReportFormulasTests()
    {
        _queryContext.TenantId.Returns(1L);
        _queryContext.UserId.Returns(1L);
        _appUserRepo.GetUserAppPermissionsAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { PermissionCodes.ReportsRead, PermissionCodes.ReportsCreate, PermissionCodes.ReportsUpdate });

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
    }

    private static List<Order> MakeOrders(int count)
    {
        var regions = new[] { "North", "South", "East" };
        var rows = new List<Order>(count);
        for (var i = 1; i <= count; i++)
            rows.Add(new Order(i, regions[i % 3], i % 9 + 1, (i % 7 + 1) * 2.5m));
        return rows;
    }

    private static List<IReadOnlyDictionary<string, object?>> StoredRows(IEnumerable<Order> orders) =>
        orders.Select(o => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = o.Id,
            ["PublicId"] = Guid.NewGuid(),
            ["CreatedOn"] = DateTime.UtcNow,
            [PhysicalNaming.ColumnName(RegionFid)] = o.Region,
            [PhysicalNaming.ColumnName(QtyFid)] = o.Qty,
            [PhysicalNaming.ColumnName(PriceFid)] = o.Price,
        }).ToList();

    /// <summary>Serves the orders through ListBatchAfterIdAsync exactly as the repository does.</summary>
    private void ArrangeTable(IReadOnlyList<Order> orders)
    {
        var stored = StoredRows(orders);
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

    private async Task<PagedReportRunResult> RunAsync(ReportDefinition definition, string reportType = "Summary")
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
        return await sut.HandleAsync(new RunReportQuery(report.PublicId, 1, 20));
    }

    private static ReportFormula Total() => new() { Id = TotalId, Name = "Line Total", TypeCode = "Formula_Number", Expression = "[Qty] * [Price]" };
    private static ReportFormula Size() => new() { Id = SizeId, Name = "Size", TypeCode = "Formula_Text", Expression = "If([Qty] > 5, \"Big\", \"Small\")" };

    private static SummaryAggregation Agg(long fid, string function) => new() { FieldId = fid, Function = function };

    private static FilterGroup TotalAbove(string value) => new()
    {
        Nodes = [new FilterNode { Condition = new FilterCondition { FieldId = TotalId, Operator = "gt", Value = value } }],
    };

    // ── Summary / Chart ────────────────────────────────────────────────────────

    [Fact]
    public async Task A_summary_can_sum_a_report_formula_grouped_by_a_stored_field()
    {
        var orders = MakeOrders(1_200);
        ArrangeTable(orders);

        var result = await RunAsync(new ReportDefinition
        {
            ReportFormulas = [Total()],
            GroupByFieldId = RegionFid,
            Aggregations = [Agg(TotalId, "Sum")],
        });

        result.Columns.Select(c => c.Name).Should().Equal("Region", "Count", "Sum of Line Total");
        result.Items.Should().HaveCount(3);
        foreach (var item in result.Items)
        {
            var mine = orders.Where(o => o.Region == (string)item.Fields[RegionFid.ToString()]!).ToList();
            item.Fields[$"agg0_{TotalId}"].Should().Be(mine.Sum(o => o.Total));
        }
    }

    [Fact]
    public async Task A_summary_can_group_by_a_report_formula()
    {
        var orders = MakeOrders(600);
        ArrangeTable(orders);

        var result = await RunAsync(new ReportDefinition
        {
            ReportFormulas = [Size()],
            GroupByFieldId = SizeId,
            Aggregations = [Agg(QtyFid, "Sum")],
        });

        result.Items.Select(i => (string)i.Fields[SizeId.ToString()]!).Should().Equal("Big", "Small");
        foreach (var item in result.Items)
        {
            var big = (string)item.Fields[SizeId.ToString()]! == "Big";
            item.Fields["0"].Should().Be((long)orders.Count(o => (o.Qty > 5) == big));
        }
    }

    [Fact]
    public async Task A_summary_filter_on_a_report_formula_is_applied()
    {
        var orders = MakeOrders(600);
        ArrangeTable(orders);

        var result = await RunAsync(new ReportDefinition
        {
            ReportFormulas = [Total()],
            GroupByFieldId = RegionFid,
            Aggregations = [Agg(QtyFid, "Sum")],
            FilterTree = TotalAbove("50"),
        });

        result.Items.Sum(i => Convert.ToInt64(i.Fields["0"])).Should().Be(orders.Count(o => o.Total > 50));
    }

    // ── Table ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task A_table_report_shows_filters_and_sorts_by_a_report_formula()
    {
        var orders = MakeOrders(300);
        _recordRepo.ListAllMatchingAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(),
                Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>())
            .Returns(StoredRows(orders));

        var result = await RunAsync(new ReportDefinition
        {
            ReportFormulas = [Total()],
            Columns = [RegionFid, TotalId],
            FilterTree = TotalAbove("50"),
            TableSortGroup = [new SortGroupLevel { FieldId = TotalId, Desc = true }],
        }, "Table");

        var expected = orders.Where(o => o.Total > 50).OrderByDescending(o => o.Total).ToList();
        result.TotalCount.Should().Be(expected.Count);
        result.Columns.Select(c => c.Name).Should().Equal("Region", "Line Total");
        result.Items.Take(5).Select(i => Convert.ToDecimal(i.Fields[TotalId.ToString()])).Should().Equal(expected.Take(5).Select(o => o.Total));
    }

    [Fact]
    public async Task A_report_formula_is_not_a_column_when_it_is_not_picked()
    {
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<int>(), Arg.Any<int>(), Arg.Any<FilterGroup?>(),
                Arg.Any<IReadOnlyList<SortSpec>?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>(), Arg.Any<IReadOnlySet<long>?>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());

        var result = await RunAsync(new ReportDefinition { ReportFormulas = [Total()], Columns = [RegionFid] }, "Table");

        result.Columns.Select(c => c.Name).Should().Equal("Region");
    }

    // ── Save-time validation ───────────────────────────────────────────────────

    private static Dictionary<string, string[]> Validate(string reportType, string columnsMode, params ReportFormula[] formulas)
    {
        var errors = new Dictionary<string, string[]>();
        var fields = new List<AppField> { Field(RegionFid, "Region", "Text"), Field(QtyFid, "Qty", "Number"), Field(PriceFid, "Price", "Number") };
        ReportFormulaValidator.Validate(reportType, columnsMode, formulas, fields, new FormulaExpressionValidator(new FormulaEngine()), errors);
        return errors;
    }

    [Fact]
    public void A_valid_report_formula_passes()
        => Validate("Table", "Custom", Total()).Should().BeEmpty();

    [Fact]
    public void More_than_three_report_formulas_are_rejected()
    {
        var errors = Validate("Table", "Custom",
            Total(), Size(),
            new ReportFormula { Id = ReportFormulaFields.FirstId + 2, Name = "C", TypeCode = "Formula_Number", Expression = "1" },
            new ReportFormula { Id = ReportFormulaFields.FirstId, Name = "D", TypeCode = "Formula_Number", Expression = "2" });
        errors.Should().ContainKey("reportFormulas");
    }

    [Fact]
    public void A_table_report_with_default_columns_cannot_have_report_formulas()
        => Validate("Table", "Default", Total()).Should().ContainKey("reportFormulas");

    [Fact]
    public void A_name_that_matches_a_table_field_or_another_formula_is_rejected()
    {
        Validate("Summary", "Custom", new ReportFormula { Id = ReportFormulaFields.FirstId, Name = "qty", TypeCode = "Formula_Number", Expression = "1" })
            .Should().ContainKey("reportFormulas[0]");
        Validate("Summary", "Custom", Total(), new ReportFormula { Id = ReportFormulaFields.FirstId + 1, Name = "line total", TypeCode = "Formula_Number", Expression = "1" })
            .Should().ContainKey("reportFormulas[1]");
    }

    [Fact]
    public void A_formula_cannot_reference_another_report_formula()
        => Validate("Table", "Custom", Total(), new ReportFormula { Id = ReportFormulaFields.FirstId + 1, Name = "Double", TypeCode = "Formula_Number", Expression = "[Line Total] * 2" })
            .Should().ContainKey("reportFormulas[1]");

    [Fact]
    public void An_expression_that_does_not_return_the_chosen_type_is_rejected()
        => Validate("Table", "Custom", new ReportFormula { Id = ReportFormulaFields.FirstId, Name = "Oops", TypeCode = "Formula_Number", Expression = "\"text\"" })
            .Should().ContainKey("reportFormulas[0]");

    [Fact]
    public void An_id_outside_the_reserved_range_or_an_unknown_type_is_rejected()
    {
        Validate("Table", "Custom", new ReportFormula { Id = 7, Name = "X", TypeCode = "Formula_Number", Expression = "1" }).Should().NotBeEmpty();
        Validate("Table", "Custom", new ReportFormula { Id = ReportFormulaFields.FirstId, Name = "X", TypeCode = "Number", Expression = "1" }).Should().NotBeEmpty();
    }
}
