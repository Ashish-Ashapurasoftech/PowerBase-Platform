using System.Text.Json;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pages;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pages.Queries.RenderPage;

public class RenderedWidgetResult
{
    public string WidgetId { get; init; } = string.Empty;
    public string WidgetType { get; init; } = DashboardWidgetTypes.Report;
    public string Status { get; init; } = "ok"; // ok | forbidden | error
    public string? Message { get; init; }
    public string Title { get; init; } = string.Empty;
    public bool ShowTitle { get; init; }
    public DashboardWidgetLayout Layout { get; init; } = new();
    public string? BackgroundColor { get; init; }

    // Report widget only
    public Guid ReportPublicId { get; init; }
    public Guid TableId { get; init; }
    public string ReportType { get; init; } = "Table";
    public ChartConfig? Chart { get; init; }
    public PagedReportRunResult? Result { get; init; }

    // Text widget only
    public string? Content { get; init; }

    // ButtonBar widget only
    public IReadOnlyList<DashboardBarButton> Buttons { get; init; } = [];

    // WebPage widget only
    public string? WebPageUrl { get; init; }

    // LinkBar widget only (legacy)
    public IReadOnlyList<DashboardBarLink> Links { get; init; } = [];

    // Search widget only
    public Guid? SearchTargetTableId { get; init; }
    public string SearchScope { get; init; } = "AllFields";
    public IReadOnlyList<long> SearchFieldIds { get; init; } = [];
    public string SearchBoxStyle { get; init; } = "SearchAllFields";
    public Guid? SearchReportId { get; init; }
    public string SearchType { get; init; } = "Partial";
    public string SearchShowResultsIn { get; init; } = "NewWindow";
    public bool SearchShowTitle { get; init; }
    public bool SearchShowHintText { get; init; }
    public string? SearchPlaceholder { get; init; }

    // Spacer widget only
    public bool SpacerShowDivider { get; init; }
}

public class RenderedTabResult
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public DashboardGrid Grid { get; init; } = new();
    public IReadOnlyList<RenderedWidgetResult> Widgets { get; init; } = [];
}

public class RenderPageResult
{
    public IReadOnlyList<DashboardFilterSlot> Filters { get; init; } = [];
    public IReadOnlyList<RenderedTabResult> Tabs { get; init; } = [];
}

/// <summary>Renders a Dashboard page: runs each Report widget's report (with page-level
/// filters and any Search-widget text materialised into that widget's own runtime filters),
/// one widget at a time — a widget whose report the viewer can't see comes back as a
/// "forbidden" placeholder rather than failing the whole page. Text/ButtonBar/LinkBar/Search
/// widgets carry no report and are passed through as static content; only Report widgets touch
/// RunReportQueryHandler. Record-, field-, and role-level security all come from that existing
/// report-execution + permission-enforcement pipeline; no new enforcement logic here.</summary>
public class RenderPageQueryHandler
{
    private readonly IPageRepository _pageRepo;
    private readonly IReportRepository _reportRepo;
    private readonly IAppTableRepository _tableRepo;
    private readonly RunReportQueryHandler _runReportHandler;

    public RenderPageQueryHandler(
        IPageRepository pageRepo, IReportRepository reportRepo, IAppTableRepository tableRepo, RunReportQueryHandler runReportHandler)
    {
        _pageRepo = pageRepo;
        _reportRepo = reportRepo;
        _tableRepo = tableRepo;
        _runReportHandler = runReportHandler;
    }

    public async Task<RenderPageResult> HandleAsync(RenderPageQuery query, CancellationToken ct = default)
    {
        var page = await _pageRepo.GetVisiblePageAsync(query.PagePublicId, ct)
            ?? throw new NotFoundException("Page", query.PagePublicId);

        if (page.PageType != PageTypes.Dashboard)
            throw new ValidationException(new Dictionary<string, string[]>
            {
                ["pageType"] = ["Only Dashboard pages can be rendered."]
            });

        var definition = DashboardDefinition.Parse(page.Definition);
        var filterValues = query.FilterValues ?? new Dictionary<string, DashboardFilterValue>();
        var searchValues = query.SearchValues ?? new Dictionary<string, string>();

        var tabResults = new List<RenderedTabResult>();

        // Run every tab's widgets sequentially — Dapper connections are scoped per call, and
        // running one report's execution at a time keeps behaviour predictable under partial
        // failure. All tabs are rendered up front so switching tabs client-side needs no
        // further round-trip.
        foreach (var tab in definition.Tabs)
        {
            // Search widgets target another widget ON THE SAME TAB — resolve that mapping once
            // per tab so a Report widget can find "did some Search widget aim at me, and if so
            // what did the caller type".
            var quickSearchByReportWidgetId = new Dictionary<string, string>();
            foreach (var searchWidget in tab.Widgets.Where(w => w.WidgetType == DashboardWidgetTypes.Search))
            {
                if (searchWidget.SearchTargetWidgetId is not { } targetId) continue;
                if (searchValues.TryGetValue(searchWidget.Id, out var text) && !string.IsNullOrWhiteSpace(text))
                    quickSearchByReportWidgetId[targetId] = text;
            }

            var widgetResults = new List<RenderedWidgetResult>();
            foreach (var widget in tab.Widgets)
            {
                quickSearchByReportWidgetId.TryGetValue(widget.Id, out var quickSearch);
                widgetResults.Add(await RenderWidgetAsync(widget, filterValues, quickSearch, ct));
            }
            tabResults.Add(new RenderedTabResult
            {
                Id = tab.Id,
                Name = tab.Name,
                Grid = tab.Grid,
                Widgets = widgetResults,
            });
        }

        return new RenderPageResult
        {
            Filters = definition.Filters,
            Tabs = tabResults,
        };
    }

    private async Task<RenderedWidgetResult> RenderWidgetAsync(
        DashboardWidget widget,
        IReadOnlyDictionary<string, DashboardFilterValue> filterValues,
        string? quickSearch,
        CancellationToken ct)
    {
        if (widget.WidgetType != DashboardWidgetTypes.Report)
        {
            // Static widgets carry no report and need no execution or permission check beyond
            // "the viewer can see this page at all", already established by GetVisiblePageAsync.
            return new RenderedWidgetResult
            {
                WidgetId = widget.Id,
                WidgetType = widget.WidgetType,
                Status = "ok",
                Title = widget.Title,
                ShowTitle = widget.ShowTitle,
                Layout = widget.Layout,
                BackgroundColor = widget.BackgroundColor,
                Content = widget.Content,
                Buttons = widget.Buttons,
                WebPageUrl = widget.WebPageUrl,
                Links = widget.Links,
                SearchTargetTableId = widget.SearchTargetTableId,
                SearchScope = widget.SearchScope,
                SearchFieldIds = widget.SearchFieldIds,
                SearchBoxStyle = widget.SearchBoxStyle,
                SearchReportId = widget.SearchReportId,
                SearchType = widget.SearchType,
                SearchShowResultsIn = widget.SearchShowResultsIn,
                SearchShowTitle = widget.SearchShowTitle,
                SearchShowHintText = widget.SearchShowHintText,
                SearchPlaceholder = widget.SearchPlaceholder,
                SpacerShowDivider = widget.SpacerShowDivider,
            };
        }

        try
        {
            var report = await _reportRepo.GetVisibleReportAsync(widget.ReportPublicId, ct);
            if (report is null)
            {
                return new RenderedWidgetResult
                {
                    WidgetId = widget.Id,
                    WidgetType = widget.WidgetType,
                    Status = "forbidden",
                    Message = "You don't have access to this report.",
                    Title = widget.Title,
                    ShowTitle = widget.ShowTitle,
                    Layout = widget.Layout,
                };
            }

            // Materialise this widget's runtime filter tree from the page filter values ∩ its own
            // bindings — reuses the SAME FilterGroup/FilterCondition engine the Advanced filter
            // builder and "ask the user" prompt already run through (RunReportQueryHandler
            // resolves User-field ids and date/during tiers on this exact path), rather than the
            // older per-type-hardcoded-operator RuntimeFilters tuple list.
            var runtimeFilterTree = BuildRuntimeFilterTree(widget.FilterBindings, filterValues);

            var result = await _runReportHandler.HandleAsync(new RunReportQuery(
                widget.ReportPublicId, Page: 1, PageSize: widget.PageSize ?? 10,
                RuntimeFilterTree: runtimeFilterTree,
                QuickSearch: quickSearch), ct);

            ChartConfig? chart = null;
            if (report.ReportType == "Chart")
            {
                var reportDef = JsonSerializer.Deserialize<ReportDefinition>(report.Definition);
                chart = reportDef?.Chart;
            }

            var table = await _tableRepo.GetByIdAsync(report.AppTableId, ct);

            return new RenderedWidgetResult
            {
                WidgetId = widget.Id,
                WidgetType = widget.WidgetType,
                Status = "ok",
                Title = widget.Title,
                ShowTitle = widget.ShowTitle,
                Layout = widget.Layout,
                ReportPublicId = report.PublicId,
                TableId = table.PublicId,
                ReportType = report.ReportType,
                Chart = chart,
                Result = result,
            };
        }
        catch (NotFoundException)
        {
            return new RenderedWidgetResult
            {
                WidgetId = widget.Id,
                WidgetType = widget.WidgetType,
                Status = "forbidden",
                Message = "This report no longer exists.",
                Title = widget.Title,
                ShowTitle = widget.ShowTitle,
                Layout = widget.Layout,
            };
        }
        catch (Exception ex)
        {
            return new RenderedWidgetResult
            {
                WidgetId = widget.Id,
                WidgetType = widget.WidgetType,
                Status = "error",
                Message = ex.Message,
                Title = widget.Title,
                ShowTitle = widget.ShowTitle,
                Layout = widget.Layout,
            };
        }
    }

    /// <summary>Turns this widget's filter-slot bindings ∩ the page's live filter values into a
    /// single AND'd FilterGroup, one node per bound-and-filled slot. `dateRange` is the one
    /// synthetic operator with no direct FilterCondition equivalent — a custom Date range picks
    /// two literal endpoints, so it expands to a nested AND group of two conditions (gte start,
    /// lte end) on the same field/subfield rather than one condition. Every other operator
    /// (eq/contains/gte/lte/during/notDuring/...) maps straight through — RunReportQueryHandler
    /// resolves User-field ids and date/during tiers downstream, same as the Advanced builder.</summary>
    internal static FilterGroup? BuildRuntimeFilterTree(
        IReadOnlyDictionary<string, DashboardFilterBinding> filterBindings,
        IReadOnlyDictionary<string, DashboardFilterValue> filterValues)
    {
        var nodes = new List<FilterNode>();
        foreach (var (slotKey, binding) in filterBindings)
        {
            if (!filterValues.TryGetValue(slotKey, out var fv) || string.IsNullOrWhiteSpace(fv.Value)) continue;

            if (string.Equals(fv.Operator, "dateRange", StringComparison.OrdinalIgnoreCase))
            {
                var parts = fv.Value.Split('|', 2);
                if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1])) continue;
                nodes.Add(new FilterNode
                {
                    Group = new FilterGroup
                    {
                        Logic = "and",
                        Nodes =
                        [
                            new FilterNode { Condition = new FilterCondition { FieldId = binding.FieldId, SubField = binding.SubField, Operator = "gte", Value = parts[0] } },
                            new FilterNode { Condition = new FilterCondition { FieldId = binding.FieldId, SubField = binding.SubField, Operator = "lte", Value = parts[1] } },
                        ],
                    },
                });
                continue;
            }

            nodes.Add(new FilterNode
            {
                Condition = new FilterCondition { FieldId = binding.FieldId, SubField = binding.SubField, Operator = fv.Operator, Value = fv.Value, ValueMode = fv.ValueMode },
            });
        }
        return nodes.Count > 0 ? new FilterGroup { Logic = "and", Nodes = nodes } : null;
    }
}
