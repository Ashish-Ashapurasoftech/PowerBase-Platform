using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Common.Interfaces;
using PowerBase.API.Models;
using PowerBase.API.Models.Fields;
using PowerBase.Application.Fields.Commands.BulkCreateFields;
using PowerBase.Application.Fields.Commands.CreateField;
using PowerBase.Application.Fields.Commands.DeleteField;
using PowerBase.Application.Fields.Commands.UpdateField;
using PowerBase.Application.Fields.Queries.ListFields;
using PowerBase.Application.Fields.Queries.GetFieldUsage;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.API.Controllers;

[ApiController]
public class FieldsController : ControllerBase
{
    private readonly CreateFieldCommandHandler _createHandler;
    private readonly BulkCreateFieldsCommandHandler _bulkCreateHandler;
    private readonly UpdateFieldCommandHandler _updateHandler;
    private readonly DeleteFieldCommandHandler _deleteHandler;
    private readonly ListFieldsQueryHandler _listHandler;

    public FieldsController(
        CreateFieldCommandHandler createHandler,
        BulkCreateFieldsCommandHandler bulkCreateHandler,
        UpdateFieldCommandHandler updateHandler,
        DeleteFieldCommandHandler deleteHandler,
        ListFieldsQueryHandler listHandler)
    {
        _createHandler = createHandler;
        _bulkCreateHandler = bulkCreateHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _listHandler = listHandler;
    }

    /// <summary>Add a field to a table (also runs ALTER TABLE on the physical data table).</summary>
    [HttpPost("tables/{tableId:guid}/fields")]
    [RequireAppPermission(PermissionCodes.FieldsCreate, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<FieldResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(Guid tableId, [FromBody] CreateFieldRequest request, CancellationToken ct)
    {
        var command = new CreateFieldCommand(tableId, request.TypeCode, request.Name, request.Label, request.Description, request.IsRequired, request.IsAuditable, request.Settings, request.DefaultValue, request.IsEncrypted);
        var result = await _createHandler.HandleAsync(command, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<FieldResponse>(MapToResponse(result)));
    }

    /// <summary>Bulk-add multiple fields to a table in one call.</summary>
    [HttpPost("tables/{tableId:guid}/fields/bulk")]
    [RequireAppPermission(PermissionCodes.FieldsCreate, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiListResponse<FieldResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> BulkCreate(Guid tableId, [FromBody] BulkCreateFieldsRequest request, CancellationToken ct)
    {
        var items = request.Fields
            .Select(f => new BulkCreateFieldItem(f.TypeCode, f.Name, f.Label, f.Description, f.IsRequired, f.IsAuditable, f.Settings, f.DefaultValue, f.IsEncrypted))
            .ToList();
        var command = new BulkCreateFieldsCommand(tableId, items);
        var results = await _bulkCreateHandler.HandleAsync(command, ct);
        var responses = results.Select(MapToResponse).ToList();
        return StatusCode(StatusCodes.Status201Created, new ApiListResponse<FieldResponse>(responses, responses.Count, 1, responses.Count));
    }

    /// <summary>List all fields for a table.</summary>
    [HttpGet("tables/{tableId:guid}/fields")]
    [RequireAppPermission(PermissionCodes.FieldsRead, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiListResponse<FieldResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(Guid tableId, CancellationToken ct)
    {
        var fields = await _listHandler.HandleAsync(new ListFieldsQuery(tableId), ct);
        var items = fields.Select(MapToResponse).ToList();
        return Ok(new ApiListResponse<FieldResponse>(items, items.Count, 1, items.Count));
    }

    /// <summary>Update a field's properties (name, label, required, searchable, reportable, etc.).</summary>
    [HttpPatch("tables/{tableId:guid}/fields/{fieldId:int}")]
    [RequireAppPermission(PermissionCodes.FieldsUpdate, AppAccessResolver.ByTableId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid tableId, int fieldId, [FromBody] UpdateFieldRequest request, CancellationToken ct)
    {
        await _updateHandler.HandleAsync(new UpdateFieldCommand(
            tableId, fieldId,
            request.Name, request.Label, request.Description,
            request.IsRequired, request.DefaultValue,
            request.IsSearchable, request.IsSortable,
            request.IsFilterable, request.IsReportable, request.IsAuditable,
            request.IsUnique, request.IsEncrypted, request.Settings), ct);
        return NoContent();
    }

    /// <summary>Soft-delete a field (does not run DROP COLUMN).</summary>
    [HttpDelete("tables/{tableId:guid}/fields/{fieldId:int}")]
    [RequireAppPermission(PermissionCodes.FieldsUpdate, AppAccessResolver.ByTableId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid tableId, int fieldId, CancellationToken ct)
    {
        await _deleteHandler.HandleAsync(new DeleteFieldCommand(tableId, fieldId), ct);
        return NoContent();
    }

    /// <summary>Soft-delete multiple fields.</summary>
    [HttpDelete("tables/{tableId:guid}/fields")]
    [RequireAppPermission(PermissionCodes.FieldsUpdate, AppAccessResolver.ByTableId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> BulkDelete(
        [FromServices] PowerBase.Application.Fields.Commands.BulkDeleteFields.BulkDeleteFieldsCommandHandler bulkDeleteHandler,
        Guid tableId, [FromBody] BulkDeleteFieldsRequest request, CancellationToken ct)
    {
        await bulkDeleteHandler.HandleAsync(new PowerBase.Application.Fields.Commands.BulkDeleteFields.BulkDeleteFieldsCommand(tableId, request.FieldIds), ct);
        return NoContent();
    }

    /// <summary>Set (or reset) a table's key field. fieldFid = 0 resets to the default Record ID#.
    /// If the table already has parent-side relationships, retry with force=true to confirm the
    /// cascade rewire of every related record's reference.</summary>
    [HttpPost("tables/{tableId:guid}/fields/{fieldFid:int}/set-key")]
    [RequireAppPermission(PermissionCodes.FieldsUpdate, AppAccessResolver.ByTableId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status422UnprocessableEntity)]
    public async Task<IActionResult> SetKey(
        [FromServices] PowerBase.Application.Fields.Commands.SetKey.SetKeyCommandHandler setKeyHandler,
        Guid tableId, int fieldFid, [FromQuery] bool force, CancellationToken ct)
    {
        await setKeyHandler.HandleAsync(new PowerBase.Application.Fields.Commands.SetKey.SetKeyCommand(
            tableId, fieldFid == 0 ? null : fieldFid, force), ct);
        return NoContent();
    }

    /// <summary>Get field usage (where it is referenced in forms, reports, and roles).</summary>
    [HttpGet("tables/{tableId:guid}/fields/{fieldId:guid}/usage")]
    [RequireAppPermission(PermissionCodes.FieldsRead, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<FieldUsageDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Usage(
        [FromServices] GetFieldUsageQueryHandler usageHandler,
        Guid tableId, Guid fieldId, [FromRoute(Name = "appId")] Guid? appId, CancellationToken ct)
    {
        var dto = await usageHandler.HandleAsync(new GetFieldUsageQuery(tableId, fieldId), ct);
        return Ok(new ApiResponse<FieldUsageDto>(dto));
    }

    private static FieldResponse MapToResponse(CreateFieldResult r) => new()
    {
        Id = r.Id,
        PublicId = r.PublicId,
        Name = r.Name,
        Label = r.Label,
        Description = r.Description,
        TypeCode = r.TypeCode,
        PhysicalColumnName = r.PhysicalColumnName,
        IsRequired = r.IsRequired,
        IsAuditable = r.IsAuditable,
        IsSystem = false,
        Fid = r.Fid,
        Settings = r.Settings,
        CreatedOn = r.CreatedOn,
    };

    private static FieldResponse MapToResponse(AppField f) => new()
    {
        Id = f.Id,
        PublicId = f.PublicId,
        Name = f.Name,
        Label = f.Label,
        Description = f.Description,
        TypeCode = f.TypeCode,
        PhysicalColumnName = f.PhysicalColumnName,
        DefaultValue = f.DefaultValue,
        IsRequired = f.IsRequired,
        IsSearchable = f.IsSearchable,
        IsSortable = f.IsSortable,
        IsFilterable = f.IsFilterable,
        IsReportable = f.IsReportable,
        IsAuditable = f.IsAuditable,
        IsUnique = f.IsUnique,
        IsSystem = f.IsSystem,
        IsEncrypted = f.IsEncrypted,
        Fid = f.Fid,
        Settings = f.Settings,
        CreatedOn = f.CreatedOn,
    };
}
