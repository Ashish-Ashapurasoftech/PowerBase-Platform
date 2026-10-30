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
/// Summary formulas: a per-group calculation over named aggregates ("summary variables"). These run the real RunReportQueryHandler over an
/// in-memory table and compare each group's formula value with a plain LINQ calculation; the validator tests use the real expression validator.
/// </summary>
public class SummaryFormulasTests
{
    private const int RegionFid = 1;
    private const int QtyFid = 2;
    private const int PriceFid = 3;
    private const long FormulaId = SummaryFormulaFields.FirstId;

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

    public SummaryFormulasTests()
    {
        _queryContext.TenantId.Returns(1L);
        _queryContext.UserId.Returns(1L);
        _appUserRepo.GetUserAppPermissionsAsync(Arg.Any<long>(), Arg.Any<long>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<string> { PermissionCodes.ReportsRead });

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

    private static AppField Field(int fid, string name, string typeCode) => new()
    {
        Id = fid, Fid = fid, Name = name, TypeCode = typeCode, IsReportable = true,
    };

    private sealed record Order(long Id, string Region, decimal Qty, decimal Price);

    private static List<Order> MakeOrders(int count)
    {
        var regions = new[] { "North", "South", "East" };
        var rows = new List<Order>(count);
        for (var i = 1; i <= count; i++)
            rows.Add(new Order(i, regions[i % 3], i % 9 + 1, (i % 7 + 1) * 2.5m));
        return rows;
    }

    private void ArrangeTable(IReadOnlyList<Order> orders)
    {
        var stored = orders.Select(o => (IReadOnlyDictionary<string, object?>)new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["Id"] = o.Id,
            [PhysicalNaming.ColumnName(RegionFid)] = o.Region,
            [PhysicalNaming.ColumnName(QtyFid)] = o.Qty,
            [PhysicalNaming.ColumnName(PriceFid)] = o.Price,
        }).ToList();

        // SummarizeAsync is only ever called for a report with no computed field, so answer it from the same data.
        _recordRepo.SummarizeAsync(
                Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<(AppField Field, string Mode)>>(), Arg.Any<IReadOnlyList<SummaryAggregation>>(),
                Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<AppField?>(), Arg.Any<string>(),
                Arg.Any<IReadOnlyList<SummarizeSortSpec>?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var aggs = ci.Arg<IReadOnlyList<SummaryAggregation>>();
                IReadOnlyList<IReadOnlyDictionary<string, object?>> rows = orders.GroupBy(o => o.Region).OrderBy(g => g.Key, StringComparer.Ordinal).Select(g =>
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase) { ["GroupValue0"] = g.Key, ["Count"] = (long)g.Count() };
                    foreach (var a in aggs)
                    {
                        var name = _fields.First(f => f.Fid == a.FieldId).Name;
                        var values = g.Select(o => a.FieldId == QtyFid ? o.Qty : o.Price).ToList();
                        row[$"{a.Function}_{name}"] = a.Function == "Avg"
                            ? Math.Round(values.Sum() / values.Count, 6, MidpointRounding.AwayFromZero)
                            : values.Sum();
                    }
                    return (IReadOnlyDictionary<string, object?>)row;
                }).ToList();
                return Task.FromResult(rows);
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

        var qc = Substitute.For<IQueryContext>();
        qc.UserId.Returns(0L);
        var runtime = Substitute.For<IFormulaRuntimeContext>();
        runtime.UrlRoot.Returns(string.Empty);
        var projector = new FormulaProjector(
            new FormulaEngine(), qc, runtime,
            Substitute.For<IAppTableRepository>(), Substitute.For<IAppFieldRepository>(), Substitute.For<IRecordRepository>(),
            Substitute.For<IAppRepository>());

        var sut = new RunReportQueryHandler(
            _reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, projector, _relationalProjector,
            Substitute.For<IAzureSearchService>(), _appUserRepo, _queryContext, Substitute.For<ILogger<RunReportQueryHandler>>());
        return await sut.HandleAsync(new RunReportQuery(report.PublicId, 1, 20, null));
    }

    private static ReportDefinition Definition(string expression, int? decimalPlaces = null) => new()
    {
        GroupByFieldId = RegionFid,
        SummaryVariables =
        [
            new SummaryVariable { FieldId = QtyFid, Function = "Sum", Name = "Qty (tot)" },
            new SummaryVariable { FieldId = PriceFid, Function = "Avg", Name = "Price (avg)" },
        ],
        SummaryFormulas =
        [
            new SummaryFormula { Id = FormulaId, Name = "Summary Formula 1", TypeCode = "Formula_Number", DecimalPlaces = decimalPlaces, Expression = expression },
        ],
        Aggregations = [new SummaryAggregation { FieldId = FormulaId, Function = SummaryFormulaFields.AggregationFunction }],
    };

    [Fact]
    public async Task A_summary_formula_is_calculated_per_group_from_its_variables()
    {
        var orders = MakeOrders(300);
        ArrangeTable(orders);

        var result = await RunAsync(Definition("[Qty (tot)] * [Price (avg)]", decimalPlaces: 2));

        result.Columns.Select(c => c.Name).Should().Equal("Region", "Count", "Summary Formula 1");
        result.Columns[^1].TypeCode.Should().Be("Number");
        result.Items.Should().HaveCount(3);
        foreach (var item in result.Items)
        {
            var mine = orders.Where(o => o.Region == (string)item.Fields[RegionFid.ToString()]!).ToList();
            var avgPrice = Math.Round(mine.Sum(o => o.Price) / mine.Count, 6, MidpointRounding.AwayFromZero);
            item.Fields[$"agg0_{FormulaId}"].Should().Be(Math.Round(mine.Sum(o => o.Qty) * avgPrice, 2, MidpointRounding.AwayFromZero));
        }
    }

    [Fact]
    public async Task A_variable_the_report_already_aggregates_is_not_queried_twice()
    {
        ArrangeTable(MakeOrders(30));
        var definition = Definition("[Qty (tot)]");
        definition.Aggregations.Insert(0, new SummaryAggregation { FieldId = QtyFid, Function = "Sum" });

        var result = await RunAsync(definition);

        result.Columns.Select(c => c.Name).Should().Equal("Region", "Count", "Sum of Qty", "Summary Formula 1");
        var call = _recordRepo.ReceivedCalls().Single(c => c.GetMethodInfo().Name == nameof(IRecordRepository.SummarizeAsync));
        var queried = (IReadOnlyList<SummaryAggregation>)call.GetArguments()[2]!;
        queried.Count(a => a.FieldId == QtyFid && a.Function == "Sum").Should().Be(1);
        foreach (var item in result.Items)
            item.Fields[$"agg1_{FormulaId}"].Should().Be(item.Fields[$"agg0_{QtyFid}"]);
    }

    [Fact]
    public async Task Rows_can_be_ordered_by_a_summary_formula()
    {
        var orders = MakeOrders(300);
        ArrangeTable(orders);
        var definition = Definition("[Qty (tot)]");
        definition.SummarySortFields =
        [
            new SummarySortField { Target = "Aggregation", AggregationFieldId = FormulaId, AggregationFunction = SummaryFormulaFields.AggregationFunction, Desc = true },
        ];

        var result = await RunAsync(definition);

        var expected = orders.GroupBy(o => o.Region).OrderByDescending(g => g.Sum(o => o.Qty)).Select(g => g.Key).ToList();
        result.Items.Select(i => (string)i.Fields[RegionFid.ToString()]!).Should().Equal(expected);
    }

    [Fact]
    public async Task A_formula_that_errors_for_a_group_is_blank_not_a_failed_report()
    {
        ArrangeTable(MakeOrders(30));

        var result = await RunAsync(Definition("[Qty (tot)] / 0"));

        result.Items.Should().HaveCount(3);
        result.Items.Should().OnlyContain(i => i.Fields[$"agg0_{FormulaId}"] == null);
    }

    // ── Validation ─────────────────────────────────────────────────────────────

    private static IReadOnlyDictionary<string, string[]> Validate(
        string reportType, List<SummaryVariable> variables, List<SummaryFormula> formulas, List<AppField> fields)
    {
        var errors = new Dictionary<string, string[]>();
        SummaryFormulaValidator.Validate(reportType, variables, formulas, fields, new FormulaExpressionValidator(new FormulaEngine()), errors);
        return errors;
    }

    private static SummaryVariable Qty => new() { FieldId = QtyFid, Function = "Sum", Name = "Qty (tot)" };

    private static SummaryFormula Formula(string typeCode, string expression) =>
        new() { Id = FormulaId, Name = "F", TypeCode = typeCode, Expression = expression };

    [Fact]
    public void A_valid_formula_passes_on_summary_and_chart()
    {
        var fields = new List<AppField> { Field(QtyFid, "Qty", "Number") };
        Validate("Summary", [Qty], [Formula("Formula_Number", "[Qty (tot)] * 2")], fields).Should().BeEmpty();
        Validate("Chart", [Qty], [Formula("Formula_Number", "[Qty (tot)] * 2")], fields).Should().BeEmpty();
    }

    [Fact]
    public void A_chart_formula_must_return_something_that_can_be_plotted()
    {
        var fields = new List<AppField> { Field(QtyFid, "Qty", "Number") };
        Validate("Chart", [Qty], [Formula("Formula_Text", "\"x\"")], fields).Should().ContainKey("summaryFormulas[0]");
        Validate("Summary", [Qty], [Formula("Formula_Text", "\"x\"")], fields).Should().BeEmpty();
        Validate("Chart", [Qty], [Formula("Formula_Bool", "[Qty (tot)] > 5")], fields).Should().NotContainKey("summaryFormulas[0]");
    }

    [Fact]
    public void A_formula_can_read_variables_only_not_table_fields()
    {
        var fields = new List<AppField> { Field(QtyFid, "Qty", "Number"), Field(PriceFid, "Price", "Number") };
        Validate("Summary", [Qty], [Formula("Formula_Number", "[Price]")], fields).Should().ContainKey("summaryFormulas[0]");
    }

    [Fact]
    public void Variables_need_a_known_field_a_valid_function_and_a_unique_name()
    {
        var fields = new List<AppField> { Field(RegionFid, "Region", "Text"), Field(QtyFid, "Qty", "Number") };
        Validate("Summary", [new SummaryVariable { FieldId = 99, Function = "Sum", Name = "A" }], [], fields).Should().ContainKey("summaryVariables[0]");
        Validate("Summary", [new SummaryVariable { FieldId = RegionFid, Function = "Sum", Name = "A" }], [], fields).Should().ContainKey("summaryVariables[0]");
        Validate("Summary", [Qty, Qty], [], fields).Should().ContainKey("summaryVariables[1]");
    }

    [Fact]
    public void A_table_report_cannot_have_summary_formulas()
    {
        var fields = new List<AppField> { Field(QtyFid, "Qty", "Number") };
        Validate("Table", [Qty], [], fields).Should().ContainKey("summaryFormulas");
    }
}
