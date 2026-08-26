using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Common.Interfaces;
using PowerBase.API.Models;
using PowerBase.API.Models.Records;
using PowerBase.API.Models.Reports;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Application.Reports.Commands.DeleteReport;
using PowerBase.Application.Reports.Commands.SetDefaultReport;
using PowerBase.Application.Reports.Commands.UpdateDefaultReportSettings;
using PowerBase.Application.Reports.Commands.UpdateReport;
using PowerBase.Application.Reports.Queries.GetReport;
using PowerBase.Application.Reports.Queries.GetDefaultReportSettings;
using PowerBase.Application.Reports.Queries.ListReports;
using PowerBase.Application.Reports.Queries.ListReportsByTable;
using PowerBase.Application.Reports.Queries.ListReportsByTablePaged;
using PowerBase.Application.Reports.Queries.ExportReport;
using PowerBase.Application.Reports.Queries.ResolveDefaultReport;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Application.Reports.Commands.UpdateReportFormOverrides;
using PowerBase.Application.Forms.Queries.ListForms;
using PowerBase.API.Models.Forms;
using PowerBase.Application.Reports.Queries.GetReportPreviewMetadata;
using PowerBase.Domain.Constants;

namespace PowerBase.API.Controllers;

[ApiController]
public class ReportsController : ControllerBase
{
    private readonly CreateReportCommandHandler _createHandler;
    private readonly UpdateReportCommandHandler _updateHandler;
    private readonly DeleteReportCommandHandler _deleteHandler;
    private readonly SetDefaultReportCommandHandler _setDefaultHandler;
    private readonly GetReportQueryHandler _getHandler;
    private readonly ListReportsQueryHandler _listHandler;
    private readonly ListReportsByTableQueryHandler _listByTableHandler;
    private readonly ListReportsByTablePagedQueryHandler _listByTablePagedHandler;
    private readonly RunReportQueryHandler _runHandler;
    private readonly ExportReportQueryHandler _exportHandler;
    private readonly GetDefaultReportSettingsQueryHandler _getDefaultSettingsHandler;
    private readonly UpdateDefaultReportSettingsCommandHandler _updateDefaultSettingsHandler;
    private readonly ResolveDefaultReportQueryHandler _resolveDefaultReportHandler;
    private readonly UpdateReportFormOverridesCommandHandler _updateReportFormOverridesHandler;
    private readonly ListFormsQueryHandler _listFormsHandler;
    private readonly GetReportPreviewMetadataQueryHandler _previewMetadataHandler;

    public ReportsController(
        CreateReportCommandHandler createHandler,
        UpdateReportCommandHandler updateHandler,
        DeleteReportCommandHandler deleteHandler,
        SetDefaultReportCommandHandler setDefaultHandler,
        GetReportQueryHandler getHandler,
        ListReportsQueryHandler listHandler,
        ListReportsByTableQueryHandler listByTableHandler,
        ListReportsByTablePagedQueryHandler listByTablePagedHandler,
        RunReportQueryHandler runHandler,
        ExportReportQueryHandler exportHandler,
        GetDefaultReportSettingsQueryHandler getDefaultSettingsHandler,
        UpdateDefaultReportSettingsCommandHandler updateDefaultSettingsHandler,
        ResolveDefaultReportQueryHandler resolveDefaultReportHandler,
        UpdateReportFormOverridesCommandHandler updateReportFormOverridesHandler,
        ListFormsQueryHandler listFormsHandler,
        GetReportPreviewMetadataQueryHandler previewMetadataHandler)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _setDefaultHandler = setDefaultHandler;
        _getHandler = getHandler;
        _listHandler = listHandler;
        _listByTableHandler = listByTableHandler;
        _listByTablePagedHandler = listByTablePagedHandler;
        _runHandler = runHandler;
        _exportHandler = exportHandler;
        _getDefaultSettingsHandler = getDefaultSettingsHandler;
        _updateDefaultSettingsHandler = updateDefaultSettingsHandler;
        _resolveDefaultReportHandler = resolveDefaultReportHandler;
        _updateReportFormOverridesHandler = updateReportFormOverridesHandler;
        _listFormsHandler = listFormsHandler;
        _previewMetadataHandler = previewMetadataHandler;
    }

    /// <summary>Save a report definition for a table.</summary>
    [HttpPost("tables/{tableId:guid}/reports")]
    [RequireAppPermission(PermissionCodes.ReportsCreate, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<ReportResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(Guid tableId, [FromBody] CreateReportRequest request, CancellationToken ct)
    {
        var command = new CreateReportCommand(
            tableId,
            request.Name,
            request.Description,
            request.Visibility,
            request.ReportType,
            request.Columns,
            request.SortFields.Select(s => new SortSpec { FieldId = s.FieldId, Desc = s.Desc }).ToList(),
            MapFilterGroup(request.FilterTree),
            request.GroupByFieldId,
            request.GroupByMode,
            request.HideTotals,
            request.GroupDefaultCollapsed,
            request.GroupByDescending,
            request.Aggregations.Select(a => new SummaryAggregationCommand(a.FieldId, a.Function, a.DisplayAs)).ToList(),
            request.DynamicFilterType,
            request.CustomDynamicFilterFields,
            request.CustomDynamicFilterItems?.Select(i => new CustomDynamicFilterItem { FieldId = i.FieldId, SubField = i.SubField }).ToList(),
            request.AllowQuickSearch,
            request.VisibleToRoleIds ?? [],
            MapChartConfig(request.Chart));
        var result = await _createHandler.HandleAsync(command, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<ReportResponse>(MapToResponse(result)));
    }

    /// <summary>List reports for a table. Supports paging, search (by name), and sorting (name,
    /// reportType, visibility, isDefault, createdOn). Same role-based visibility rules as before —
    /// a user only sees Shared reports, their own Personal reports, and role-scoped reports their
    /// role grants.</summary>
    [HttpGet("tables/{tableId:guid}/reports")]
    [RequireAppPermission(PermissionCodes.ReportsRead, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiListResponse<ReportListItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListByTable(
        Guid tableId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        [FromQuery] string sortBy = "name",
        [FromQuery] string sortDirection = "asc",
        CancellationToken ct = default)
    {
        var sortDesc = string.Equals(sortDirection, "desc", StringComparison.OrdinalIgnoreCase);
        var result = await _listByTablePagedHandler.HandleAsync(new ListReportsByTablePagedQuery(tableId, page, pageSize, search, sortBy, sortDesc), ct);
        var items = result.Items.Select(MapToListItemResponse).ToList();
        return Ok(new ApiListResponse<ReportListItemResponse>(items, result.Total, result.Page, result.PageSize));
    }

    /// <summary>Get report form settings.</summary>
    [HttpGet("tables/{tableId:guid}/reports/form-settings")]
    [RequireAppPermission(PermissionCodes.ReportsRead, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<ReportFormSettingsResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetReportFormSettings(Guid tableId, CancellationToken ct)
    {
        var reports = await _listByTableHandler.HandleAsync(new ListReportsByTableQuery(tableId), ct);
        var forms = await _listFormsHandler.HandleAsync(new ListFormsQuery(tableId), ct);

        var response = new ReportFormSettingsResponse
        {
            Reports = reports.Select(r => new ReportFormOverrideResponse
            {
                ReportId = r.Id,
                ReportName = r.Name,
                FormId = r.ViewEditFormId
            }).ToList(),
            Forms = forms.Select(f => new FormListItemResponse
            {
                Id = f.Id,
                Name = f.Name,
                IsDefault = f.IsDefault
            }).ToList()
        };

        return Ok(new ApiResponse<ReportFormSettingsResponse>(response));
    }

    /// <summary>Update report form settings.</summary>
    [HttpPut("tables/{tableId:guid}/reports/form-settings")]
    [RequireAppPermission(PermissionCodes.ReportsUpdate, AppAccessResolver.ByTableId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    public async Task<IActionResult> UpdateReportFormSettings(Guid tableId, [FromBody] UpdateReportFormSettingsRequest request, CancellationToken ct)
    {
        var overrides = request.ReportOverrides.Select(o => new ReportFormOverrideCommandDto(o.ReportId, o.FormId)).ToList();
        await _updateReportFormOverridesHandler.HandleAsync(new UpdateReportFormOverridesCommand(tableId, overrides), ct);
        return NoContent();
    }

    /// <summary>List all reports for an app.</summary>
    [HttpGet("apps/{appId:guid}/reports")]
    [RequireAppPermission(PermissionCodes.ReportsRead, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ReportResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(Guid appId, CancellationToken ct)
    {
        var results = await _listHandler.HandleAsync(new ListReportsQuery(appId), ct);
        var items = results.Select(MapToResponse).ToList();
        return Ok(new ApiResponse<IReadOnlyList<ReportResponse>>(items));
    }

    /// <summary>Update a report's definition (name, description, columns, filters, sort, grouping, aggregations).</summary>
    [HttpPatch("reports/{publicId:guid}")]
    [RequireAppPermission(PermissionCodes.ReportsUpdate, AppAccessResolver.ByReportPublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid publicId, [FromBody] UpdateReportRequest request, CancellationToken ct)
    {
        var command = new UpdateReportCommand(
            publicId,
            request.Name,
            request.Description,
            request.Visibility,
            request.Columns,
            request.SortFields.Select(s => new SortSpec { FieldId = s.FieldId, Desc = s.Desc }).ToList(),
            MapFilterGroup(request.FilterTree),
            request.GroupByFieldId,
            request.GroupByMode,
            request.HideTotals,
            request.GroupDefaultCollapsed,
            request.GroupByDescending,
            request.Aggregations.Select(a => new SummaryAggregationCommand(a.FieldId, a.Function, a.DisplayAs)).ToList(),
            request.DynamicFilterType,
            request.CustomDynamicFilterFields,
            request.CustomDynamicFilterItems?.Select(i => new CustomDynamicFilterItem { FieldId = i.FieldId, SubField = i.SubField }).ToList(),
            request.AllowQuickSearch,
            request.VisibleToRoleIds ?? [],
            MapChartConfig(request.Chart));
        await _updateHandler.HandleAsync(command, ct);
        return NoContent();
    }

    /// <summary>Set a report as the default for its table. Clears any previously set default.</summary>
    [HttpPatch("tables/{tableId:guid}/reports/{reportId:guid}/set-default")]
    [RequireAppPermission(PermissionCodes.ReportsUpdate, AppAccessResolver.ByTableId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefault(Guid tableId, Guid reportId, CancellationToken ct)
    {
        await _setDefaultHandler.HandleAsync(new SetDefaultReportCommand(tableId, reportId), ct);
        return NoContent();
    }

    /// <summary>Get default report settings for a table, including app roles and selectable reports.</summary>
    [HttpGet("tables/{tableId:guid}/default-report-settings")]
    [RequireAppPermission(PermissionCodes.ReportsRead, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<DefaultReportSettingsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDefaultReportSettings(Guid tableId, CancellationToken ct)
    {
        var result = await _getDefaultSettingsHandler.HandleAsync(new GetDefaultReportSettingsQuery(tableId), ct);
        return Ok(new ApiResponse<DefaultReportSettingsResponse>(MapToDefaultSettingsResponse(result)));
    }

    /// <summary>Update default report settings for a table.</summary>
    [HttpPut("tables/{tableId:guid}/default-report-settings")]
    [RequireAppPermission(PermissionCodes.TablesUpdate, AppAccessResolver.ByTableId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateDefaultReportSettings(Guid tableId, [FromBody] UpdateDefaultReportSettingsRequest request, CancellationToken ct)
    {
        await _updateDefaultSettingsHandler.HandleAsync(new UpdateDefaultReportSettingsCommand(
            tableId,
            request.Mode,
            request.EveryoneReportId,
            request.RoleDefaults), ct);
        return NoContent();
    }

    /// <summary>Resolve the effective default report for the current user and table.</summary>
    [HttpGet("tables/{tableId:guid}/default-report")]
    [RequireAppPermission(PermissionCodes.ReportsRead, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<ReportResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetDefaultReport(Guid tableId, CancellationToken ct)
    {
        var result = await _resolveDefaultReportHandler.HandleAsync(new ResolveDefaultReportQuery(tableId), ct);
        return Ok(new ApiResponse<ReportResponse>(MapToResponse(result)));
    }

    /// <summary>Soft-delete a report.</summary>
    [HttpDelete("reports/{publicId:guid}")]
    [RequireAppPermission(PermissionCodes.ReportsDelete, AppAccessResolver.ByReportPublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid publicId, CancellationToken ct)
    {
        await _deleteHandler.HandleAsync(new DeleteReportCommand(publicId), ct);
        return NoContent();
    }

    /// <summary>Get a single report definition.</summary>
    [HttpGet("reports/{publicId:guid}")]
    [RequireAppPermission(PermissionCodes.ReportsRead, AppAccessResolver.ByReportPublicId)]
    [ProducesResponseType(typeof(ApiResponse<ReportResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid publicId, CancellationToken ct)
    {
        var result = await _getHandler.HandleAsync(new GetReportQuery(publicId), ct);
        return Ok(new ApiResponse<ReportResponse>(MapToResponse(result)));
    }

    /// <summary>API 1.4: Report Preview (Aggregate Only) — Return only row counts and summary totals for report builders, never raw record data.</summary>
    [HttpGet("reports/{publicId:guid}/preview-metadata")]
    [HttpGet("api/v1/reports/{publicId:guid}/preview-metadata")]
    [RequireAppMember(AppAccessResolver.ByReportPublicId)]
    [ProducesResponseType(typeof(ApiResponse<ReportPreviewMetadataDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetPreviewMetadata(Guid publicId, CancellationToken ct)
    {
        var result = await _previewMetadataHandler.HandleAsync(new GetReportPreviewMetadataQuery(publicId), ct);
        return Ok(new ApiResponse<ReportPreviewMetadataDto>(result));
    }

    /// <summary>Execute a report and return paged results. Pass dynamic filter values as dynamicFilters=fieldId:value (repeatable).</summary>
    [HttpGet("reports/{publicId:guid}/run")]
    [RequireAppMember(AppAccessResolver.ByReportPublicId)]
    [ProducesResponseType(typeof(ApiResponse<ReportRunResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Run(
        Guid publicId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] List<string>? dynamicFilters = null,
        [FromQuery] string? quickSearch = null,
        [FromQuery] List<long>? searchFieldIds = null,
        [FromQuery] bool exactMatch = false,
        CancellationToken ct = default)
    {
        var runtimeFilters = ParseDynamicFilters(dynamicFilters);
        var result = await _runHandler.HandleAsync(
            new RunReportQuery(publicId, page, pageSize, runtimeFilters, quickSearch, searchFieldIds, exactMatch), ct);
        return Ok(new ApiResponse<ReportRunResponse>(ToRunResponse(result)));
    }

    /// <summary>
    /// Execute a report with ad-hoc (not persisted) filtering/sorting/grouping — the Advanced
    /// filter builder, per-column filters, header-click sort, and the per-column grouping menu
    /// all go through this instead of GET run, since a nested FilterTree can be arbitrarily deep
    /// and would risk hitting URL length limits as a query string.
    /// </summary>
    [HttpPost("reports/{publicId:guid}/run")]
    [RequireAppMember(AppAccessResolver.ByReportPublicId)]
    [ProducesResponseType(typeof(ApiResponse<ReportRunResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> RunPost(Guid publicId, [FromBody] RunReportRequest request, CancellationToken ct = default)
    {
        var runtimeFilters = ParseDynamicFilters(request.DynamicFilters);
        var result = await _runHandler.HandleAsync(new RunReportQuery(
            publicId,
            request.Page,
            request.PageSize,
            runtimeFilters,
            request.QuickSearch,
            request.SearchFieldIds,
            request.ExactMatch,
            MapFilterGroup(request.FilterTree),
            request.SortFieldId,
            request.SortDesc,
            request.GroupByFieldId,
            request.GroupByDesc,
            request.ClearGrouping), ct);
        return Ok(new ApiResponse<ReportRunResponse>(ToRunResponse(result)));
    }

    private static ReportRunResponse ToRunResponse(PagedReportRunResult result) => new()
    {
        Columns = result.Columns.Select(c => new ReportColumnDto
        {
            FieldId = c.FieldId,
            Name = c.Name,
            TypeCode = c.TypeCode,
        }).ToList(),
        Rows = result.Items.Select(r => new RecordResponse
        {
            Id = r.Id,
            CreatedOn = r.CreatedOn,
            ModifiedOn = r.ModifiedOn,
            CreatedBy = r.CreatedBy,
            Fields = r.Fields,
        }).ToList(),
        TotalCount = result.TotalCount,
        Page = result.Page,
        PageSize = result.PageSize,
        IsDataMasked = result.IsDataMasked,
    };

    /// <summary>Export report results as CSV.</summary>
    [HttpGet("reports/{publicId:guid}/export/csv")]
    [RequireAppMember(AppAccessResolver.ByReportPublicId)]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportCsv(Guid publicId, CancellationToken ct)
    {
        var result = await _exportHandler.HandleAsync(new ExportReportQuery(publicId, "csv"), ct);
        return File(result.Content, result.ContentType, result.FileName);
    }

    /// <summary>Export report results as Excel (.xlsx).</summary>
    [HttpGet("reports/{publicId:guid}/export/xlsx")]
    [RequireAppMember(AppAccessResolver.ByReportPublicId)]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ExportXlsx(Guid publicId, CancellationToken ct)
    {
        var result = await _exportHandler.HandleAsync(new ExportReportQuery(publicId, "xlsx"), ct);
        return File(result.Content, result.ContentType, result.FileName);
    }

    private static IReadOnlyList<(long FieldId, string Value, string? SubField)>? ParseDynamicFilters(List<string>? raw)
    {
        if (raw is null or { Count: 0 }) return null;
        var result = new List<(long, string, string?)>();
        foreach (var item in raw)
        {
            // Format: "fieldId:value" OR "fieldId__subfield:value" (for address sub-fields)
            var idx = item.IndexOf(':');
            if (idx > 0 && long.TryParse(item[..idx].Split("__")[0], out var fid))
            {
                var fieldPart = item[..idx];
                var value = item[(idx + 1)..];
                string? subField = null;
                var dunderIdx = fieldPart.IndexOf("__");
                if (dunderIdx > 0)
                    subField = fieldPart[(dunderIdx + 2)..];
                result.Add((fid, value, subField));
            }
        }
        return result.Count > 0 ? result : null;
    }

    private static ReportListItemResponse MapToListItemResponse(PowerBase.Application.Common.Models.ReportListItemDto r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Description = r.Description,
        ReportType = r.ReportType,
        Visibility = r.Visibility,
        IsDefault = r.IsDefault,
        CreatedOn = r.CreatedOn,
    };

    private static ReportResponse MapToResponse(ReportDetailResult r) => new()
    {
        Id = r.Id,
        Name = r.Name,
        Description = r.Description,
        ReportType = r.ReportType,
        Visibility = r.Visibility,
        Definition = new ReportDefinitionDto
        {
            Columns = r.Definition.Columns,
            SortFields = r.Definition.SortFields.Select(s => new SortSpecDto { FieldId = s.FieldId, Desc = s.Desc }).ToList(),
            FilterTree = MapFilterGroupDto(r.Definition.FilterTree),
            // Legacy compat fields
            SortFieldId = r.Definition.SortFieldId,
            SortDesc = r.Definition.SortDesc,
            Filters = r.Definition.Filters.Select(f => new ReportFilterDto
            {
                FieldId = f.FieldId,
                Operator = f.Operator,
                Value = f.Value,
            }).ToList(),
            GroupByFieldId = r.Definition.GroupByFieldId,
            GroupByMode = r.Definition.GroupByMode,
            HideTotals = r.Definition.HideTotals,
            GroupDefaultCollapsed = r.Definition.GroupDefaultCollapsed,
            GroupByDescending = r.Definition.GroupByDescending,
            Aggregations = r.Definition.Aggregations.Select(a => new SummaryAggregationDto
            {
                FieldId = a.FieldId,
                Function = a.Function,
                DisplayAs = a.DisplayAs,
            }).ToList(),
            DynamicFilterType = r.Definition.DynamicFilterType,
            CustomDynamicFilterFields = r.Definition.CustomDynamicFilterFields,
            CustomDynamicFilterItems = r.Definition.CustomDynamicFilterItems?.Select(i => new CustomDynamicFilterItemDto
            {
                FieldId = i.FieldId,
                SubField = i.SubField
            }).ToList() ?? [],
            AllowQuickSearch = r.Definition.AllowQuickSearch,
            Chart = MapChartConfigDto(r.Definition.Chart),
        },
        IsDefault = r.IsDefault,
        DisplayOrder = r.DisplayOrder,
        ViewEditFormId = r.ViewEditFormId,
        TableId = r.TableId,
        TableName = r.TableName,
        CreatedOn = r.CreatedOn,
        VisibleToRoleIds = r.VisibleToRoleIds,
    };

    private static DefaultReportSettingsResponse MapToDefaultSettingsResponse(DefaultReportSettingsResult r) => new()
    {
        Mode = r.Mode,
        EveryoneReportId = r.EveryoneReportId,
        Roles = r.Roles.Select(role => new RoleDefaultResponse
        {
            RoleId = role.RoleId,
            RoleName = role.RoleName,
            ReportId = role.ReportId,
        }).ToList(),
        Reports = r.Reports.Select(report => new DefaultReportListItemResponse
        {
            Id = report.Id,
            Name = report.Name,
            IsDefault = report.IsDefault,
        }).ToList(),
    };

    // ── Filter group mapping helpers ──────────────────────────────────────────

    private static FilterGroup? MapFilterGroup(FilterGroupRequest? req)
    {
        if (req is null) return null;
        return new FilterGroup
        {
            Logic = req.Logic,
            Nodes = req.Nodes.Select(n => new FilterNode
            {
                Condition = n.Condition is null ? null : new FilterCondition
                {
                    FieldId = n.Condition.FieldId,
                    Operator = n.Condition.Operator,
                    Value = n.Condition.Value,
                    SubField = n.Condition.SubField,
                },
                Group = MapFilterGroup(n.Group),
            }).ToList(),
        };
    }

    private static FilterGroupDto? MapFilterGroupDto(FilterGroup? group)
    {
        if (group is null) return null;
        return new FilterGroupDto
        {
            Logic = group.Logic,
            Nodes = group.Nodes.Select(n => new FilterNodeDto
            {
                Condition = n.Condition is null ? null : new FilterConditionDto
                {
                    FieldId = n.Condition.FieldId,
                    Operator = n.Condition.Operator,
                    Value = n.Condition.Value,
                    SubField = n.Condition.SubField,
                },
                Group = MapFilterGroupDto(n.Group),
            }).ToList(),
        };
    }

    // ── Chart config mapping helpers ──────────────────────────────────────────

    private static ChartConfigCommand? MapChartConfig(ChartConfigRequest? req)
    {
        if (req is null) return null;
        return new ChartConfigCommand(
            req.ChartType,
            req.SeriesFieldId,
            req.SeriesMode,
            req.AxisLabelX,
            req.AxisLabelY,
            req.YMin,
            req.YMax,
            req.LogScale,
            req.SortBy,
            req.SortDirection,
            req.GoalValue,
            req.GoalLabel,
            req.DataLabelsVisible,
            req.HideMissingCategories,
            req.DrilldownReportId,
            req.SecondaryAxisAggregationFieldIds,
            req.AxisLabelY2,
            req.YMin2,
            req.YMax2,
            req.LogScale2,
            req.GaugeFieldId,
            req.GaugeLowMaxPercent,
            req.GaugeMediumMaxPercent);
    }

    private static ChartConfigDto? MapChartConfigDto(ChartConfig? chart)
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
}
