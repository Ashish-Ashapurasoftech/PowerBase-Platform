using PowerBase.API.Models;
using PowerBase.API.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("admin/search")]
public class AdminSearchController : ControllerBase
{
    public AdminSearchController()
    {
    }

    /// <summary>Triggers a background job to backfill all records into Azure AI Search for the current tenant.</summary>
    [HttpPost("backfill")]
    [RequireSuperAdmin] // Requires SuperAdmin
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult BackfillSearchIndex()
    {
        // Fire and forget to not block the HTTP request
        var queryContext = HttpContext.RequestServices.GetRequiredService<PowerBase.Application.Common.Interfaces.IQueryContext>();
        var tenantId = queryContext.TenantId;
        
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = HttpContext.RequestServices.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<Application.Search.Commands.BackfillSearchIndex.BackfillSearchIndexCommandHandler>();
                var request = new Application.Search.Commands.BackfillSearchIndex.BackfillSearchIndexCommand(tenantId);
                await handler.HandleAsync(request);
            }
            catch (Exception ex)
            {
                // In production, log this exception to Application Insights / Serilog
                Console.WriteLine($"Error during search backfill: {ex.Message}");
            }
        });

        return Accepted(new ApiResponse<string>("Backfill job started."));
    }
}
