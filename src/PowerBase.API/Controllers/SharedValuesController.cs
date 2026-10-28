using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Models;
using PowerBase.Application.Fields.Queries.SharedValues;

namespace PowerBase.API.Controllers;

/// <summary>Shared values: fields whose record values may be another field's dropdown choices, across apps.</summary>
[ApiController]
public class SharedValuesController : ControllerBase
{
    private readonly ListSharedFieldsQueryHandler _listHandler;
    private readonly GetSharedFieldValuesQueryHandler _valuesHandler;

    public SharedValuesController(ListSharedFieldsQueryHandler listHandler, GetSharedFieldValuesQueryHandler valuesHandler)
    {
        _listHandler = listHandler;
        _valuesHandler = valuesHandler;
    }

    /// <summary>The Shared-values fields of every app the caller belongs to — the "From another field" picker.</summary>
    [HttpGet("shared-fields")]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<SharedFieldDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct)
        => Ok(new ApiResponse<IReadOnlyList<SharedFieldDto>>(await _listHandler.HandleAsync(ct)));

    /// <summary>The current distinct values of a Shared-values field (read fresh on every call).</summary>
    [HttpGet("shared-fields/{tableId:guid}/{fieldId:int}/values")]
    [ProducesResponseType(typeof(ApiResponse<SharedValuesResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Values(Guid tableId, int fieldId, [FromQuery] int limit = GetSharedFieldValuesQueryHandler.MaxValues, CancellationToken ct = default)
        => Ok(new ApiResponse<SharedValuesResponse>(await _valuesHandler.HandleAsync(tableId, fieldId, limit, ct)));
}
