using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Common.Interfaces;
using PowerBase.API.Models;
using PowerBase.API.Models.Fields;
using PowerBase.API.Models.Tables;
using PowerBase.Application.Common.Models;
using PowerBase.Application.Tables.Commands.BulkDeleteTables;
using PowerBase.Application.Tables.Commands.CreateTable;
using PowerBase.Application.Tables.Commands.DeleteTable;
using PowerBase.Application.Tables.Commands.UpdateTable;
using PowerBase.Application.Tables.Queries.GetTable;
using PowerBase.Application.Tables.Queries.ListTables;
using PowerBase.Application.Tables.Queries.ListTableNavItems;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;

namespace PowerBase.API.Controllers;

[ApiController]
public class TablesController : ControllerBase
{
    private readonly CreateTableCommandHandler _createHandler;
    private readonly UpdateTableCommandHandler _updateHandler;
    private readonly DeleteTableCommandHandler _deleteHandler;
    private readonly BulkDeleteTablesCommandHandler _bulkDeleteHandler;
    private readonly GetTableQueryHandler _getHandler;
    private readonly ListTablesQueryHandler _listHandler;
    private readonly ListTableNavItemsQueryHandler _listNavHandler;

    public TablesController(
        CreateTableCommandHandler createHandler,
        UpdateTableCommandHandler updateHandler,
        DeleteTableCommandHandler deleteHandler,
        BulkDeleteTablesCommandHandler bulkDeleteHandler,
        GetTableQueryHandler getHandler,
        ListTablesQueryHandler listHandler,
        ListTableNavItemsQueryHandler listNavHandler)
    {
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
        _bulkDeleteHandler = bulkDeleteHandler;
        _getHandler = getHandler;
        _listHandler = listHandler;
        _listNavHandler = listNavHandler;
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

    /// <summary>Slim, unpaginated table listing for nav surfaces (sidebar, top nav, table switcher) —
    /// every table in the app, just publicId/name/singularLabel/icon/isShowInBar. Never takes page/
    /// search/sort params: callers filter (isShowInBar) and search (by name) client-side. Use
    /// GET /apps/{appId}/tables instead for the paged, server-searched "manage tables" listing.</summary>
    [HttpGet("apps/{appId:guid}/tables/nav")]

    [RequireAppPermission(PermissionCodes.TablesRead, AppAccessResolver.ByAppId)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<TableNavItemResponse>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListNav(Guid appId, CancellationToken ct)
    {
        var items = await _listNavHandler.HandleAsync(new ListTableNavItemsQuery(appId), ct);
        return Ok(new ApiResponse<IReadOnlyList<TableNavItemResponse>>(items.Select(MapToNavItemResponse).ToList()));
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

    /// <summary>Get a table's Advanced Settings page data — the table's own editable properties
    /// plus a minimal per-field list (id/name/isSystem) for the default record picker dropdowns.
    /// Slimmer than <see cref="Get"/>, which returns each field's full configuration.</summary>
    [HttpGet("tables/{publicId:guid}/advanced-settings")]

    [RequireAppPermission(PermissionCodes.TablesRead, AppAccessResolver.ByTablePublicId)]
    [ProducesResponseType(typeof(ApiResponse<TableAdvancedSettingsResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetAdvancedSettings(Guid publicId, CancellationToken ct)
    {
        var result = await _getHandler.HandleAsync(new GetTableQuery(publicId), ct);
        return Ok(new ApiResponse<TableAdvancedSettingsResponse>(MapToAdvancedSettingsResponse(result)));
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

    /// <summary>Bulk soft-delete tables (and their relationships) in a single request.</summary>
    [HttpPost("apps/{appId:guid}/tables/bulk-delete")]
    [RequireAppPermission(PermissionCodes.TablesDelete, AppAccessResolver.ByAppId)]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> BulkDelete(Guid appId, [FromBody] BulkDeleteTablesRequest request, CancellationToken ct)
    {
        var deletedCount = await _bulkDeleteHandler.HandleAsync(new BulkDeleteTablesCommand(appId, request.PublicIds), ct);
        return Ok(new { success = true, deletedCount });
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

    private static TableNavItemResponse MapToNavItemResponse(AppTableNavItemDto t) => new()
    {
        PublicId = t.PublicId,
        Name = t.Name,
        SingularLabel = t.SingularLabel,
        Icon = t.Icon,
        IsShowInBar = t.IsShowInBar,
    };

    private static TableAdvancedSettingsResponse MapToAdvancedSettingsResponse(GetTableResult r) => new()
    {
        PublicId = r.Table.PublicId,
        Name = r.Table.Name,
        SingularLabel = r.Table.SingularLabel,
        PluralLabel = r.Table.PluralLabel,
        Description = r.Table.Description,
        Icon = r.Table.Icon,
        DefaultRecordPickerField1Id = r.Table.DefaultRecordPickerField1Id,
        DefaultRecordPickerField2Id = r.Table.DefaultRecordPickerField2Id,
        DefaultRecordPickerField3Id = r.Table.DefaultRecordPickerField3Id,
        Fields = r.Fields.Select(f => new TableAdvancedSettingsFieldResponse
        {
            Id = f.Id,
            Name = f.Label,
            IsSystem = f.IsSystem,
        }).ToList(),
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
        IsEncrypted = f.IsEncrypted,
        Fid = f.Fid,
        Settings = f.Settings,
        CreatedOn = f.CreatedOn,
    };
}
