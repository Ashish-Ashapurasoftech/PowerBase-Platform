using System.Text.Json;
using FluentAssertions;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pages;
using PowerBase.Application.Pages.Queries.RenderPage;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Pages;

public class RenderPageQueryHandlerTests
{
    private readonly IPageRepository _pageRepo = Substitute.For<IPageRepository>();
    private readonly IReportRepository _reportRepo = Substitute.For<IReportRepository>();
    private readonly IAppTableRepository _tableRepo = Substitute.For<IAppTableRepository>();
    private readonly IAppFieldRepository _fieldRepo = Substitute.For<IAppFieldRepository>();
    private readonly IRecordRepository _recordRepo = Substitute.For<IRecordRepository>();
    private readonly IRolePermissionEnforcer _enforcer = Substitute.For<IRolePermissionEnforcer>();
    private readonly IUserRepository _userRepo = Substitute.For<IUserRepository>();
    private readonly PowerBase.Application.Formulas.IFormulaProjector _formulaProjector = Substitute.For<PowerBase.Application.Formulas.IFormulaProjector>();
    private readonly PowerBase.Application.Relationships.IRelationalProjector _relationalProjector = Substitute.For<PowerBase.Application.Relationships.IRelationalProjector>();
    private readonly IAzureSearchService _searchService = Substitute.For<IAzureSearchService>();

    private RenderPageQueryHandler MakeSut()
    {
        var runReportHandler = new RunReportQueryHandler(
            _reportRepo, _tableRepo, _fieldRepo, _recordRepo, _enforcer, _userRepo, _formulaProjector, _relationalProjector, _searchService);
        return new RenderPageQueryHandler(_pageRepo, _reportRepo, _tableRepo, runReportHandler);
    }

    private static Page MakeDashboardPage(string definitionJson) => new()
    {
        Id = 1,
        PublicId = Guid.NewGuid(),
        AppId = 1,
        PageNumber = 1,
        PageType = PageTypes.Dashboard,
        Name = "Dashboard",
        OwnerId = 1,
        Visibility = "Shared",
        Definition = definitionJson,
        CurrentVersionNo = 1,
    };

    private static AppTable MakeTable(long id = 5) => new()
    {
        Id = id,
        PublicId = Guid.NewGuid(),
        Name = "T",
        PhysicalTableName = PhysicalNaming.TableName(id),
    };

    private static Report MakeReport(long tableId, string reportType = "Table") => new()
    {
        Id = 10,
        PublicId = Guid.NewGuid(),
        AppTableId = tableId,
        Name = "My Report",
        ReportType = reportType,
        Visibility = "Shared",
        Definition = JsonSerializer.Serialize(new ReportDefinition()),
        CreatedOn = DateTime.UtcNow,
    };

    [Fact]
    public async Task Render_NonDashboardPage_ThrowsValidation()
    {
        var page = MakeDashboardPage("{}");
        page.PageType = PageTypes.Code;
        _pageRepo.GetVisiblePageAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);
        var sut = MakeSut();

        await sut.Invoking(s => s.HandleAsync(new RenderPageQuery(page.PublicId)))
            .Should().ThrowAsync<ValidationException>();
    }

    [Fact]
    public async Task Render_PageNotVisible_ThrowsNotFound()
    {
        _pageRepo.GetVisiblePageAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns((Page?)null);
        var sut = MakeSut();

        await sut.Invoking(s => s.HandleAsync(new RenderPageQuery(Guid.NewGuid())))
            .Should().ThrowAsync<NotFoundException>();
    }

    private static DashboardTab MakeTab(params DashboardWidget[] widgets) => new()
    {
        Id = "tab-1",
        Name = "Tab 1",
        Widgets = widgets.ToList(),
    };

    [Fact]
    public async Task Render_WidgetReportNotVisible_ReturnsForbiddenPlaceholder_NotWholePageFailure()
    {
        var widgetReportId = Guid.NewGuid();
        var definition = new DashboardDefinition
        {
            Tabs = [MakeTab(new DashboardWidget { Id = "w1", ReportPublicId = widgetReportId, Title = "Widget 1" })],
        };
        var page = MakeDashboardPage(JsonSerializer.Serialize(definition));
        _pageRepo.GetVisiblePageAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);
        _reportRepo.GetVisibleReportAsync(widgetReportId, Arg.Any<CancellationToken>()).Returns((Report?)null);
        var sut = MakeSut();

        var result = await sut.HandleAsync(new RenderPageQuery(page.PublicId));

        result.Tabs.Should().ContainSingle();
        result.Tabs[0].Widgets.Should().ContainSingle();
        result.Tabs[0].Widgets[0].Status.Should().Be("forbidden");
        result.Tabs[0].Widgets[0].WidgetId.Should().Be("w1");
    }

    [Fact]
    public async Task Render_LegacyVersion1FlatDefinition_WrapsIntoSingleDefaultTab()
    {
        // Regression guard: a Definition saved before tabs existed (flat Grid+Widgets, no
        // Tabs array) must still render — DashboardDefinition.Parse wraps it into one tab.
        var widgetReportId = Guid.NewGuid();
        var legacyJson = JsonSerializer.Serialize(new
        {
            version = 1,
            grid = new { cols = 12, rowHeight = 60, gap = 8 },
            filters = new object[0],
            widgets = new[] { new { id = "w1", reportPublicId = widgetReportId, title = "Legacy Widget", showTitle = true, layout = new { x = 0, y = 0, w = 4, h = 4 }, pageSize = 10, filterBindings = new { } } },
        });
        var page = MakeDashboardPage(legacyJson);
        _pageRepo.GetVisiblePageAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);
        _reportRepo.GetVisibleReportAsync(widgetReportId, Arg.Any<CancellationToken>()).Returns((Report?)null);
        var sut = MakeSut();

        var result = await sut.HandleAsync(new RenderPageQuery(page.PublicId));

        result.Tabs.Should().ContainSingle();
        result.Tabs[0].Widgets.Should().ContainSingle(w => w.WidgetId == "w1");
    }

    [Fact]
    public async Task Render_TwoWidgets_OneForbiddenOneOk_BothReturnedIndependently()
    {
        var table = MakeTable();
        var okReport = MakeReport(table.Id);
        var forbiddenReportId = Guid.NewGuid();

        var definition = new DashboardDefinition
        {
            Tabs =
            [
                MakeTab(
                    new DashboardWidget { Id = "w-ok", ReportPublicId = okReport.PublicId, Title = "OK Widget" },
                    new DashboardWidget { Id = "w-forbidden", ReportPublicId = forbiddenReportId, Title = "Forbidden Widget" }),
            ],
        };
        var page = MakeDashboardPage(JsonSerializer.Serialize(definition));
        _pageRepo.GetVisiblePageAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);

        _reportRepo.GetVisibleReportAsync(okReport.PublicId, Arg.Any<CancellationToken>()).Returns(okReport);
        _reportRepo.GetVisibleReportAsync(forbiddenReportId, Arg.Any<CancellationToken>()).Returns((Report?)null);

        _tableRepo.GetByIdAsync(table.Id, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(new List<AppField>());
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, Arg.Any<int>(),
            Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());
        _recordRepo.CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(0);
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new TableAccessContext
            {
                Unrestricted = true,
                VisibleFields = ci.Arg<IReadOnlyList<AppField>>(),
                EditableFieldIds = new HashSet<long>(),
            }));

        var sut = MakeSut();

        var result = await sut.HandleAsync(new RenderPageQuery(page.PublicId));

        var widgets = result.Tabs.SelectMany(t => t.Widgets).ToList();
        widgets.Should().HaveCount(2);
        widgets.Single(w => w.WidgetId == "w-ok").Status.Should().Be("ok");
        widgets.Single(w => w.WidgetId == "w-forbidden").Status.Should().Be("forbidden");
    }

    [Fact]
    public async Task Render_PageFilterValue_MapsToWidgetRuntimeFilterViaBinding()
    {
        var table = MakeTable();
        var report = MakeReport(table.Id);
        var definition = new DashboardDefinition
        {
            Filters = [new DashboardFilterSlot { Key = "region", Label = "Region" }],
            Tabs =
            [
                MakeTab(new DashboardWidget
                {
                    Id = "w1",
                    ReportPublicId = report.PublicId,
                    FilterBindings = new Dictionary<string, DashboardFilterBinding>
                    {
                        ["region"] = new DashboardFilterBinding { FieldId = 7 },
                    },
                }),
            ],
        };
        var page = MakeDashboardPage(JsonSerializer.Serialize(definition));
        _pageRepo.GetVisiblePageAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);
        _reportRepo.GetVisibleReportAsync(report.PublicId, Arg.Any<CancellationToken>()).Returns(report);
        _tableRepo.GetByIdAsync(table.Id, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 1, Fid = 7, Name = "Region", TypeCode = "Text", IsReportable = true },
        });
        _recordRepo.CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(0);
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, Arg.Any<int>(),
            Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new TableAccessContext
            {
                Unrestricted = true,
                VisibleFields = ci.Arg<IReadOnlyList<AppField>>(),
                EditableFieldIds = new HashSet<long>(),
            }));
        var sut = MakeSut();

        var filterValues = new Dictionary<string, IReadOnlyList<string>> { ["region"] = ["West"] };
        var result = await sut.HandleAsync(new RenderPageQuery(page.PublicId, filterValues));

        result.Tabs.SelectMany(t => t.Widgets).Should().ContainSingle(w => w.Status == "ok");
        await _recordRepo.Received(1).ListAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, Arg.Any<int>(),
            Arg.Is<FilterGroup?>(f => f != null && FilterTreeContainsField(f, 7)),
            Arg.Any<IReadOnlyList<SortSpec>?>());
    }

    [Fact]
    public async Task Render_TextWidget_PassesThroughContent_NoReportExecution()
    {
        var definition = new DashboardDefinition
        {
            Tabs =
            [
                MakeTab(new DashboardWidget
                {
                    Id = "w-text", WidgetType = DashboardWidgetTypes.Text, Title = "Notes",
                    Content = "<p>Hello dashboard</p>",
                }),
            ],
        };
        var page = MakeDashboardPage(JsonSerializer.Serialize(definition));
        _pageRepo.GetVisiblePageAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);
        var sut = MakeSut();

        var result = await sut.HandleAsync(new RenderPageQuery(page.PublicId));

        var widget = result.Tabs.Single().Widgets.Single();
        widget.Status.Should().Be("ok");
        widget.WidgetType.Should().Be(DashboardWidgetTypes.Text);
        widget.Content.Should().Be("<p>Hello dashboard</p>");
        await _reportRepo.DidNotReceive().GetVisibleReportAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Fact]
    public async Task Render_SearchWidget_AppliesTypedTextAsQuickSearch_OnTargetReportWidget()
    {
        var table = MakeTable();
        var report = MakeReport(table.Id);
        var definition = new DashboardDefinition
        {
            Tabs =
            [
                MakeTab(
                    new DashboardWidget { Id = "w-report", WidgetType = DashboardWidgetTypes.Report, ReportPublicId = report.PublicId },
                    new DashboardWidget { Id = "w-search", WidgetType = DashboardWidgetTypes.Search, SearchTargetWidgetId = "w-report" }),
            ],
        };
        var page = MakeDashboardPage(JsonSerializer.Serialize(definition));
        _pageRepo.GetVisiblePageAsync(page.PublicId, Arg.Any<CancellationToken>()).Returns(page);
        _reportRepo.GetVisibleReportAsync(report.PublicId, Arg.Any<CancellationToken>()).Returns(report);
        _tableRepo.GetByIdAsync(table.Id, Arg.Any<CancellationToken>()).Returns(table);
        _fieldRepo.ListByTableAsync(table.Id, Arg.Any<CancellationToken>()).Returns(new List<AppField>
        {
            new() { Id = 1, Fid = 1, Name = "Name", TypeCode = "Text", IsReportable = true, IsSearchable = true },
        });
        _recordRepo.CountAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<FilterGroup?>(), Arg.Any<long?>(), Arg.Any<CancellationToken>())
            .Returns(0);
        _recordRepo.ListAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, Arg.Any<int>(),
            Arg.Any<FilterGroup?>(), Arg.Any<IReadOnlyList<SortSpec>?>())
            .Returns(new List<IReadOnlyDictionary<string, object?>>());
        _enforcer.GetTableAccessAsync(Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(new TableAccessContext
            {
                Unrestricted = true,
                VisibleFields = ci.Arg<IReadOnlyList<AppField>>(),
                EditableFieldIds = new HashSet<long>(),
            }));
        var sut = MakeSut();

        var searchValues = new Dictionary<string, string> { ["w-search"] = "coffee" };
        var result = await sut.HandleAsync(new RenderPageQuery(page.PublicId, SearchValues: searchValues));

        result.Tabs.Single().Widgets.Single(w => w.WidgetId == "w-report").Status.Should().Be("ok");
        await _recordRepo.Received(1).ListAsync(
            Arg.Any<AppTable>(), Arg.Any<IReadOnlyList<AppField>>(), 1, Arg.Any<int>(),
            Arg.Is<FilterGroup?>(f => f != null && FilterTreeContainsQuickSearch(f, "coffee")),
            Arg.Any<IReadOnlyList<SortSpec>?>());
    }

    private static bool FilterTreeContainsQuickSearch(FilterGroup group, string value) =>
        group.Nodes.Any(n =>
            (n.Condition?.Value == value) ||
            (n.Group != null && FilterTreeContainsQuickSearch(n.Group, value)));

    private static bool FilterTreeContainsField(FilterGroup group, long fieldId) =>
        group.Nodes.Any(n =>
            (n.Condition?.FieldId == fieldId) ||
            (n.Group != null && FilterTreeContainsField(n.Group, fieldId)));
}
