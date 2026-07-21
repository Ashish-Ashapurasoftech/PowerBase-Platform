using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Common.Interfaces;
using PowerBase.API.Models;
using PowerBase.API.Models.Fields;
using PowerBase.API.Models.Tables;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Tables.Commands.CreateTable;
using PowerBase.Application.Tables.Commands.DeleteTable;
using PowerBase.Application.Tables.Commands.UpdateTable;
using PowerBase.Application.Tables.Queries.GetTable;
using PowerBase.Application.Tables.Queries.ListTables;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.API.Controllers;

[ApiController]
public class TablesController : ControllerBase
{
    private readonly CreateTableCommandHandler _createHandler;
    private readonly UpdateTableCommandHandler _updateHandler;
    private readonly DeleteTableCommandHandler _deleteHandler;
    private readonly GetTableQueryHandler _getHandler;
    private readonly ListTablesQueryHandler _listHandler;

    public TablesController(
        CreateTableCommandHandler createHandler,
        UpdateTableCommandHandler updateHandler,
        DeleteTableCommandHandler deleteHandler,
        GetTableQueryHandler getHandler,
        ListTablesQueryHandler listHandler)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _getHandler = getHandler;
        _listHandler = listHandler;
    }

    /// <summary>Create a table inside an app (also provisions the physical data table).</summary>
    [HttpPost("apps/{appId:guid}/tables")]

    [RequireAppPermission(PermissionCodes.TablesCreate, AppAccessResolver.ByAppId)]
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

    [RequireAppPermission(PermissionCodes.TablesRead, AppAccessResolver.ByAppId)]
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

    [RequireAppPermission(PermissionCodes.TablesRead, AppAccessResolver.ByTablePublicId)]
    [ProducesResponseType(typeof(ApiResponse<TableResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(Guid publicId, CancellationToken ct)
    {
        var result = await _getHandler.HandleAsync(new GetTableQuery(publicId), ct);
        return Ok(new ApiResponse<TableResponse>(MapToResponse(result)));
    }

    /// <summary>Update a table's name, labels, description, or icon.</summary>
    [HttpPatch("tables/{publicId:guid}")]

    [RequireAppPermission(PermissionCodes.TablesUpdate, AppAccessResolver.ByTablePublicId)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid publicId, [FromBody] UpdateTableRequest request, CancellationToken ct)
    {
        await _updateHandler.HandleAsync(new UpdateTableCommand(
            publicId, request.Name, request.SingularLabel, request.PluralLabel, request.Description, request.Icon,
            request.DefaultRecordPickerField1Id, request.DefaultRecordPickerField2Id, request.DefaultRecordPickerField3Id), ct);
        return NoContent();
    }

    /// <summary>Soft-delete a table by its public ID (does not drop the physical table).</summary>
    [HttpDelete("tables/{publicId:guid}")]

    [RequireAppPermission(PermissionCodes.TablesDelete, AppAccessResolver.ByTablePublicId)]
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
        DefaultRecordPickerField1Id = t.DefaultRecordPickerField1Id,
        DefaultRecordPickerField2Id = t.DefaultRecordPickerField2Id,
        DefaultRecordPickerField3Id = t.DefaultRecordPickerField3Id,
        KeyFieldFid = ResolveKeyFieldFid(t.KeyFieldId, t.Fields),
        RecordCount = t.RecordCount,
        CreatedOn = t.CreatedOn,
        Fields = t.Fields.Select(MapFieldToResponse).ToList(),
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
        DefaultRecordPickerField1Id = r.Table.DefaultRecordPickerField1Id,
        DefaultRecordPickerField2Id = r.Table.DefaultRecordPickerField2Id,
        DefaultRecordPickerField3Id = r.Table.DefaultRecordPickerField3Id,
        KeyFieldFid = ResolveKeyFieldFid(r.Table.KeyFieldId, r.Fields),
        RecordCount = r.Table.RecordCount,
        CreatedOn = r.Table.CreatedOn,
        Fields = r.Fields.Select(MapFieldToResponse).ToList(),
    };

    private static int? ResolveKeyFieldFid(long? keyFieldId, IReadOnlyList<AppField> fields) =>
        keyFieldId is null ? null : fields.FirstOrDefault(f => f.Id == keyFieldId.Value)?.Fid;

    private static FieldResponse MapFieldToResponse(AppField f) => new()
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
        Fid = f.Fid,
        Settings = f.Settings,
        CreatedOn = f.CreatedOn,
    };
}
