using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Capabilities;
using PowerBase.Application.Capabilities.Commands.SaveRoleCapabilities;
using PowerBase.Application.Capabilities.Commands.UpdateRoleCapability;
using PowerBase.Application.Capabilities.Dtos;
using PowerBase.Application.Capabilities.Queries.GetRoleCapabilities;
using PowerBase.Application.Capabilities.Queries.ListCapabilities;
using PowerBase.Domain.Constants;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("capabilities")]
[Route("api/v1/capabilities")]
[RequireAuth]
public class CapabilitiesController : ControllerBase
{
    private readonly ListCapabilitiesQueryHandler _listHandler;
    private readonly GetRoleCapabilitiesQueryHandler _getRoleCapabilitiesHandler;
    private readonly SaveRoleCapabilitiesCommandHandler _saveRoleCapabilitiesHandler;
    private readonly UpdateRoleCapabilityCommandHandler _updateRoleCapabilityHandler;

    public CapabilitiesController(
        ListCapabilitiesQueryHandler listHandler,
        GetRoleCapabilitiesQueryHandler getRoleCapabilitiesHandler,
        SaveRoleCapabilitiesCommandHandler saveRoleCapabilitiesHandler,
        UpdateRoleCapabilityCommandHandler updateRoleCapabilityHandler)
    {
        _listHandler = listHandler;
        _getRoleCapabilitiesHandler = getRoleCapabilitiesHandler;
        _saveRoleCapabilitiesHandler = saveRoleCapabilitiesHandler;
        _updateRoleCapabilityHandler = updateRoleCapabilityHandler;
    }

    /// <summary>List all builder capabilities and their associated granular permissions.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<CapabilityDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var capabilities = await _listHandler.HandleAsync(new ListCapabilitiesQuery(), ct);
        return Ok(new ApiResponse<IReadOnlyList<CapabilityDto>>(capabilities));
    }

    /// <summary>API 1.1: Save Builder Capabilities — Assign one or more powers (Schema, Form, Report, Automation, Security) to a role.</summary>
    [HttpPost]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Save([FromBody] SaveRoleCapabilitiesRequest request, CancellationToken ct)
    {
        await _saveRoleCapabilitiesHandler.HandleAsync(
            new SaveRoleCapabilitiesCommand(request.RoleId, request.Capabilities), ct);
        return Ok(new ApiResponse<string>("Role capabilities saved successfully."));
    }

    /// <summary>API 1.2: Get Builder Capabilities — Fetch which powers a role currently has.</summary>
    [HttpGet("{roleId:guid}")]
    [RequireAppPermission(PermissionCodes.RolesManage, AppAccessResolver.ByRoleId)]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<RoleCapabilityDto>>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByRole(Guid roleId, CancellationToken ct)
    {
        var capabilities = await _getRoleCapabilitiesHandler.HandleAsync(
            new GetRoleCapabilitiesQuery(roleId), ct);
        return Ok(new ApiResponse<IReadOnlyList<RoleCapabilityDto>>(capabilities));
    }

    /// <summary>API 1.3: Update/Revoke Capability — Turn a specific power on or off for a role.</summary>
    [HttpPatch("{roleId:guid}")]
    [RequireAppPermission(PermissionCodes.RolesManage, AppAccessResolver.ByRoleId)]
    [ProducesResponseType(typeof(ApiResponse<string>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> UpdateCapability(
        Guid roleId,
        [FromBody] UpdateRoleCapabilityRequest request,
        CancellationToken ct)
    {
        await _updateRoleCapabilityHandler.HandleAsync(
            new UpdateRoleCapabilityCommand(roleId, request.Capability, request.Enabled), ct);
        return Ok(new ApiResponse<string>($"Capability '{request.Capability}' updated successfully."));
    }
}
