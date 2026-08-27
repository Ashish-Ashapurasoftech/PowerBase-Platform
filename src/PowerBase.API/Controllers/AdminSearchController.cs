using PowerBase.API.Models;
using PowerBase.API.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("admin/search")]
public class AdminSearchController : ControllerBase
{
    private readonly Microsoft.Extensions.DependencyInjection.IServiceScopeFactory _scopeFactory;

    public AdminSearchController(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>Triggers a background job to backfill all records into Azure AI Search for the current tenant.</summary>
    [HttpPost("backfill")]
    [RequireSuperAdmin] // Requires SuperAdmin
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult BackfillSearchIndex()
    {
        var queryContext = HttpContext.RequestServices.GetRequiredService<PowerBase.Application.Common.Interfaces.IQueryContext>();
        var tenantId = queryContext.TenantId;
        
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<Application.Search.Commands.BackfillSearchIndex.BackfillSearchIndexCommandHandler>();
                var request = new Application.Search.Commands.BackfillSearchIndex.BackfillSearchIndexCommand(tenantId);
                await handler.HandleAsync(request);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during search backfill: {ex}");
            }
        });

        return Accepted(new ApiResponse<string>("Backfill job started."));
    }

    /// <summary>Triggers a background job to identify and encrypt legacy plaintext data in encrypted apps, and re-indexes them.</summary>
    [HttpPost("sanitize")]
    [RequireSuperAdmin] // Requires SuperAdmin
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult SanitizeEncryptedData()
    {
        var queryContext = HttpContext.RequestServices.GetRequiredService<PowerBase.Application.Common.Interfaces.IQueryContext>();
        var tenantId = queryContext.TenantId;
        
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<Application.Search.Commands.SanitizeEncryptedData.SanitizeEncryptedDataCommandHandler>();
                var request = new Application.Search.Commands.SanitizeEncryptedData.SanitizeEncryptedDataCommand(tenantId);
                await handler.HandleAsync(request);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during encrypted data sanitization: {ex}");
            }
        });

        return Accepted(new ApiResponse<string>("Sanitization job started."));
    }
}
