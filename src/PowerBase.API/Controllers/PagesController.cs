using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Pages;
using PowerBase.Application.Pages;
using PowerBase.Application.Pages.Commands.CreatePage;
using PowerBase.Application.Pages.Commands.DeletePages;
using PowerBase.Application.Pages.Commands.DuplicatePage;
using PowerBase.Application.Pages.Commands.PublishPage;
using PowerBase.Application.Pages.Commands.RestorePageVersion;
using PowerBase.Application.Pages.Commands.UpdatePage;
using PowerBase.Application.Pages.Queries.GetPage;
using PowerBase.Application.Pages.Queries.ListPages;
using PowerBase.Application.Pages.Queries.ListPageVersions;
using PowerBase.Application.Pages.Commands.SetDefaultHome;
using PowerBase.Application.Pages.Queries.RenderPage;
using PowerBase.API.Models.Records;
using PowerBase.API.Models.Reports;
using PowerBase.Domain.Constants;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("apps/{appId:guid}/pages")]
[RequireAuth]
public class PagesController : ControllerBase
{
    private readonly ListPagesQueryHandler _listHandler;
    private readonly GetPageQueryHandler _getHandler;
    private readonly ListPageVersionsQueryHandler _listVersionsHandler;
    private readonly CreatePageCommandHandler _createHandler;
    private readonly UpdatePageCommandHandler _updateHandler;
    private readonly DeletePagesCommandHandler _deleteHandler;
    private readonly DuplicatePageCommandHandler _duplicateHandler;
    private readonly PublishPageCommandHandler _publishHandler;
    private readonly RestorePageVersionCommandHandler _restoreHandler;
    private readonly RenderPageQueryHandler _renderHandler;
    private readonly SetDefaultHomeCommandHandler _setDefaultHomeHandler;

    public PagesController(
        ListPagesQueryHandler listHandler,
        GetPageQueryHandler getHandler,
        ListPageVersionsQueryHandler listVersionsHandler,
        CreatePageCommandHandler createHandler,
        UpdatePageCommandHandler updateHandler,
        DeletePagesCommandHandler deleteHandler,
        DuplicatePageCommandHandler duplicateHandler,
        PublishPageCommandHandler publishHandler,
        RestorePageVersionCommandHandler restoreHandler,
        RenderPageQueryHandler renderHandler,
        SetDefaultHomeCommandHandler setDefaultHomeHandler)
    {
        _listHandler = listHandler;
        _getHandler = getHandler;
        _listVersionsHandler = listVersionsHandler;
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _duplicateHandler = duplicateHandler;
        _publishHandler = publishHandler;
        _restoreHandler = restoreHandler;
        _renderHandler = renderHandler;
        _setDefaultHomeHandler = setDefaultHomeHandler;
    }

    /// <summary>List pages for this app. AllPages=true (App Settings authoring view) also
    /// returns drafts and pages the caller wouldn't otherwise see by role visibility.</summary>
    [HttpGet]
    [RequireAppPermission(PermissionCodes.PagesRead, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PageListItemResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(Guid appId, [FromQuery] string? search, [FromQuery] bool allPages = true, CancellationToken ct = default)
    {
        var pages = await _listHandler.HandleAsync(new ListPagesQuery(appId, search, allPages), ct);
        return Ok(new ApiResponse<IReadOnlyList<PageListItemResponse>>(pages.Select(MapListItem).ToList()));
    }

    /// <summary>Create a page.</summary>
    [HttpPost]
    [RequireAppPermission(PermissionCodes.PagesCreate, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiResponse<PageDetailResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(Guid appId, [FromBody] CreatePageRequest request, CancellationToken ct)
    {
        var result = await _createHandler.HandleAsync(new CreatePageCommand(
            appId, request.PageType, request.Name, request.Description, request.Visibility,
            request.VisibleToRoleIds, request.Definition, request.ContentType,
            request.CodeHtml, request.CodeCss, request.CodeJs,
            request.ShowInNav, request.NavOrder, request.NavIcon), ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<PageDetailResponse>(MapDetail(result)));
    }

    /// <summary>Get full page detail, including Code content.</summary>
    [HttpGet("{publicId:guid}")]
    [RequireAppPermission(PermissionCodes.PagesRead, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiResponse<PageDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid appId, Guid publicId, CancellationToken ct)
    {
        var result = await _getHandler.HandleAsync(new GetPageQuery(publicId), ct);
        return Ok(new ApiResponse<PageDetailResponse>(MapDetail(result)));
    }

    /// <summary>Update a page. Requires a non-empty change note — every edit is versioned.</summary>
    [HttpPatch("{publicId:guid}")]
    [RequireAppPermission(PermissionCodes.PagesUpdate, AppAccessResolver.ByPagePublicId)]
    [ProducesResponseType(typeof(ApiResponse<PageDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid appId, Guid publicId, [FromBody] UpdatePageRequest request, CancellationToken ct)
    {
        var result = await _updateHandler.HandleAsync(new UpdatePageCommand(
            publicId, request.Name, request.Description, request.Visibility, request.VisibleToRoleIds,
            request.Definition, request.ContentType, request.CodeHtml, request.CodeCss, request.CodeJs,
            request.ShowInNav, request.NavOrder, request.NavIcon, request.ChangeNotes), ct);
        return Ok(new ApiResponse<PageDetailResponse>(MapDetail(result)));
    }

    /// <summary>Bulk soft-delete pages.</summary>
    [HttpPost("bulk-delete")]
    [RequireAppPermission(PermissionCodes.PagesDelete, AppAccessResolver.ByAppId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> BulkDelete(Guid appId, [FromBody] BulkDeletePagesRequest request, CancellationToken ct)
    {
        await _deleteHandler.HandleAsync(new DeletePagesCommand(appId, request.PublicIds), ct);
        return NoContent();
    }

    /// <summary>Set (or clear, with a null PagePublicId) the app's default home Dashboard page.
    /// Only one page per app can be the default home — setting a new one atomically replaces
    /// any previous default.</summary>
    [HttpPost("default-home")]
    [RequireAppPermission(PermissionCodes.PagesUpdate, AppAccessResolver.ByAppId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefaultHome(Guid appId, [FromBody] SetDefaultHomeRequest request, CancellationToken ct)
    {
        await _setDefaultHomeHandler.HandleAsync(new SetDefaultHomeCommand(appId, request.PagePublicId), ct);
        return NoContent();
    }

    /// <summary>Duplicate a page. The copy starts unpublished and Personal-visibility.</summary>
    [HttpPost("{publicId:guid}/duplicate")]
    [RequireAppPermission(PermissionCodes.PagesCreate, AppAccessResolver.ByPagePublicId)]
    [ProducesResponseType(typeof(ApiResponse<PageDetailResponse>), StatusCodes.Status201Created)]
    public async Task<IActionResult> Duplicate(Guid appId, Guid publicId, [FromBody] DuplicatePageRequest request, CancellationToken ct)
    {
        var result = await _duplicateHandler.HandleAsync(new DuplicatePageCommand(publicId, request.NewName), ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<PageDetailResponse>(MapDetail(result)));
    }

    /// <summary>Publish or unpublish a page.</summary>
    [HttpPost("{publicId:guid}/publish")]
    [RequireAppPermission(PermissionCodes.PagesPublish, AppAccessResolver.ByPagePublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> Publish(Guid appId, Guid publicId, [FromBody] PublishPageRequest request, CancellationToken ct)
    {
        await _publishHandler.HandleAsync(new PublishPageCommand(publicId, request.IsPublished), ct);
        return NoContent();
    }

    /// <summary>List version history for a page, newest first.</summary>
    [HttpGet("{publicId:guid}/versions")]
    [RequireAppPermission(PermissionCodes.PagesRead, AppAccessResolver.ByPagePublicId)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PageVersionResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> ListVersions(Guid appId, Guid publicId, CancellationToken ct)
    {
        var versions = await _listVersionsHandler.HandleAsync(new ListPageVersionsQuery(publicId), ct);
        return Ok(new ApiResponse<IReadOnlyList<PageVersionResponse>>(versions.Select(MapVersion).ToList()));
    }

    /// <summary>Restore a page to an earlier version. Requires a change note; writes a NEW
    /// version rather than mutating history.</summary>
    [HttpPost("{publicId:guid}/versions/{versionNo:int}/restore")]
    [RequireAppPermission(PermissionCodes.PagesUpdate, AppAccessResolver.ByPagePublicId)]
    [ProducesResponseType(typeof(ApiResponse<PageDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RestoreVersion(Guid appId, Guid publicId, int versionNo, [FromBody] RestorePageVersionRequest request, CancellationToken ct)
    {
        var result = await _restoreHandler.HandleAsync(new RestorePageVersionCommand(publicId, versionNo, request.ChangeNotes), ct);
        return Ok(new ApiResponse<PageDetailResponse>(MapDetail(result)));
    }

    /// <summary>Render a Dashboard page: runs every widget's report and returns per-widget
    /// results. A widget whose report the caller can't see comes back as a "forbidden"
    /// placeholder rather than failing the whole page. Membership-only (not builder-only)
    /// since this is the viewer-facing render path.</summary>
    [HttpPost("{publicId:guid}/render")]
    [RequireAppMember(AppAccessResolver.ByPagePublicId)]
    [ProducesResponseType(typeof(ApiResponse<RenderPageResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Render(Guid appId, Guid publicId, [FromBody] RenderPageRequest request, CancellationToken ct)
    {
        IReadOnlyDictionary<string, IReadOnlyList<string>>? filterValues = request.FilterValues?
            .ToDictionary(kv => kv.Key, kv => (IReadOnlyList<string>)kv.Value);
        var result = await _renderHandler.HandleAsync(new RenderPageQuery(publicId, filterValues, request.SearchValues), ct);
        return Ok(new ApiResponse<RenderPageResponse>(MapRender(result)));
    }

    private static RenderPageResponse MapRender(Application.Pages.Queries.RenderPage.RenderPageResult r) => new()
    {
        Filters = r.Filters.Select(f => new DashboardFilterSlotResponse
        {
            Key = f.Key,
            Label = f.Label,
            InputType = f.InputType,
            DefaultValue = f.DefaultValue,
        }).ToList(),
        Tabs = r.Tabs.Select(t => new RenderedTabResponse
        {
            Id = t.Id,
            Name = t.Name,
            Grid = new DashboardGridResponse { Cols = t.Grid.Cols, RowHeight = t.Grid.RowHeight, Gap = t.Grid.Gap },
            Widgets = t.Widgets.Select(w => new RenderedWidgetResponse
            {
                WidgetId = w.WidgetId,
                WidgetType = w.WidgetType,
                Status = w.Status,
                Message = w.Message,
                Title = w.Title,
                ShowTitle = w.ShowTitle,
                Layout = new DashboardWidgetLayoutResponse { X = w.Layout.X, Y = w.Layout.Y, W = w.Layout.W, H = w.Layout.H },
                BackgroundColor = w.BackgroundColor,
                ReportPublicId = w.ReportPublicId,
                TableId = w.TableId,
                ReportType = w.ReportType,
                Chart = MapChartConfigDto(w.Chart),
                Result = w.Result is null ? null : new ReportRunResponse
                {
                    Columns = w.Result.Columns.Select(c => new ReportColumnDto { FieldId = c.FieldId, Name = c.Name, TypeCode = c.TypeCode }).ToList(),
                    Rows = w.Result.Items.Select(i => new RecordResponse
                    {
                        Id = i.Id,
                        CreatedOn = i.CreatedOn,
                        ModifiedOn = i.ModifiedOn,
                        CreatedBy = i.CreatedBy,
                        Fields = i.Fields,
                    }).ToList(),
                    TotalCount = w.Result.TotalCount,
                    Page = w.Result.Page,
                    PageSize = w.Result.PageSize,
                },
                Content = w.Content,
                Buttons = w.Buttons.Select(b => new DashboardBarButtonResponse
                {
                    Id = b.Id,
                    Label = b.Label,
                    Icon = b.Icon,
                    ActionType = b.ActionType,
                    Url = b.Url,
                    TargetPageId = b.TargetPageId,
                    TargetReportId = b.TargetReportId,
                    TargetTableId = b.TargetTableId,
                    OpenInNewTab = b.OpenInNewTab,
                    BackgroundColor = b.BackgroundColor,
                    IconAlign = b.IconAlign,
                }).ToList(),
                WebPageUrl = w.WebPageUrl,
                Links = w.Links.Select(l => new DashboardBarLinkResponse
                {
                    Id = l.Id,
                    Label = l.Label,
                    Url = l.Url,
                    OpenInNewTab = l.OpenInNewTab,
                }).ToList(),
                SearchTargetTableId = w.SearchTargetTableId,
                SearchScope = w.SearchScope,
                SearchFieldIds = w.SearchFieldIds,
                SearchBoxStyle = w.SearchBoxStyle,
                SearchReportId = w.SearchReportId,
                SearchType = w.SearchType,
                SearchShowResultsIn = w.SearchShowResultsIn,
                SearchShowTitle = w.SearchShowTitle,
                SearchShowHintText = w.SearchShowHintText,
                SearchPlaceholder = w.SearchPlaceholder,
                SpacerShowDivider = w.SpacerShowDivider,
            }).ToList(),
        }).ToList(),
    };

    private static ChartConfigDto? MapChartConfigDto(Application.Reports.ChartConfig? chart)
    {
        if (chart is null) return null;
        return new ChartConfigDto
        {
            ChartType = chart.ChartType,
            SeriesFieldId = chart.SeriesFieldId,
            SeriesMode = chart.SeriesMode,
            AxisLabelX = chart.AxisLabelX,
            AxisLabelY = chart.AxisLabelY,
            YMin = chart.YMin,
            YMax = chart.YMax,
            LogScale = chart.LogScale,
            SortBy = chart.SortBy,
            SortDirection = chart.SortDirection,
            GoalValue = chart.GoalValue,
            GoalLabel = chart.GoalLabel,
            DataLabelsVisible = chart.DataLabelsVisible,
            HideMissingCategories = chart.HideMissingCategories,
            DrilldownReportId = chart.DrilldownReportId,
            SecondaryAxisAggregationFieldIds = chart.SecondaryAxisAggregationFieldIds,
            AxisLabelY2 = chart.AxisLabelY2,
            YMin2 = chart.YMin2,
            YMax2 = chart.YMax2,
            LogScale2 = chart.LogScale2,
            GaugeFieldId = chart.GaugeFieldId,
            GaugeLowMaxPercent = chart.GaugeLowMaxPercent,
            GaugeMediumMaxPercent = chart.GaugeMediumMaxPercent,
        };
    }

    private static PageListItemResponse MapListItem(PageListItemDto p) => new()
    {
        Id = p.Id,
        PageNumber = p.PageNumber,
        PageType = p.PageType,
        Name = p.Name,
        Description = p.Description,
        Visibility = p.Visibility,
        IsPublished = p.IsPublished,
        ShowInNav = p.ShowInNav,
        NavOrder = p.NavOrder,
        IsDefaultHome = p.IsDefaultHome,
        HomePageForRoles = p.HomePageForRoles,
        CreatedOn = p.CreatedOn,
        ModifiedOn = p.ModifiedOn,
    };

    private static PageDetailResponse MapDetail(PageDetailDto p) => new()
    {
        Id = p.Id,
        PageNumber = p.PageNumber,
        PageType = p.PageType,
        Name = p.Name,
        Description = p.Description,
        Visibility = p.Visibility,
        VisibleToRoleIds = p.VisibleToRoleIds,
        Definition = p.Definition,
        ContentType = p.ContentType,
        CodeHtml = p.CodeHtml,
        CodeCss = p.CodeCss,
        CodeJs = p.CodeJs,
        IsPublished = p.IsPublished,
        CurrentVersionNo = p.CurrentVersionNo,
        PublishedVersionNo = p.PublishedVersionNo,
        ShowInNav = p.ShowInNav,
        NavOrder = p.NavOrder,
        NavIcon = p.NavIcon,
        IsDefaultHome = p.IsDefaultHome,
        CreatedOn = p.CreatedOn,
        ModifiedOn = p.ModifiedOn,
    };

    private static PageVersionResponse MapVersion(PageVersionDto v) => new()
    {
        Id = v.Id,
        VersionNo = v.VersionNo,
        PageType = v.PageType,
        Name = v.Name,
        Description = v.Description,
        Definition = v.Definition,
        CodeHtml = v.CodeHtml,
        CodeCss = v.CodeCss,
        CodeJs = v.CodeJs,
        ChangeNotes = v.ChangeNotes,
        WasPublished = v.WasPublished,
        CreatedOn = v.CreatedOn,
        CreatedBy = v.CreatedBy,
    };
}
