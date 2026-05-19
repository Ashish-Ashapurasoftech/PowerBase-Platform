using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Common.Interfaces;
using PowerBase.API.Models;
using PowerBase.API.Models.Records;
using PowerBase.API.Models.Reports;
using PowerBase.Application.Reports;
using PowerBase.Application.Reports.Commands.CreateReport;
using PowerBase.Application.Reports.Commands.DeleteReport;
using PowerBase.Application.Reports.Commands.UpdateReport;
using PowerBase.Application.Reports.Queries.GetReport;
using PowerBase.Application.Reports.Queries.ListReports;
using PowerBase.Application.Reports.Queries.RunReport;
using PowerBase.Domain.Constants;

namespace PowerBase.API.Controllers;

[ApiController]
public class ReportsController : ControllerBase
{
    private readonly CreateReportCommandHandler _createHandler;
    private readonly UpdateReportCommandHandler _updateHandler;
    private readonly DeleteReportCommandHandler _deleteHandler;
    private readonly GetReportQueryHandler _getHandler;
    private readonly ListReportsQueryHandler _listHandler;
    private readonly RunReportQueryHandler _runHandler;

    public ReportsController(
        CreateReportCommandHandler createHandler,
        UpdateReportCommandHandler updateHandler,
        DeleteReportCommandHandler deleteHandler,
        GetReportQueryHandler getHandler,
        ListReportsQueryHandler listHandler,
        RunReportQueryHandler runHandler)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _getHandler = getHandler;
        _listHandler = listHandler;
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
            request.Columns,
            request.SortFieldId,
            request.SortDesc);
        var result = await _createHandler.HandleAsync(command, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<ReportResponse>(MapToResponse(result)));
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

    /// <summary>Update a report's name, visibility, and column definition.</summary>
    [HttpPatch("reports/{publicId:guid}")]
    [RequirePermission(PermissionCodes.ReportsUpdate)]
    [RequireAppAccess(AppAccess.Admin, AppAccessResolver.ByReportPublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid publicId, [FromBody] UpdateReportRequest request, CancellationToken ct)
    {
        var command = new UpdateReportCommand(publicId, request.Name, request.Description, request.Visibility, request.Columns, request.SortFieldId, request.SortDesc);
        await _updateHandler.HandleAsync(command, ct);
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

    /// <summary>Execute a report and return paged results.</summary>
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
        CancellationToken ct = default)
    {
        var result = await _runHandler.HandleAsync(new RunReportQuery(publicId, page, pageSize), ct);
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
            SortFieldId = r.Definition.SortFieldId,
            SortDesc = r.Definition.SortDesc,
        },
        IsDefault = r.IsDefault,
        DisplayOrder = r.DisplayOrder,
        CreatedOn = r.CreatedOn,
    };
}
