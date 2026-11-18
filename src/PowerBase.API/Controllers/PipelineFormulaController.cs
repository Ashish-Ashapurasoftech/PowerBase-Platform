using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.Application.Formulas.Queries;
using PowerBase.Application.Pipelines.Queries.ValidatePipelineFormula;

namespace PowerBase.API.Controllers;

/// <summary>Live checking of a formula typed into a PowerFlow step value.</summary>
[ApiController]
[RequireAuth]
public sealed class PipelineFormulaController(ValidatePipelineFormulaHandler validate) : ControllerBase
{
    [HttpPost("pipelines/formula/validate")]
    [ProducesResponseType(typeof(ApiResponse<ValidateFormulaResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult Validate([FromBody] PipelineFormulaCheck request) =>
        Ok(new ApiResponse<ValidateFormulaResult>(validate.Handle(request)));
}
