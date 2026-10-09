using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.Application.Formulas.Queries;
using PowerBase.Application.Imports;

namespace PowerBase.API.Controllers;

/// <summary>Live checking of a formula typed on the import screen, against what the import will compile it against.</summary>
[ApiController]
[RequireAuth]
public sealed class ImportFormulaController(ValidateImportFormulaHandler validate) : ControllerBase
{
    [HttpPost("apps/{appId:guid}/import-formula/validate")]
    [ProducesResponseType(typeof(ApiResponse<ValidateFormulaResult>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Validate(Guid appId, [FromBody] ImportFormulaCheck request, CancellationToken ct) =>
        Ok(new ApiResponse<ValidateFormulaResult>(await validate.HandleAsync(appId, request, ct)));
}
