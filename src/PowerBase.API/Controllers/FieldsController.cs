using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Common.Interfaces;
using PowerBase.API.Models;
using PowerBase.API.Models.Fields;
using PowerBase.Application.Fields.Commands.CreateField;
using PowerBase.Application.Fields.Queries.ListFields;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.API.Controllers;

[ApiController]
public class FieldsController : ControllerBase
{
    private readonly CreateFieldCommandHandler _createHandler;
    private readonly ListFieldsQueryHandler _listHandler;

    public FieldsController(CreateFieldCommandHandler createHandler, ListFieldsQueryHandler listHandler)
    {
        _createHandler = createHandler;
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
        DisplayOrder = r.DisplayOrder,
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
        IsRequired = f.IsRequired,
        IsSystem = f.IsSystem,
        DisplayOrder = f.DisplayOrder,
        CreatedOn = f.CreatedOn,
    };
}
