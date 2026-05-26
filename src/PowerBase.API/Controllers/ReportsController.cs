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
using PowerBase.Application.Reports.Commands.UpdateReport;
using PowerBase.Application.Reports.Queries.GetReport;
using PowerBase.Application.Reports.Queries.ListReports;
using PowerBase.Application.Reports.Queries.ListReportsByTable;
using PowerBase.Application.Reports.Queries.RunReport;
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
    private readonly RunReportQueryHandler _runHandler;

    public ReportsController(
        CreateReportCommandHandler createHandler,
        UpdateReportCommandHandler updateHandler,
        DeleteReportCommandHandler deleteHandler,
        SetDefaultReportCommandHandler setDefaultHandler,
        GetReportQueryHandler getHandler,
        ListReportsQueryHandler listHandler,
        ListReportsByTableQueryHandler listByTableHandler,
        RunReportQueryHandler runHandler)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _setDefaultHandler = setDefaultHandler;
        _getHandler = getHandler;
        _listHandler = listHandler;
        _listByTableHandler = listByTableHandler;
        _runHandler = runHandler;
    }

    /// <summary>Save a report definition for a table.</summary>
    [HttpPost("tables/{tableId:guid}/reports")]
    [RequirePermission(PermissionCodes.ReportsCreate)]
    [RequireAppAccess(AppAccess.Admin, AppAccessResolver.ByTableId)]
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
            request.AllowQuickSearch);
        var result = await _createHandler.HandleAsync(command, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<ReportResponse>(MapToResponse(result)));
    }

    /// <summary>List all reports for a specific table.</summary>
    [HttpGet("tables/{tableId:guid}/reports")]
    [RequirePermission(PermissionCodes.ReportsRead)]
    [RequireAppAccess(AppAccess.Read, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<ReportResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListByTable(Guid tableId, CancellationToken ct)
    {
        var results = await _listByTableHandler.HandleAsync(new ListReportsByTableQuery(tableId), ct);
        var items = results.Select(MapToResponse).ToList();
        return Ok(new ApiResponse<IReadOnlyList<ReportResponse>>(items));
    }

    /// <summary>List all reports for an app.</summary>
    [HttpGet("apps/{appId:guid}/reports")]
    [RequirePermission(PermissionCodes.ReportsRead)]
    [RequireAppAccess(AppAccess.Read, AppAccessResolver.ByAppId)]
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
    [RequirePermission(PermissionCodes.ReportsUpdate)]
    [RequireAppAccess(AppAccess.Admin, AppAccessResolver.ByReportPublicId)]
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
            request.AllowQuickSearch);
        await _updateHandler.HandleAsync(command, ct);
        return NoContent();
    }

    /// <summary>Set a report as the default for its table. Clears any previously set default.</summary>
    [HttpPatch("tables/{tableId:guid}/reports/{reportId:guid}/set-default")]
    [RequirePermission(PermissionCodes.ReportsUpdate)]
    [RequireAppAccess(AppAccess.Admin, AppAccessResolver.ByTableId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> SetDefault(Guid tableId, Guid reportId, CancellationToken ct)
    {
        await _setDefaultHandler.HandleAsync(new SetDefaultReportCommand(tableId, reportId), ct);
        return NoContent();
    }

    /// <summary>Soft-delete a report.</summary>
    [HttpDelete("reports/{publicId:guid}")]
    [RequirePermission(PermissionCodes.ReportsDelete)]
    [RequireAppAccess(AppAccess.Admin, AppAccessResolver.ByReportPublicId)]
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
    [RequirePermission(PermissionCodes.ReportsRead)]
    [RequireAppAccess(AppAccess.Read, AppAccessResolver.ByReportPublicId)]
    [ProducesResponseType(typeof(ApiResponse<ReportResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid publicId, CancellationToken ct)
    {
        var result = await _getHandler.HandleAsync(new GetReportQuery(publicId), ct);
        return Ok(new ApiResponse<ReportResponse>(MapToResponse(result)));
    }

    /// <summary>Execute a report and return paged results. Pass dynamic filter values as dynamicFilters=fieldId:value (repeatable).</summary>
    [HttpGet("reports/{publicId:guid}/run")]
    [RequirePermission(PermissionCodes.ReportsRun)]
    [RequireAppAccess(AppAccess.Read, AppAccessResolver.ByReportPublicId)]
    [ProducesResponseType(typeof(ApiResponse<ReportRunResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Run(
        Guid publicId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] List<string>? dynamicFilters = null,
        CancellationToken ct = default)
    {
        var runtimeFilters = ParseDynamicFilters(dynamicFilters);
        var result = await _runHandler.HandleAsync(new RunReportQuery(publicId, page, pageSize, runtimeFilters), ct);
        var response = new ReportRunResponse
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
                Fields = r.Fields,
            }).ToList(),
            TotalCount = result.TotalCount,
            Page = result.Page,
            PageSize = result.PageSize,
        };
        return Ok(new ApiResponse<ReportRunResponse>(response));
    }

    private static IReadOnlyList<(long FieldId, string Value)>? ParseDynamicFilters(List<string>? raw)
    {
        if (raw is null or { Count: 0 }) return null;
        var result = new List<(long, string)>();
        foreach (var item in raw)
        {
            var idx = item.IndexOf(':');
            if (idx > 0 && long.TryParse(item[..idx], out var fid))
                result.Add((fid, item[(idx + 1)..]));
        }
        return result.Count > 0 ? result : null;
    }

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
            AllowQuickSearch = r.Definition.AllowQuickSearch,
        },
        IsDefault = r.IsDefault,
        DisplayOrder = r.DisplayOrder,
        CreatedOn = r.CreatedOn,
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
                },
                Group = MapFilterGroupDto(n.Group),
            }).ToList(),
        };
    }
}
