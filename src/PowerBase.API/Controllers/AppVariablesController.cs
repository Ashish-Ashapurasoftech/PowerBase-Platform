using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Apps;
using PowerBase.Application.Apps.Commands.CreateAppVariable;
using PowerBase.Application.Apps.Commands.DeleteAppVariable;
using PowerBase.Application.Apps.Commands.UpdateAppVariable;

using PowerBase.Application.Apps.Queries.ListAppVariables;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Constants;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("apps/{appId:guid}/variables")]
[RequireAuth]
public class AppVariablesController : ControllerBase
{
    private readonly ListAppVariablesQueryHandler _listHandler;
    private readonly CreateAppVariableCommandHandler _createHandler;
    private readonly UpdateAppVariableCommandHandler _updateHandler;
    private readonly DeleteAppVariableCommandHandler _deleteHandler;

    public AppVariablesController(
        ListAppVariablesQueryHandler listHandler,
        CreateAppVariableCommandHandler createHandler,
        UpdateAppVariableCommandHandler updateHandler,
        DeleteAppVariableCommandHandler deleteHandler)
    {
        _listHandler = listHandler;
        _createHandler = createHandler;
        _updateHandler = updateHandler;
        _deleteHandler = deleteHandler;
    }

    /// <summary>List all variables defined for this app.</summary>
    [HttpGet]

    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<AppVariableResponse>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List([FromRoute] Guid appId, CancellationToken ct)
    {
        var variables = await _listHandler.HandleAsync(new ListAppVariablesQuery(appId), ct);
        var response = variables.Select(v => new AppVariableResponse
        {
            PublicId = v.PublicId,
            Name = v.Name,
            Value = v.Value,
            Description = v.Description,
            CreatedOn = v.CreatedOn,
            ModifiedOn = v.ModifiedOn
        }).ToList();

        return Ok(new ApiResponse<IReadOnlyList<AppVariableResponse>>(response));
    }

    /// <summary>Create a variable for this app.</summary>
    [HttpPost]

    [ProducesResponseType(typeof(ApiResponse<AppVariableResponse>), StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Create([FromRoute] Guid appId, [FromBody] CreateAppVariableRequest request, CancellationToken ct)
    {
        var result = await _createHandler.HandleAsync(new CreateAppVariableCommand(appId, request.Name, request.Value, request.Description), ct);
        var response = new AppVariableResponse
        {
            PublicId = result.PublicId,
            Name = result.Name,
            Value = result.Value,
            Description = result.Description,
            CreatedOn = result.CreatedOn,
            ModifiedOn = result.ModifiedOn
        };

        return StatusCode(StatusCodes.Status201Created, new ApiResponse<AppVariableResponse>(response));
    }

    /// <summary>Update a variable for this app.</summary>
    [HttpPatch("{id:guid}")]

    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> Update(
        [FromRoute] Guid appId,
        [FromRoute] Guid id,
        [FromBody] UpdateAppVariableRequest request,
        CancellationToken ct)
    {
        await _updateHandler.HandleAsync(new UpdateAppVariableCommand(appId, id, request.Name, request.Value, request.Description), ct);
        return NoContent();
    }

    /// <summary>Delete a variable from this app.</summary>
    [HttpDelete("{id:guid}")]

    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Delete([FromRoute] Guid appId, [FromRoute] Guid id, CancellationToken ct)
    {
        await _deleteHandler.HandleAsync(new DeleteAppVariableCommand(appId, id), ct);
        return NoContent();
    }
}
