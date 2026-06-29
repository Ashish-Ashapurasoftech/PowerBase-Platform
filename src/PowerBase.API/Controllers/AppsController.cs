using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Common.Interfaces;
using PowerBase.API.Models;
using PowerBase.API.Models.Apps;
using PowerBase.Application.Apps.Commands.CreateApp;
using PowerBase.Application.Apps.Commands.DeleteApp;
using PowerBase.Application.Apps.Commands.UpdateApp;
using PowerBase.Application.Apps.Queries.GetApp;
using PowerBase.Application.Apps.Queries.ListApps;
using PowerBase.Application.Reports.Queries.GetRolesReportsMatrix;
using PowerBase.Application.Reports.Commands.UpdateReportVisibilityMatrix;
using PowerBase.Application.Common.Models;
using PowerBase.Domain.Constants;
using PowerBase.Domain.ValueObjects;
using System.Text.Json;
using PowerBase.Application.Apps.Queries.GetAppStorageUsage;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("apps")]
public class AppsController : ControllerBase
{
    private readonly CreateAppCommandHandler _createHandler;
    private readonly UpdateAppCommandHandler _updateHandler;
    private readonly DeleteAppCommandHandler _deleteHandler;
    private readonly GetAppQueryHandler _getHandler;
    private readonly ListAppsQueryHandler _listHandler;
    private readonly PowerBase.Application.Apps.Queries.GetAppPermissions.GetAppPermissionsQueryHandler _getPermissionsHandler;
    private readonly GetRolesReportsMatrixQueryHandler _getRolesReportsMatrixHandler;
    private readonly UpdateReportVisibilityMatrixCommandHandler _updateReportVisibilityMatrixHandler;
    private readonly GetAppStorageUsageQueryHandler _getStorageUsageHandler;
    private readonly IAppRepository _appRepo;
    private readonly IQueryContext _queryContext;

    public AppsController(
        CreateAppCommandHandler createHandler,
        UpdateAppCommandHandler updateHandler,
        DeleteAppCommandHandler deleteHandler,
        GetAppQueryHandler getHandler,
        ListAppsQueryHandler listHandler,
        PowerBase.Application.Apps.Queries.GetAppPermissions.GetAppPermissionsQueryHandler getPermissionsHandler,
        GetRolesReportsMatrixQueryHandler getRolesReportsMatrixHandler,
        UpdateReportVisibilityMatrixCommandHandler updateReportVisibilityMatrixHandler,
        GetAppStorageUsageQueryHandler getStorageUsageHandler,
        IAppRepository appRepo,
        IQueryContext queryContext)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _getHandler = getHandler;
        _listHandler = listHandler;
        _getPermissionsHandler = getPermissionsHandler;
        _getRolesReportsMatrixHandler = getRolesReportsMatrixHandler;
        _updateReportVisibilityMatrixHandler = updateReportVisibilityMatrixHandler;
        _getStorageUsageHandler = getStorageUsageHandler;
        _appRepo = appRepo;
        _queryContext = queryContext;
    }

    /// <summary>Create a new app for the current tenant.</summary>
    [HttpPost]
    [RequirePermission(PermissionCodes.AppsCreate)]
    [ProducesResponseType(typeof(ApiResponse<AppResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromBody] CreateAppRequest request, CancellationToken ct)
    {
        var command = new CreateAppCommand(
            request.Name,
            request.Description,
            request.Icon,
            request.Color,
            request.Tables.Select(t => new TableSpec(
                t.Name,
                t.SingularLabel,
                t.PluralLabel,
                t.Icon,
                t.Description,
                t.Config,
                t.Fields?.Select(f => new AppFieldSpec(f.Name, f.TypeCode)).ToList())).ToList());
        var result = await _createHandler.HandleAsync(command, ct);
        var response = MapToAppResponse(result);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<AppResponse>(response));
    }

    /// <summary>List all apps for the current tenant.</summary>
    [HttpGet]
    [RequirePermission(PermissionCodes.AppsRead)]
    [ProducesResponseType(typeof(ApiListResponse<AppResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List([FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var result = await _listHandler.HandleAsync(new ListAppsQuery(page, pageSize), ct);
        var items = result.Items.Select(MapToAppResponse).ToList();
        return Ok(new ApiListResponse<AppResponse>(items, result.Total, result.Page, result.PageSize));
    }

    /// <summary>Search apps by name for the current tenant.</summary>
    [HttpGet("search")]
    [RequirePermission(PermissionCodes.AppsRead)]
    [ProducesResponseType(typeof(ApiListResponse<AppResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Search([FromQuery] string name = "", [FromQuery] int page = 1, [FromQuery] int pageSize = 20, CancellationToken ct = default)
    {
        var allItems = new List<PowerBase.Domain.Entities.App>();
        int currentPage = 1;
        int batchSize = 100;
        
        while (true)
        {
            var batchResult = await _listHandler.HandleAsync(new ListAppsQuery(currentPage, batchSize), ct);
            if (batchResult.Items == null || batchResult.Items.Count == 0)
            {
                break;
            }
            allItems.AddRange(batchResult.Items);
            if (batchResult.Items.Count < batchSize || allItems.Count >= batchResult.Total)
            {
                break;
            }
            currentPage++;
        }

        var filtered = allItems
            .Where(a => string.IsNullOrEmpty(name) || a.Name.Contains(name.Trim(), StringComparison.OrdinalIgnoreCase))
            .ToList();

        var paginated = filtered
            .Skip((page - 1) * pageSize)
            .Take(pageSize)
            .Select(MapToAppResponse)
            .ToList();

        return Ok(new ApiListResponse<AppResponse>(paginated, filtered.Count, page, pageSize));
    }

    /// <summary>Export all apps for the current tenant to CSV.</summary>
    [HttpGet("export")]
    [RequirePermission(PermissionCodes.AppsRead)]
    [ProducesResponseType(typeof(FileResult), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> Export(CancellationToken ct = default)
    {
        var allItems = await _appRepo.ListAllByUserAsync(_queryContext.UserId, ct);

        var csvBuilder = new System.Text.StringBuilder();
        csvBuilder.AppendLine("PublicId,Name,Description,Color,Icon,Status,CreatedOn");

        foreach (var app in allItems)
        {
            csvBuilder.AppendLine(string.Join(",",
                EscapeCsvField(app.PublicId.ToString()),
                EscapeCsvField(app.Name),
                EscapeCsvField(app.Description),
                EscapeCsvField(app.Color),
                EscapeCsvField(app.Icon),
                EscapeCsvField(app.Status),
                EscapeCsvField(app.CreatedOn.ToString("o"))
            ));
        }

        var bytes = System.Text.Encoding.UTF8.GetBytes(csvBuilder.ToString());
        return File(bytes, "text/csv", "apps.csv");
    }

    private static string EscapeCsvField(string? field)
    {
        if (string.IsNullOrEmpty(field))
        {
            return string.Empty;
        }

        bool mustQuote = field.Contains(',') || field.Contains('"') || field.Contains('\n') || field.Contains('\r');
        if (mustQuote)
        {
            return $"\"{field.Replace("\"", "\"\"")}\"";
        }

        return field;
    }


    /// <summary>Get a single app by its public ID.</summary>
    [HttpGet("{publicId:guid}")]
    [RequirePermission(PermissionCodes.AppsRead)]
    [ProducesResponseType(typeof(ApiResponse<AppResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid publicId, CancellationToken ct)
    {
        var app = await _getHandler.HandleAsync(new GetAppQuery(publicId), ct);
        return Ok(new ApiResponse<AppResponse>(MapToAppResponse(app)));
    }

    /// <summary>Get dynamic storage usage for the database and files directory of this app.</summary>
    [HttpGet("{publicId:guid}/storage-usage")]
    [RequirePermission(PermissionCodes.AppsRead)]
    [ProducesResponseType(typeof(ApiResponse<AppStorageUsageResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetStorageUsage(Guid publicId, CancellationToken ct)
    {
        var result = await _getStorageUsageHandler.HandleAsync(new GetAppStorageUsageQuery(publicId), ct);
        return Ok(new ApiResponse<AppStorageUsageResult>(result));
    }

    /// <summary>Get current user's active permissions for this app.</summary>
    [HttpGet("{publicId:guid}/permissions")]
    [RequirePermission(PermissionCodes.AppsRead)]
    [ProducesResponseType(typeof(ApiResponse<PowerBase.Application.Apps.Queries.GetAppPermissions.AppPermissionsResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetPermissions(Guid publicId, CancellationToken ct)
    {
        var result = await _getPermissionsHandler.HandleAsync(new PowerBase.Application.Apps.Queries.GetAppPermissions.GetAppPermissionsQuery(publicId), ct);
        return Ok(new ApiResponse<PowerBase.Application.Apps.Queries.GetAppPermissions.AppPermissionsResult>(result));
    }

    /// <summary>Update an app's name, description, icon, or color.</summary>
    [HttpPatch("{publicId:guid}")]
    [RequirePermission(PermissionCodes.AppsUpdate)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid publicId, [FromBody] UpdateAppRequest request, CancellationToken ct)
    {
        await _updateHandler.HandleAsync(new UpdateAppCommand(publicId, request.Name, request.Description, request.Icon, request.Color, request.Formatting, request.SecurityOptions), ct);
        return NoContent();
    }

    /// <summary>Soft-delete an app by its public ID.</summary>
    [HttpDelete("{publicId:guid}")]
    [RequirePermission(PermissionCodes.AppsDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid publicId, CancellationToken ct)
    {
        await _deleteHandler.HandleAsync(new DeleteAppCommand(publicId), ct);
        return NoContent();
    }

    /// <summary>Get the roles and reports visibility matrix for this app.</summary>
    [HttpGet("{publicId:guid}/roles-reports-matrix")]
    [RequireAppPermission(PermissionCodes.RolesManage, AppAccessResolver.ByAppPublicId)]
    [ProducesResponseType(typeof(ApiResponse<RolesReportsMatrixResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> GetRolesReportsMatrix(Guid publicId, CancellationToken ct)
    {
        var result = await _getRolesReportsMatrixHandler.HandleAsync(new GetRolesReportsMatrixQuery(publicId), ct);
        return Ok(new ApiResponse<RolesReportsMatrixResult>(result));
    }

    /// <summary>Update the roles and reports visibility matrix for this app.</summary>
    [HttpPut("{publicId:guid}/roles-reports-matrix")]
    [RequireAppPermission(PermissionCodes.RolesManage, AppAccessResolver.ByAppPublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> UpdateRolesReportsMatrix(Guid publicId, [FromBody] UpdateReportVisibilityMatrixRequest request, CancellationToken ct)
    {
        var updates = request.Updates.Select(u => new ReportVisibilityUpdate(u.ReportPublicId, u.Visibility, u.VisibleToRoleIds)).ToList();
        await _updateReportVisibilityMatrixHandler.HandleAsync(new UpdateReportVisibilityMatrixCommand(publicId, updates), ct);
        return NoContent();
    }

    private static AppResponse MapToAppResponse(CreateAppResult result) => new()
    {
        PublicId = result.PublicId,
        Name = result.Name,
        Description = result.Description,
        Icon = result.Icon,
        Color = result.Color,
        Status = result.Status,
        CreatedOn = result.CreatedOn,
    };

    private static AppResponse MapToAppResponse(PowerBase.Domain.Entities.App app)
    {
        var formatting = string.IsNullOrEmpty(app.Formatting)
            ? new AppFormattingSettings()
            : JsonSerializer.Deserialize<AppFormattingSettings>(app.Formatting, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppFormattingSettings();

        var security = string.IsNullOrEmpty(app.SecurityOptions)
            ? new AppSecurityOptionsSettings()
            : JsonSerializer.Deserialize<AppSecurityOptionsSettings>(app.SecurityOptions, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSecurityOptionsSettings();

        return new AppResponse
        {
            PublicId = app.PublicId,
            Name = app.Name,
            Description = app.Description,
            Icon = app.Icon,
            Color = app.Color,
            Formatting = formatting,
            SecurityOptions = security,
            Status = app.Status,
            CreatedOn = app.CreatedOn,
        };
    }

    private static AppResponse MapToAppResponse(AppListItemDto app)
    {
        var formatting = string.IsNullOrEmpty(app.Formatting)
            ? new AppFormattingSettings()
            : JsonSerializer.Deserialize<AppFormattingSettings>(app.Formatting, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppFormattingSettings();

        var security = string.IsNullOrEmpty(app.SecurityOptions)
            ? new AppSecurityOptionsSettings()
            : JsonSerializer.Deserialize<AppSecurityOptionsSettings>(app.SecurityOptions, new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? new AppSecurityOptionsSettings();

        return new AppResponse
        {
            PublicId = app.PublicId,
            Name = app.Name,
            Description = app.Description,
            Icon = app.Icon,
            Color = app.Color,
            Formatting = formatting,
            SecurityOptions = security,
            Status = app.Status,
            CreatedOn = app.CreatedOn,
            OwnerName = app.OwnerName,
        };
    }
}
