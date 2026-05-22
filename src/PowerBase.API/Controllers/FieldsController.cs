using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Common.Interfaces;
using PowerBase.API.Models;
using PowerBase.API.Models.Fields;
using PowerBase.Application.Fields.Commands.CreateField;
using PowerBase.Application.Fields.Commands.DeleteField;
using PowerBase.Application.Fields.Commands.UpdateField;
using PowerBase.Application.Fields.Queries.ListFields;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.API.Controllers;

[ApiController]
public class FieldsController : ControllerBase
{
    private readonly CreateFieldCommandHandler _createHandler;
    private readonly UpdateFieldCommandHandler _updateHandler;
    private readonly DeleteFieldCommandHandler _deleteHandler;
    private readonly ListFieldsQueryHandler _listHandler;

    public FieldsController(
        CreateFieldCommandHandler createHandler,
        UpdateFieldCommandHandler updateHandler,
        DeleteFieldCommandHandler deleteHandler,
        ListFieldsQueryHandler listHandler)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _listHandler = listHandler;
    }

    /// <summary>Add a field to a table (also runs ALTER TABLE on the physical data table).</summary>
    [HttpPost("tables/{tableId:guid}/fields")]
    [RequirePermission(PermissionCodes.FieldsCreate)]
    [RequireAppAccess(AppAccess.Admin, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<FieldResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(Guid tableId, [FromBody] CreateFieldRequest request, CancellationToken ct)
    {
        var command = new CreateFieldCommand(tableId, request.TypeCode, request.Name, request.Label, request.Description, request.IsRequired);
        var result = await _createHandler.HandleAsync(command, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<FieldResponse>(MapToResponse(result)));
    }

    /// <summary>List all fields for a table.</summary>
    [HttpGet("tables/{tableId:guid}/fields")]
    [RequirePermission(PermissionCodes.FieldsRead)]
    [RequireAppAccess(AppAccess.Read, AppAccessResolver.ByTableId)]
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
    [HttpPatch("tables/{tableId:guid}/fields/{fieldId:guid}")]
    [RequirePermission(PermissionCodes.FieldsCreate)]
    [RequireAppAccess(AppAccess.Admin, AppAccessResolver.ByTableId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid tableId, Guid fieldId, [FromBody] UpdateFieldRequest request, CancellationToken ct)
    {
        await _updateHandler.HandleAsync(new UpdateFieldCommand(
            tableId, fieldId,
            request.Name, request.Label, request.Description,
            request.IsRequired, request.DefaultValue,
            request.IsSearchable, request.IsSortable,
            request.IsFilterable, request.IsReportable), ct);
        return NoContent();
    }

    /// <summary>Soft-delete a field (does not run DROP COLUMN).</summary>
    [HttpDelete("tables/{tableId:guid}/fields/{fieldId:guid}")]
    [RequirePermission(PermissionCodes.FieldsCreate)]
    [RequireAppAccess(AppAccess.Admin, AppAccessResolver.ByTableId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid tableId, Guid fieldId, CancellationToken ct)
    {
        await _deleteHandler.HandleAsync(new DeleteFieldCommand(tableId, fieldId), ct);
        return NoContent();
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
        IsSystem = false,
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
        IsUnique = f.IsUnique,
        IsSystem = f.IsSystem,
        CreatedOn = f.CreatedOn,
    };
}
