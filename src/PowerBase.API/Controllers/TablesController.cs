using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Fields;
using PowerBase.API.Models.Tables;
using PowerBase.Application.Tables.Commands.CreateTable;
using PowerBase.Application.Tables.Commands.DeleteTable;
using PowerBase.Application.Tables.Queries.GetTable;
using PowerBase.Application.Tables.Queries.ListTables;
using PowerBase.Domain.Entities;

namespace PowerBase.API.Controllers;

[ApiController]
[RequireAuth]
public class TablesController : ControllerBase
{
    private readonly CreateTableCommandHandler _createHandler;
    private readonly DeleteTableCommandHandler _deleteHandler;
    private readonly GetTableQueryHandler _getHandler;
    private readonly ListTablesQueryHandler _listHandler;

    public TablesController(
        CreateTableCommandHandler createHandler,
        DeleteTableCommandHandler deleteHandler,
        GetTableQueryHandler getHandler,
        ListTablesQueryHandler listHandler)
    {
        _createHandler = createHandler;
        _deleteHandler = deleteHandler;
        _getHandler = getHandler;
        _listHandler = listHandler;
    }

    /// <summary>Create a table inside an app (also provisions the physical data table).</summary>
    [HttpPost("apps/{appId:guid}/tables")]
    [ProducesResponseType(typeof(ApiResponse<TableResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(Guid appId, [FromBody] CreateTableRequest request, CancellationToken ct)
    {
        var command = new CreateTableCommand(appId, request.Name, request.SingularLabel, request.PluralLabel, request.Description, request.Icon);
        var result = await _createHandler.HandleAsync(command, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<TableResponse>(MapToResponse(result)));
    }

    /// <summary>List all tables for an app.</summary>
    [HttpGet("apps/{appId:guid}/tables")]
    [ProducesResponseType(typeof(ApiListResponse<TableResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(Guid appId, CancellationToken ct)
    {
        var tables = await _listHandler.HandleAsync(new ListTablesQuery(appId), ct);
        var items = tables.Select(MapToResponse).ToList();
        return Ok(new ApiListResponse<TableResponse>(items, items.Count, 1, items.Count));
    }

    /// <summary>Get a single table by its public ID, including its fields.</summary>
    [HttpGet("tables/{publicId:guid}")]
    [ProducesResponseType(typeof(ApiResponse<TableResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid publicId, CancellationToken ct)
    {
        var result = await _getHandler.HandleAsync(new GetTableQuery(publicId), ct);
        return Ok(new ApiResponse<TableResponse>(MapToResponse(result)));
    }

    /// <summary>Soft-delete a table by its public ID (does not drop the physical table).</summary>
    [HttpDelete("tables/{publicId:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete(Guid publicId, CancellationToken ct)
    {
        await _deleteHandler.HandleAsync(new DeleteTableCommand(publicId), ct);
        return NoContent();
    }

    private static TableResponse MapToResponse(CreateTableResult r) => new()
    {
        PublicId = r.PublicId,
        Name = r.Name,
        SingularLabel = r.SingularLabel,
        PluralLabel = r.PluralLabel,
        Description = r.Description,
        Icon = r.Icon,
        PhysicalTableName = r.PhysicalTableName,
        RecordCount = r.RecordCount,
        CreatedOn = r.CreatedOn,
        Fields = [],
    };

    private static TableResponse MapToResponse(AppTable t) => new()
    {
        PublicId = t.PublicId,
        Name = t.Name,
        SingularLabel = t.SingularLabel,
        PluralLabel = t.PluralLabel,
        Description = t.Description,
        Icon = t.Icon,
        PhysicalTableName = t.PhysicalTableName,
        RecordCount = t.RecordCount,
        CreatedOn = t.CreatedOn,
        Fields = [],
    };

    private static TableResponse MapToResponse(GetTableResult r) => new()
    {
        PublicId = r.Table.PublicId,
        Name = r.Table.Name,
        SingularLabel = r.Table.SingularLabel,
        PluralLabel = r.Table.PluralLabel,
        Description = r.Table.Description,
        Icon = r.Table.Icon,
        PhysicalTableName = r.Table.PhysicalTableName,
        RecordCount = r.Table.RecordCount,
        CreatedOn = r.Table.CreatedOn,
        Fields = r.Fields.Select(MapFieldToResponse).ToList(),
    };

    private static FieldResponse MapFieldToResponse(AppField f) => new()
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
