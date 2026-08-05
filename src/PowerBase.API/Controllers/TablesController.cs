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
    [ProducesResponseType(typeof(ApiResponse<TableSummaryResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create(Guid appId, [FromBody] CreateTableRequest request, CancellationToken ct)
    {
        var command = new CreateTableCommand(appId, request.Name, request.SingularLabel, request.PluralLabel, request.Description, request.Icon);
        var result = await _createHandler.HandleAsync(command, ct);
        return StatusCode(StatusCodes.Status201Created, new ApiResponse<TableSummaryResponse>(MapToSummaryResponse(result)));
    }

    /// <summary>List tables for an app. Supports paging, search (by name or singular label), and sorting.
    /// Omit <paramref name="isShowInBar"/> for the full listing (e.g. a "manage tables" page); pass it
    /// (true/false) to return only tables matching that flag (e.g. the sidebar navigation).</summary>
    [HttpGet("apps/{appId:guid}/tables")]

    [RequireAppPermission(PermissionCodes.TablesRead, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiListResponse<TableListItemResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> List(
        Guid appId,
        [FromQuery] int page = 1,
        [FromQuery] int pageSize = 20,
        [FromQuery] string? search = null,
        [FromQuery] string sortBy = "name",
        [FromQuery] bool sortDesc = false,
        [FromQuery] bool? isShowInBar = null,
        CancellationToken ct = default)
    {
        var result = await _listHandler.HandleAsync(new ListTablesQuery(appId, page, pageSize, search, sortBy, sortDesc, isShowInBar), ct);
        var items = result.Items.Select(MapToListItemResponse).ToList();
        return Ok(new ApiListResponse<TableListItemResponse>(items, result.Total, result.Page, result.PageSize));
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
    [ProducesResponseType(typeof(ApiResponse<TableSummaryResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update(Guid publicId, [FromBody] UpdateTableRequest request, CancellationToken ct)
    {
        var result = await _updateHandler.HandleAsync(new UpdateTableCommand(
            publicId, request.Name, request.SingularLabel, request.PluralLabel, request.Description, request.Icon,
            request.DefaultRecordPickerField1Id, request.DefaultRecordPickerField2Id, request.DefaultRecordPickerField3Id,
            request.IsShowInBar), ct);
        return Ok(new ApiResponse<TableSummaryResponse>(MapToSummaryResponse(result)));
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
        IsShowInBar = r.IsShowInBar,
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
        IsShowInBar = t.IsShowInBar,
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
        IsShowInBar = r.Table.IsShowInBar,
        CreatedOn = r.Table.CreatedOn,
        Fields = r.Fields.Select(MapFieldToResponse).ToList(),
    };

    private static TableSummaryResponse MapToSummaryResponse(CreateTableResult r) => new()
    {
        PublicId = r.PublicId,
        Name = r.Name,
        SingularLabel = r.SingularLabel,
        Icon = r.Icon,
        IsShowInBar = r.IsShowInBar,
        CreatedOn = r.CreatedOn,
    };

    private static TableSummaryResponse MapToSummaryResponse(PowerBase.Application.Tables.Commands.UpdateTable.UpdateTableResult r) => new()
    {
        PublicId = r.PublicId,
        Name = r.Name,
        SingularLabel = r.SingularLabel,
        Icon = r.Icon,
        IsShowInBar = r.IsShowInBar,
        CreatedOn = r.CreatedOn,
    };

    private static TableListItemResponse MapToListItemResponse(AppTableListItemDto t) => new()
    {
        PublicId = t.PublicId,
        Name = t.Name,
        SingularLabel = t.SingularLabel,
        Icon = t.Icon,
        RecordCount = t.RecordCount,
        FieldCount = t.FieldCount,
        IsShowInBar = t.IsShowInBar,
        CreatedOn = t.CreatedOn,
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
