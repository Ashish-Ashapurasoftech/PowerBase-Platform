using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Models.Apps;
using PowerBase.Application.Apps.Commands.UpdateAppBranding;
using PowerBase.Application.Apps.Queries.GetAppBranding;
using PowerBase.Domain.ValueObjects;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("apps/{appId:guid}/branding")]
[RequireAuth]
public class AppBrandingController : ControllerBase
{
    private readonly GetAppBrandingQueryHandler _getHandler;
    private readonly UpdateAppBrandingCommandHandler _updateHandler;

    public AppBrandingController(
        GetAppBrandingQueryHandler getHandler,
        UpdateAppBrandingCommandHandler updateHandler)
    {
        _getHandler = getHandler;
        _updateHandler = updateHandler;
    }

    /// <summary>Get this app's Branding (appearance defaults) and Layout settings.</summary>
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<AppBrandingResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get([FromRoute] Guid appId, CancellationToken ct)
    {
        var app = await _getHandler.HandleAsync(new GetAppBrandingQuery(appId), ct);

        var jsonOptions = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var appearance = string.IsNullOrEmpty(app.Branding)
            ? new AppBrandingSettings()
            : JsonSerializer.Deserialize<AppBrandingSettings>(app.Branding, jsonOptions) ?? new AppBrandingSettings();
        var layout = string.IsNullOrEmpty(app.LayoutSettings)
            ? new AppLayoutSettings()
            : JsonSerializer.Deserialize<AppLayoutSettings>(app.LayoutSettings, jsonOptions) ?? new AppLayoutSettings();

        var response = new AppBrandingResponse { Appearance = appearance, Layout = layout };
        return Ok(new ApiResponse<AppBrandingResponse>(response));
    }

    /// <summary>Update this app's Branding (appearance defaults) and/or Layout settings.
    /// Requires App Administrator — enforced in the handler.</summary>
    [HttpPut]
    [ProducesResponseType(typeof(ApiResponse<AppBrandingResponse>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Update([FromRoute] Guid appId, [FromBody] UpdateAppBrandingRequest request, CancellationToken ct)
    {
        await _updateHandler.HandleAsync(new UpdateAppBrandingCommand(appId, request.Appearance, request.Layout), ct);
        return await Get(appId, ct);
    }
}
