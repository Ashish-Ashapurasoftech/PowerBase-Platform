using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Formulas;
using PowerBase.Application.Formulas.Queries;
using PowerBase.Domain.Constants;

namespace PowerBase.API.Controllers;

[ApiController]
public class FormulaController : ControllerBase
{
    private readonly ValidateFormulaQueryHandler _validateHandler;
    private readonly EvaluateFormulaQueryHandler _evaluateHandler;

    public FormulaController(
        ValidateFormulaQueryHandler validateHandler,
        EvaluateFormulaQueryHandler evaluateHandler)
    {
        _validateHandler = validateHandler;
        _evaluateHandler = evaluateHandler;
    }

    /// <summary>Compile a formula against a table's fields and return diagnostics (drives inline authoring errors).</summary>
    [HttpPost("tables/{tableId:guid}/formula/validate")]
    [RequireAppPermission(PermissionCodes.FieldsRead, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<ValidateFormulaResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Validate(Guid tableId, [FromBody] ValidateFormulaRequest request, CancellationToken ct)
    {
        var result = await _validateHandler.HandleAsync(
            new ValidateFormulaQuery(tableId, request.Expression, request.ExpectedType), ct);
        return Ok(new ApiResponse<ValidateFormulaResult>(result));
    }

    /// <summary>Compile and evaluate a formula against supplied field values (drives live preview).</summary>
    [HttpPost("tables/{tableId:guid}/formula/evaluate")]
    [RequireAppPermission(PermissionCodes.RecordsRead, AppAccessResolver.ByTableId)]
    [ProducesResponseType(typeof(ApiResponse<EvaluateFormulaResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Evaluate(Guid tableId, [FromBody] EvaluateFormulaRequest request, CancellationToken ct)
    {
        var values = new Dictionary<long, object?>();
        if (request.Values is not null)
        {
            foreach (var (key, element) in request.Values)
                if (long.TryParse(key, out var fid))
                    values[fid] = Unwrap(element);
        }

        var result = await _evaluateHandler.HandleAsync(
            new EvaluateFormulaQuery(tableId, request.Expression, request.ExpectedType, values), ct);
        return Ok(new ApiResponse<EvaluateFormulaResult>(result));
    }

    // Unwrap a JSON value into the CLR primitive the engine's value coercion understands.
    private static object? Unwrap(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.String => element.GetString(),
        JsonValueKind.Number => element.TryGetDecimal(out var d) ? d : element.GetDouble(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        _ => element.ToString(),
    };
}
