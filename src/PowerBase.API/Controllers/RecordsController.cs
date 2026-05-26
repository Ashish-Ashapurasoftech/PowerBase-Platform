using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Common.Interfaces;
using PowerBase.API.Models;
using PowerBase.API.Models.Records;
using PowerBase.Application.Records;
using PowerBase.Application.Records.Commands.BulkDeleteRecords;
using PowerBase.Application.Records.Commands.CreateRecord;
using PowerBase.Application.Records.Commands.DeleteRecord;
using PowerBase.Application.Records.Commands.UpdateRecord;
using PowerBase.Application.Records.Queries.GetRecord;
using PowerBase.Application.Records.Queries.ListRecords;
using PowerBase.Domain.Constants;

namespace PowerBase.API.Controllers;

[ApiController]
public class RecordsController : ControllerBase
{
    private readonly CreateRecordCommandHandler _createHandler;
    private readonly UpdateRecordCommandHandler _updateHandler;
    private readonly DeleteRecordCommandHandler _deleteHandler;
    private readonly BulkDeleteRecordsCommandHandler _bulkDeleteHandler;
    private readonly ListRecordsQueryHandler _listHandler;
    private readonly GetRecordQueryHandler _getHandler;
    private readonly IAppAccessService _appAccessService;

    public RecordsController(
        CreateRecordCommandHandler createHandler,
        UpdateRecordCommandHandler updateHandler,
        DeleteRecordCommandHandler deleteHandler,
        BulkDeleteRecordsCommandHandler bulkDeleteHandler,
        ListRecordsQueryHandler listHandler,
        GetRecordQueryHandler getHandler,
        IAppAccessService appAccessService)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _bulkDeleteHandler = bulkDeleteHandler;
        _listHandler = listHandler;
        _getHandler = getHandler;
        _appAccessService = appAccessService;
    }

    /// <summary>Insert a new record into a table.</summary>
    [HttpPost("tables/{tableId:guid}/records")]
    [RequirePermission(PermissionCodes.RecordsCreate)]
    [RequireAppAccess(AppAccess.Read, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<RecordResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Create(Guid tableId, [FromBody] CreateRecordRequest request, CancellationToken ct)
    {
        var flags = await _appAccessService.GetPermissionFlagsByTablePublicIdAsync(tableId, ct);
        if (!flags.CanAdd)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = new { code = "FORBIDDEN", message = "Your role does not allow adding records." } });

        var command = new CreateRecordCommand(tableId, ParseFieldValues(request.Fields));
        var result = await _createHandler.HandleAsync(command, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<RecordResponse>(MapToResponse(result)));
    }

    /// <summary>List records for a table (paged).</summary>
    [HttpGet("tables/{tableId:guid}/records")]
    [RequirePermission(PermissionCodes.RecordsRead)]
    [RequireAppAccess(AppAccess.Read, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiListResponse<RecordResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(
        Guid tableId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        CancellationToken ct = default)
    {
        var result = await _listHandler.HandleAsync(new ListRecordsQuery(tableId, page, pageSize), ct);
        var items = result.Items.Select(MapToResponse).ToList();
        return Ok(new ApiListResponse<RecordResponse>(items, result.TotalCount, result.Page, result.PageSize));
    }

    /// <summary>Get a single record by its public ID.</summary>
    [HttpGet("tables/{tableId:guid}/records/{id:guid}")]
    [RequirePermission(PermissionCodes.RecordsRead)]
    [RequireAppAccess(AppAccess.Read, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<RecordResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid tableId, Guid id, CancellationToken ct)
    {
        var result = await _getHandler.HandleAsync(new GetRecordQuery(tableId, id), ct);
        return Ok(new ApiResponse<RecordResponse>(MapToResponse(result)));
    }

    /// <summary>Update specific fields on an existing record.</summary>
    [HttpPatch("tables/{tableId:guid}/records/{id:guid}")]
    [RequirePermission(PermissionCodes.RecordsUpdate)]
    [RequireAppAccess(AppAccess.Read, AppAccessResolver.ByTableId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid tableId, Guid id, [FromBody] UpdateRecordRequest request, CancellationToken ct)
    {
        var flags = await _appAccessService.GetPermissionFlagsByTablePublicIdAsync(tableId, ct);
        if (!flags.CanEdit)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = new { code = "FORBIDDEN", message = "Your role does not allow editing records." } });

        var command = new UpdateRecordCommand(tableId, id, ParseFieldValues(request.Fields));
        await _updateHandler.HandleAsync(command, ct);
        return NoContent();
    }

    /// <summary>Bulk soft-delete up to 500 records by public ID.</summary>
    [HttpPost("tables/{tableId:guid}/records/bulk-delete")]
    [RequirePermission(PermissionCodes.RecordsDelete)]
    [RequireAppAccess(AppAccess.Read, AppAccessResolver.ByTableId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BulkDelete(Guid tableId, [FromBody] BulkDeleteRequest request, CancellationToken ct)
    {
        var flags = await _appAccessService.GetPermissionFlagsByTablePublicIdAsync(tableId, ct);
        if (!flags.CanDelete)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = new { code = "FORBIDDEN", message = "Your role does not allow deleting records." } });

        await _bulkDeleteHandler.HandleAsync(new BulkDeleteRecordsCommand(tableId, request.Ids), ct);
        return NoContent();
    }

    /// <summary>Soft-delete a record.</summary>
    [HttpDelete("tables/{tableId:guid}/records/{id:guid}")]
    [RequirePermission(PermissionCodes.RecordsDelete)]
    [RequireAppAccess(AppAccess.Read, AppAccessResolver.ByTableId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid tableId, Guid id, CancellationToken ct)
    {
        var flags = await _appAccessService.GetPermissionFlagsByTablePublicIdAsync(tableId, ct);
        if (!flags.CanDelete)
            return StatusCode(StatusCodes.Status403Forbidden, new { error = new { code = "FORBIDDEN", message = "Your role does not allow deleting records." } });

        await _deleteHandler.HandleAsync(new DeleteRecordCommand(tableId, id), ct);
        return NoContent();
    }

    public record BulkDeleteRequest(List<Guid> Ids);

    private static IReadOnlyDictionary<long, object?> ParseFieldValues(Dictionary<string, JsonElement> fields)
    {
        var result = new Dictionary<long, object?>();
        foreach (var (key, el) in fields)
        {
            if (!long.TryParse(key, out var fieldId)) continue;
            result[fieldId] = el.ValueKind switch
            {
                JsonValueKind.String => el.GetString(),
                JsonValueKind.Number => el.GetDecimal(),
                JsonValueKind.True => (object?)true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => null,
            };
        }
        return result;
    }

    private static RecordResponse MapToResponse(RecordResult r) => new()
    {
        Id = r.Id,
        CreatedOn = r.CreatedOn,
        ModifiedOn = r.ModifiedOn,
        Fields = r.Fields,
    };
}
