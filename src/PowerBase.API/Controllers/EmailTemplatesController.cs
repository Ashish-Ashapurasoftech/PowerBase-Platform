using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.Application.Import.Jobs;

namespace PowerBase.API.Controllers;

public class SaveEmailTemplateRequest
{
    public string? Subject { get; set; }
    public string? Body { get; set; }
    public bool IsEnabled { get; set; } = true;
}

/// <summary>The emails the platform sends about background app imports. Tenant administrators can change the wording
/// (subject and HTML body, with {{Placeholders}}), switch a template off, or reset it to the built-in default.</summary>
[ApiController]
[Route("email-templates")]
[RequireAuth]
[RequireTenantAdmin]
public class EmailTemplatesController(EmailTemplateService service) : ControllerBase
{
    [HttpGet]
    [ProducesResponseType(typeof(ApiResponse<IReadOnlyList<EmailTemplateDto>>), StatusCodes.Status200OK)]
    public async Task<IActionResult> List(CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<EmailTemplateDto>>(await service.ListAsync(ct)));

    [HttpGet("{key}")]
    [ProducesResponseType(typeof(ApiResponse<EmailTemplateDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Get(string key, CancellationToken ct) =>
        Ok(new ApiResponse<EmailTemplateDto>(await service.GetAsync(key, ct)));

    [HttpPut("{key}")]
    [ProducesResponseType(typeof(ApiResponse<EmailTemplateDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Save(string key, [FromBody] SaveEmailTemplateRequest request, CancellationToken ct) =>
        Ok(new ApiResponse<EmailTemplateDto>(await service.SaveAsync(key, request.Subject, request.Body, request.IsEnabled, ct)));

    /// <summary>Back to the built-in wording.</summary>
    [HttpDelete("{key}")]
    [ProducesResponseType(typeof(ApiResponse<EmailTemplateDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Reset(string key, CancellationToken ct) =>
        Ok(new ApiResponse<EmailTemplateDto>(await service.ResetAsync(key, ct)));
}
