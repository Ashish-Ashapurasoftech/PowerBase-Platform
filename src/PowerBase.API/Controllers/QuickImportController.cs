using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.Application.Imports;

namespace PowerBase.API.Controllers;

/// <summary>Imports started from a table's own menu, with no template to keep. Permissions are enforced in the handler (the plan builder checks the
/// person's rights on every table the import touches); the controller only shapes the request and the response.</summary>
[ApiController]
[RequireAuth]
public sealed class QuickImportController(StartQuickImportHandler start, IConfiguration configuration) : ControllerBase
{
    /// <summary>Queues a one-off import into the table and returns at once; the run executes in the background and is followed on the run page.</summary>
    [HttpPost("tables/{tableId:guid}/quick-imports")]
    public async Task<IActionResult> Start(Guid tableId, [FromBody] QuickImportRequest request, CancellationToken ct) =>
        Accepted(new { data = new { runId = (await start.HandleAsync(tableId, request, FrontendBaseUrl(), ct, Request.Headers["X-Time-Zone"].ToString())).RunId } });

    /// <summary>The web address the completion email links to: the configured frontend, or the caller's Origin when that is a known
    /// frontend. Never an address an attacker could choose.</summary>
    private string? FrontendBaseUrl() => ImportLinks.TrustedBaseUrl(
        configuration["Frontend:BaseUrl"], Request.Headers.Origin.ToString(), configuration.GetSection("Cors:AllowedOrigins").Get<string[]>() ?? []);
}
