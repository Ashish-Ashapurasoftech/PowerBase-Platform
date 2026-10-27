using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.FieldTypeConversion;
using PowerBase.Application.Fields.TypeConversion;
using PowerBase.Domain.Constants;

namespace PowerBase.API.Controllers;

/// <summary>Change a field's type (Quickbase "Change Type") — rules in docs/field-type-conversion.md.</summary>
[ApiController]
public class FieldTypeConversionController : ControllerBase
{
    private readonly GetFieldTypeChangeTargetsQueryHandler _targetsHandler;
    private readonly PreviewFieldTypeChangeQueryHandler _previewHandler;
    private readonly ChangeFieldTypeCommandHandler _changeHandler;

    public FieldTypeConversionController(
        GetFieldTypeChangeTargetsQueryHandler targetsHandler,
        PreviewFieldTypeChangeQueryHandler previewHandler,
        ChangeFieldTypeCommandHandler changeHandler)
    {
        _targetsHandler = targetsHandler;
        _previewHandler = previewHandler;
        _changeHandler = changeHandler;
    }

    /// <summary>The types this field may change to (the Change Type page's "to:" dropdown).</summary>
    [HttpGet("tables/{tableId:guid}/fields/{publicId:guid}/change-type/targets")]
    [RequireAppPermission(PermissionCodes.FieldsUpdate, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<FieldTypeChangeTargetsDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Targets(Guid tableId, Guid publicId, CancellationToken ct)
    {
        var dto = await _targetsHandler.HandleAsync(new GetFieldTypeChangeTargetsQuery(tableId, publicId), ct);
        return Ok(new ApiResponse<FieldTypeChangeTargetsDto>(dto));
    }

    /// <summary>What changing to the target type would do — counts, before/after samples, blockers and
    /// affected formulas/reports. Read-only: nothing is changed.</summary>
    [HttpPost("tables/{tableId:guid}/fields/{publicId:guid}/change-type/preview")]
    [RequireAppPermission(PermissionCodes.FieldsUpdate, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<FieldTypeChangePreviewDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Preview(Guid tableId, Guid publicId, [FromBody] PreviewFieldTypeChangeRequest request, CancellationToken ct)
    {
        var dto = await _previewHandler.HandleAsync(new PreviewFieldTypeChangeQuery(tableId, publicId, request.TargetTypeCode), ct);
        return Ok(new ApiResponse<FieldTypeChangePreviewDto>(dto));
    }

    /// <summary>Changes the field's type and converts every record's value. Values that can't be converted become
    /// blank; no record is deleted.</summary>
    [HttpPost("tables/{tableId:guid}/fields/{publicId:guid}/change-type")]
    [RequireAppPermission(PermissionCodes.FieldsUpdate, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<ChangeFieldTypeResultDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Change(Guid tableId, Guid publicId, [FromBody] ChangeFieldTypeRequest request, CancellationToken ct)
    {
        var result = await _changeHandler.HandleAsync(
            new ChangeFieldTypeCommand(tableId, publicId, request.TargetTypeCode, request.CommitMessage), ct);
        return Ok(new ApiResponse<ChangeFieldTypeResultDto>(result));
    }
}
