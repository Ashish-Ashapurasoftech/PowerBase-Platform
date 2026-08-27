using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Pipelines;
using PowerBase.Application.Pipelines.Commands.CreatePipeline;
using PowerBase.Application.Pipelines.Commands.CopyPipeline;
using PowerBase.Application.Pipelines.Commands.DeletePipeline;
using PowerBase.Application.Pipelines.Commands.DeletePipelines;
using PowerBase.Application.Pipelines.Commands.SavePipelineSteps;
using PowerBase.Application.Pipelines.Commands.UpdatePipeline;
using PowerBase.Application.Pipelines.Queries.GetPipeline;
using PowerBase.Application.Pipelines.Queries.GetPipelineEditor;
using PowerBase.Application.Pipelines.Queries.ListPipelines;
using PowerBase.Application.Pipelines.Queries.GetPipelineSchedule;
using PowerBase.Application.Pipelines.Commands.UpdatePipelineSchedule;
using PowerBase.Application.Pipelines.Commands.DeletePipelineSchedule;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Constants;
using System;
using System.Text.Json;
using PowerBase.Application.Pipelines.Queries.ListPipelineRuns;
using PowerBase.Application.Pipelines.Queries.GetPipelineRunSteps;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("")]
public class PipelinesController : ControllerBase
{
    private readonly CreatePipelineCommandHandler _createHandler;
    private readonly CopyPipelineCommandHandler _copyHandler;
    private readonly UpdatePipelineCommandHandler _updateHandler;
    private readonly SavePipelineStepsCommandHandler _saveStepsHandler;
    private readonly DeletePipelineCommandHandler _deleteHandler;
    private readonly GetPipelineQueryHandler _getHandler;
    private readonly ListPipelinesQueryHandler _listHandler;

    public PipelinesController(
        CreatePipelineCommandHandler createHandler,
        CopyPipelineCommandHandler copyHandler,
        UpdatePipelineCommandHandler updateHandler,
        SavePipelineStepsCommandHandler saveStepsHandler,
        DeletePipelineCommandHandler deleteHandler,
        GetPipelineQueryHandler getHandler,
        ListPipelinesQueryHandler listHandler)
    {
        _createHandler = createHandler;
        _copyHandler = copyHandler;
        _updateHandler = updateHandler;
        _saveStepsHandler = saveStepsHandler;
        _deleteHandler = deleteHandler;
        _getHandler = getHandler;
        _listHandler = listHandler;
    }

    /// <summary>Create a new pipeline workflow for an app.</summary>
    [HttpPost("apps/{appId:guid}/pipelines")]
    [RequireAppPermission(PermissionCodes.PowerFlowsCreate, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiResponse<PipelineResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> Create(Guid appId, [FromBody] CreatePipelineRequest request, CancellationToken ct)
    {
        var command = new CreatePipelineCommand(appId, request.Name, request.Description);
        var result = await _createHandler.HandleAsync(command, ct);
        var response = MapToResponse(result);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<PipelineResponse>(response));
    }

    /// <summary>List pipelines in an app with pagination, search, sorting, and status filtering.</summary>
    [HttpGet("apps/{appId:guid}/pipelines")]
    [RequireAppPermission(PermissionCodes.PowerFlowsRead, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiListResponse<PipelineListItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> List(
        Guid appId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        [FromQuery] string? search = null,
        [FromQuery] string sortBy = "createdOn",
        [FromQuery] bool sortDesc = true,
        [FromQuery] bool? isActive = null,
        CancellationToken ct = default)
    {
        var query = new ListPipelinesQuery(
            appId,
            page,
            pageSize,
            search,
            sortBy,
            sortDesc,
            isActive);

        var result = await _listHandler.HandleAsync(query, ct);
        var items = result.Items.Select(MapToListItemResponse).ToList();
        return Ok(new ApiListResponse<PipelineListItemResponse>(items, result.TotalCount, result.Page, result.PageSize));
    }

    /// <summary>Get a single pipeline and its fully reconstructed step hierarchy tree.</summary>
    [HttpGet("pipelines/{publicId:guid}")]
    [RequireAppPermission(PermissionCodes.PowerFlowsRead, AppAccessResolver.ByPipelinePublicId)]
    [ProducesResponseType(typeof(ApiResponse<PipelineDetailResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid publicId, CancellationToken ct)
    {
        var query = new GetPipelineQuery(publicId);
        var result = await _getHandler.HandleAsync(query, ct);
        var response = MapToDetailResponse(result);
        return Ok(new ApiResponse<PipelineDetailResponse>(response));
    }

    /// <summary>
    /// Get a pipeline together with its complete editor metadata (resolved table/field
    /// metadata for all step references the backend can resolve authoritatively).
    /// Use this endpoint — not GET /pipelines/{id} — when opening the pipeline editor.
    /// The frontend must await all ClientResolveRefs before rendering the editor.
    /// </summary>
    [HttpGet("pipelines/{publicId:guid}/editor")]
    [RequireAppPermission(PermissionCodes.PowerFlowsRead, AppAccessResolver.ByPipelinePublicId)]
    [ProducesResponseType(typeof(ApiResponse<PipelineEditorResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetEditor(
        Guid publicId,
        [FromServices] GetPipelineEditorQueryHandler editorHandler,
        CancellationToken ct)
    {
        var query = new GetPipelineEditorQuery(publicId);
        var result = await editorHandler.HandleAsync(query, ct);
        var response = MapToEditorResponse(result);
        return Ok(new ApiResponse<PipelineEditorResponse>(response));
    }

    /// <summary>Update a pipeline's basic metadata (name, description, isActive).</summary>
    [HttpPut("pipelines/{publicId:guid}")]
    [RequireAppPermission(PermissionCodes.PowerFlowsUpdate, AppAccessResolver.ByPipelinePublicId)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(Guid publicId, [FromBody] UpdatePipelineRequest request, CancellationToken ct)
    {
        var rowVersion = Convert.FromBase64String(request.RowVersion);
        var command = new UpdatePipelineCommand(publicId, request.Name, request.Description, request.IsActive, rowVersion);
        await _updateHandler.HandleAsync(command, ct);
        var pipeline = await _getHandler.HandleAsync(new GetPipelineQuery(publicId), ct);
        return Ok(new { rowVersion = Convert.ToBase64String(pipeline.RowVersion) });
    }

    /// <summary>Save or overwrite the hierarchical steps layout for a pipeline.</summary>
    [HttpPut("pipelines/{publicId:guid}/steps")]
    [RequireAppPermission(PermissionCodes.PowerFlowsUpdate, AppAccessResolver.ByPipelinePublicId)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> SaveSteps(Guid publicId, [FromBody] SavePipelineStepsRequest request, CancellationToken ct)
    {
        var rowVersion = Convert.FromBase64String(request.RowVersion);
        var command = new SavePipelineStepsCommand(publicId, request.Steps, rowVersion);
        await _saveStepsHandler.HandleAsync(command, ct);
        var pipeline = await _getHandler.HandleAsync(new GetPipelineQuery(publicId), ct);
        return Ok(new { rowVersion = Convert.ToBase64String(pipeline.RowVersion), isActive = pipeline.IsActive });
    }

    /// <summary>Soft-delete a pipeline workflow.</summary>
    [HttpDelete("pipelines/{publicId:guid}")]
    [RequireAppPermission(PermissionCodes.PowerFlowsDelete, AppAccessResolver.ByPipelinePublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid publicId, CancellationToken ct)
    {
        var command = new DeletePipelineCommand(publicId);
        await _deleteHandler.HandleAsync(command, ct);
        return NoContent();
    }

    /// <summary>Bulk soft-delete multiple pipeline workflows.</summary>
    [HttpPost("apps/{appId:guid}/pipelines/bulk-delete")]
    [RequireAppPermission(PermissionCodes.PowerFlowsDelete, AppAccessResolver.ByAppId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BulkDelete(
        Guid appId,
        [FromBody] BulkDeletePipelinesRequest request,
        [FromServices] DeletePipelinesCommandHandler bulkDeleteHandler,
        CancellationToken ct)
    {
        var command = new DeletePipelinesCommand(appId, request.PublicIds);
        await bulkDeleteHandler.HandleAsync(command, ct);
        return NoContent();
    }

    /// <summary>Copy/clone an existing pipeline.</summary>
    [HttpPost("pipelines/{publicId:guid}/copy")]
    [RequireAppPermission(PermissionCodes.PowerFlowsCopy, AppAccessResolver.ByPipelinePublicId)]
    [ProducesResponseType(typeof(ApiResponse<PipelineResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Copy(Guid publicId, CancellationToken ct)
    {
        var command = new CopyPipelineCommand(publicId);
        var result = await _copyHandler.HandleAsync(command, ct);
        var response = MapToResponse(result);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<PipelineResponse>(response));
    }

    /// <summary>Get list of all timezones in canonical IANA format.</summary>
    [HttpGet("api/v1/pipelines/timezones")]
    [ProducesResponseType(typeof(List<PowerBase.Application.Pipelines.Queries.GetTimeZones.TimeZoneDto>), StatusCodes.Status200OK)]
    public IActionResult GetTimeZones()
    {
        var timeZones = TimeZoneInfo.GetSystemTimeZones();
        var resultList = new List<PowerBase.Application.Pipelines.Queries.GetTimeZones.TimeZoneDto>();
        
        foreach (var tz in timeZones)
        {
            string ianaId = TimeZoneInfo.TryConvertWindowsIdToIanaId(tz.Id, out var canonicalId) ? canonicalId : tz.Id;
            if (string.Equals(ianaId, "Etc/UTC", StringComparison.OrdinalIgnoreCase))
            {
                ianaId = "UTC";
            }
            else if (string.Equals(ianaId, "Asia/Calcutta", StringComparison.OrdinalIgnoreCase))
            {
                ianaId = "Asia/Kolkata";
            }

            var offset = tz.GetUtcOffset(DateTime.UtcNow);
            var sign = offset.Ticks >= 0 ? "+" : "-";
            var offsetStr = $"UTC{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";
            var displayName = $"({offsetStr}) {ianaId} ({tz.StandardName})";

            resultList.Add(new PowerBase.Application.Pipelines.Queries.GetTimeZones.TimeZoneDto
            {
                Id = ianaId,
                DisplayName = displayName
            });
        }

        // Ensure key representative zones are always present, even on Windows hosts
        var representativeZones = new[]
        {
            "UTC",
            "Asia/Kolkata",
            "Asia/Tokyo",
            "Asia/Dubai",
            "Asia/Singapore",
            "Europe/London",
            "Europe/Paris",
            "Europe/Berlin",
            "America/New_York",
            "America/Chicago",
            "America/Denver",
            "America/Los_Angeles",
            "America/Toronto",
            "America/Sao_Paulo",
            "Australia/Sydney",
            "Pacific/Auckland"
        };

        foreach (var repId in representativeZones)
        {
            if (!resultList.Any(t => string.Equals(t.Id, repId, StringComparison.OrdinalIgnoreCase)))
            {
                try
                {
                    var tz = PowerBase.Infrastructure.Pipelines.TimeZoneMapper.ResolveTimeZone(repId);
                    var offset = tz.GetUtcOffset(DateTime.UtcNow);
                    var sign = offset.Ticks >= 0 ? "+" : "-";
                    var offsetStr = $"UTC{sign}{Math.Abs(offset.Hours):00}:{Math.Abs(offset.Minutes):00}";
                    var displayName = $"({offsetStr}) {repId} ({tz.StandardName})";

                    resultList.Add(new PowerBase.Application.Pipelines.Queries.GetTimeZones.TimeZoneDto
                    {
                        Id = repId,
                        DisplayName = displayName
                    });
                }
                catch
                {
                    // Fallback or ignore if unresolved
                }
            }
        }

        var sortedList = resultList
            .GroupBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .OrderBy(t => {
                try
                {
                    var tz = PowerBase.Infrastructure.Pipelines.TimeZoneMapper.ResolveTimeZone(t.Id);
                    return tz.GetUtcOffset(DateTime.UtcNow).TotalMinutes;
                }
                catch
                {
                    return 0.0;
                }
            })
            .ThenBy(t => t.Id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!sortedList.Any(t => string.Equals(t.Id, "UTC", StringComparison.OrdinalIgnoreCase)))
        {
            sortedList.Insert(0, new PowerBase.Application.Pipelines.Queries.GetTimeZones.TimeZoneDto
            {
                Id = "UTC",
                DisplayName = "(UTC+00:00) UTC (Coordinated Universal Time)"
            });
        }

        return Ok(sortedList);
    }

    // --- Mappings ---

    private static PipelineResponse MapToResponse(CreatePipelineResult result) => new()
    {
        PublicId = result.PublicId,
        Name = result.Name,
        Description = result.Description,
        IsActive = result.IsActive,
        CreatedOn = result.CreatedOn
    };

    private static PipelineListItemResponse MapToListItemResponse(PipelineListItem item) => new()
    {
        PublicId = item.PublicId,
        Name = item.Name,
        Description = item.Description,
        IsActive = item.IsActive,
        CreatedOn = item.CreatedOn,
        FirstStepType = item.FirstStepType,
        FirstStepSubtype = item.FirstStepSubtype
    };

    private static PipelineDetailResponse MapToDetailResponse(PipelineResult result) => new()
    {
        PublicId = result.PublicId,
        AppPublicId = result.AppPublicId,
        Name = result.Name,
        Description = result.Description,
        VariablesJson = result.VariablesJson,
        IsActive = result.IsActive,
        RowVersion = Convert.ToBase64String(result.RowVersion),
        Steps = result.Steps.Select(MapStepResponse).ToList()
    };

    private static PipelineStepResponse MapStepResponse(PipelineStepResult step) => new()
    {
        PublicId = step.PublicId,
        RefId = step.RefId,
        DisplayOrder = step.DisplayOrder,
        Type = step.Type,
        Subtype = step.Subtype,
        ConfigJson = step.ConfigJson,
        ParentBranch = step.ParentBranch,
        RowVersion = Convert.ToBase64String(step.RowVersion),
        Children = step.Children.Select(MapStepResponse).ToList(),
        ElseChildren = step.ElseChildren.Select(MapStepResponse).ToList(),
        SuccessChildren = step.SuccessChildren.Select(MapStepResponse).ToList(),
        ErrorChildren = step.ErrorChildren.Select(MapStepResponse).ToList()
    };

    private static PipelineEditorResponse MapToEditorResponse(PipelineEditorResult result) => new()
    {
        PublicId = result.PublicId,
        AppPublicId = result.AppPublicId,
        Name = result.Name,
        Description = result.Description,
        VariablesJson = result.VariablesJson,
        IsActive = result.IsActive,
        RowVersion = Convert.ToBase64String(result.RowVersion),
        Steps = result.Steps.Select(MapEditorStepResponse).ToList(),
        EditorTables = result.EditorTables.Select(t => new PipelineEditorTableDto
        {
            ConnectionPublicId = t.ConnectionPublicId,
            AppPublicId = t.AppPublicId,
            TablePublicId = t.TablePublicId,
            TableName = t.TableName,
            Fields = t.Fields.Select(f => new PipelineEditorFieldDto
            {
                PublicId = f.PublicId,
                Name = f.Name,
                Label = f.Label,
                TypeCode = f.TypeCode,
                Fid = f.Fid,
                Settings = f.Settings,
                DefaultValue = f.DefaultValue,
                IsRequired = f.IsRequired,
                IsSystem = f.IsSystem
            }).ToList()
        }).ToList(),
        ClientResolveRefs = result.ClientResolveRefs.Select(r => new PipelineEditorClientRefDto
        {
            ConnectionPublicId = r.ConnectionPublicId,
            AppPublicId = r.AppPublicId,
            TablePublicId = r.TablePublicId,
            Reason = r.Reason switch
            {
                PipelineEditorRefReason.SavedConnection => "saved_connection",
                PipelineEditorRefReason.SystemConnection => "system_connection",
                PipelineEditorRefReason.TableNotFound => "table_not_found",
                PipelineEditorRefReason.AppNotFound => "app_not_found",
                PipelineEditorRefReason.AccessDenied => "access_denied",
                PipelineEditorRefReason.TenantNotFound => "tenant_not_found",
                PipelineEditorRefReason.ConnectionUnavailable => "connection_unavailable",
                _ => "resolution_error"
            }
        }).ToList()
    };

    private static PipelineStepResponse MapEditorStepResponse(PipelineEditorStepResult step) => new()
    {
        PublicId = step.PublicId,
        RefId = step.RefId,
        DisplayOrder = step.DisplayOrder,
        Type = step.Type,
        Subtype = step.Subtype,
        ConfigJson = step.ConfigJson,
        ParentBranch = step.ParentBranch,
        RowVersion = Convert.ToBase64String(step.RowVersion),
        Children = step.Children.Select(MapEditorStepResponse).ToList(),
        ElseChildren = step.ElseChildren.Select(MapEditorStepResponse).ToList(),
        SuccessChildren = step.SuccessChildren.Select(MapEditorStepResponse).ToList(),
        ErrorChildren = step.ErrorChildren.Select(MapEditorStepResponse).ToList()
    };

    /// <summary>Get a pipeline's schedule details.</summary>
    [HttpGet("pipelines/{publicId:guid}/schedule")]
    [RequireAppPermission(PermissionCodes.PowerFlowsRead, AppAccessResolver.ByPipelinePublicId)]
    [ProducesResponseType(typeof(ApiResponse<PipelineScheduleResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetSchedule(
        Guid publicId,
        [FromServices] GetPipelineScheduleQueryHandler handler,
        CancellationToken ct)
    {
        var result = await handler.HandleAsync(new GetPipelineScheduleQuery(publicId), ct);
        if (result == null)
        {
            result = new PipelineScheduleResult
            {
                PipelinePublicId = publicId,
                ScheduleType = "hourly",
                Interval = 1,
                TimeZone = "UTC"
            };
        }
        return Ok(new ApiResponse<PipelineScheduleResult>(result));
    }

    /// <summary>Create or update a pipeline's schedule.</summary>
    [HttpPut("pipelines/{publicId:guid}/schedule")]
    [RequireAppPermission(PermissionCodes.PowerFlowsUpdate, AppAccessResolver.ByPipelinePublicId)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> UpdateSchedule(
        Guid publicId,
        [FromBody] UpdatePipelineScheduleRequest request,
        [FromServices] UpdatePipelineScheduleCommandHandler handler,
        CancellationToken ct)
    {
        var command = new UpdatePipelineScheduleCommand(
            publicId,
            request.ScheduleType,
            request.Interval,
            request.TimeOfDay,
            request.Weekdays,
            request.MonthDay,
            request.MonthOfYear,
            request.RelativeWeek,
            request.RelativeDay,
            request.TimeZone,
            request.CronExpression);

        await handler.HandleAsync(command, ct);
        var pipeline = await _getHandler.HandleAsync(new GetPipelineQuery(publicId), ct);
        return Ok(new { message = "Schedule updated successfully.", rowVersion = Convert.ToBase64String(pipeline.RowVersion), isActive = pipeline.IsActive });
    }

    /// <summary>Delete a pipeline's schedule.</summary>
    [HttpDelete("pipelines/{publicId:guid}/schedule")]
    [RequireAppPermission(PermissionCodes.PowerFlowsUpdate, AppAccessResolver.ByPipelinePublicId)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> DeleteSchedule(
        Guid publicId,
        [FromServices] DeletePipelineScheduleCommandHandler handler,
        CancellationToken ct)
    {
        await handler.HandleAsync(new DeletePipelineScheduleCommand(publicId), ct);
        var pipeline = await _getHandler.HandleAsync(new GetPipelineQuery(publicId), ct);
        return Ok(new { message = "Schedule deleted successfully.", rowVersion = Convert.ToBase64String(pipeline.RowVersion), isActive = pipeline.IsActive });
    }

    /// <summary>Run a pipeline manually on demand.</summary>
    [HttpPost("pipelines/{publicId:guid}/run")]
    [RequireAppPermission(PermissionCodes.PowerFlowsUpdate, AppAccessResolver.ByPipelinePublicId)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public async Task<IActionResult> RunNow(
        Guid publicId,
        [FromServices] IPipelineRepository pipelineRepo,
        [FromServices] IPipelineExecutionQueue queue,
        [FromServices] IQueryContext queryContext,
        CancellationToken ct)
    {
        var pipelineId = await pipelineRepo.GetIdByPublicIdAsync(publicId, ct);
        var pipeline = await pipelineRepo.GetByPublicIdAsync(publicId, ct);

        var clientToken = Request.Headers["X-PowerBase-Client-Token"].FirstOrDefault();
        Guid messageId;
        if (!string.IsNullOrEmpty(clientToken))
        {
            var hashInput = publicId.ToString() + "_" + clientToken;
            var hashBytes = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(hashInput));
            var guidBytes = new byte[16];
            Array.Copy(hashBytes, guidBytes, 16);
            messageId = new Guid(guidBytes);
        }
        else
        {
            messageId = Guid.NewGuid();
        }

        var correlationId = Guid.NewGuid().ToString();

        var task = new PipelineExecutionTask
        {
            TenantId = queryContext.TenantId,
            PipelineId = pipelineId,
            TriggerEvent = "manual",
            TriggerPayloadJson = JsonSerializer.Serialize(new
            {
                TriggerTime = DateTime.UtcNow,
                ScheduledTime = DateTime.UtcNow,
                Source = "manual"
            }),
            TriggeredBy = queryContext.UserId,
            VariablesJson = null,
            CorrelationId = correlationId,
            Depth = 1,
            MessageId = messageId.ToString()
        };

        queue.QueueTask(task);
        return Ok(new { message = "Pipeline run requested and enqueued.", messageId = messageId.ToString(), correlationId });
    }

    /// <summary>List pipeline execution runs with pagination.</summary>
    [HttpGet("pipelines/{publicId:guid}/runs")]
    [RequireAppPermission(PermissionCodes.PowerFlowsRead, AppAccessResolver.ByPipelinePublicId)]
    [ProducesResponseType(typeof(ApiListResponse<PipelineRunDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListRuns(
        Guid publicId,
        [FromServices] ListPipelineRunsQueryHandler handler,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 10,
        CancellationToken ct = default)
    {
        var query = new ListPipelineRunsQuery(publicId, page, pageSize);
        var result = await handler.HandleAsync(query, ct);
        return Ok(new ApiListResponse<PipelineRunDto>(result.Items, result.TotalCount, result.Page, result.PageSize));
    }

    [HttpGet("pipelines/runs/{runPublicId:guid}/steps")]
    [ProducesResponseType(typeof(ApiListResponse<PipelineStepRunDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetRunSteps(
        Guid runPublicId,
        [FromServices] GetPipelineRunStepsQueryHandler handler,
        [FromServices] IAppAccessService appAccessService,
        [FromServices] IPipelineRepository pipelineRepo,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 50,
        CancellationToken ct = default)
    {
        var run = await pipelineRepo.GetRunByPublicIdAsync(runPublicId, ct);
        if (run == null)
            return NotFound(new { error = new { code = "NOT_FOUND", message = $"PipelineRun {runPublicId} not found." } });

        var pipeline = await pipelineRepo.GetByIdAsync(run.PipelineId, ct);
        if (pipeline == null)
            return NotFound(new { error = new { code = "NOT_FOUND", message = "Parent Pipeline not found." } });

        try
        {
            await appAccessService.RequirePermissionByPipelinePublicIdAsync(pipeline.PublicId, PermissionCodes.PowerFlowsRead, ct);
        }
        catch (PowerBase.Domain.Exceptions.UnauthorizedActionException ex)
        {
            return StatusCode(StatusCodes.Status403Forbidden, new { error = new { code = "FORBIDDEN", message = ex.Message } });
        }

        var query = new GetPipelineRunStepsQuery(runPublicId, page, pageSize);
        var result = await handler.HandleAsync(query, ct);
        return Ok(new ApiListResponse<PipelineStepRunDto>(result.Items, result.TotalCount, result.Page, result.PageSize));
    }

    /// <summary>Get list of all tenants accessible to the logged-in user for pipeline connection setup.</summary>
    [HttpGet("pipelines/available-tenants")]
    [RequireAuth]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<PowerBase.API.Models.Auth.TenantListItem>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> GetAvailableTenants(
        [FromServices] ITenantRepository tenantRepo,
        [FromServices] IQueryContext queryContext,
        CancellationToken ct)
    {
        var tenants = await tenantRepo.ListTenantsForUserAsync(queryContext.UserId, ct);
        var response = tenants.Select(t => new PowerBase.API.Models.Auth.TenantListItem
        {
            PublicId = t.PublicId,
            Name = t.Name,
            Slug = t.Slug,
            IsOwner = t.IsOwner
        }).ToList();
        return Ok(new ApiResponse<IReadOnlyList<PowerBase.API.Models.Auth.TenantListItem>>(response));
    }
}

