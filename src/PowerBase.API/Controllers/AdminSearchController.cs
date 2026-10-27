using PowerBase.API.Models;
using PowerBase.API.Attributes;
using Microsoft.AspNetCore.Mvc;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("admin/search")]
public class AdminSearchController : ControllerBase
{
    private readonly Microsoft.Extensions.DependencyInjection.IServiceScopeFactory _scopeFactory;
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, SearchBackfillStatus> _backfillJobs = new();

    public AdminSearchController(Microsoft.Extensions.DependencyInjection.IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    /// <summary>Triggers a background job to backfill records into Azure AI Search for the current tenant, optionally scoped to a table.</summary>
    [HttpPost("backfill")]
    [RequireSuperAdmin] // Requires SuperAdmin
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public IActionResult BackfillSearchIndex([FromQuery] long? tableId = null, [FromQuery] long? tenantId = null)
    {
        var queryContext = HttpContext.RequestServices.GetRequiredService<PowerBase.Application.Common.Interfaces.IQueryContext>();
        var effectiveTenantId = tenantId.HasValue && tenantId.Value > 0 ? tenantId.Value : queryContext.TenantId;

        var job = new SearchBackfillStatus
        {
            TenantId = effectiveTenantId,
            TableId = tableId,
            StartedAtUtc = DateTime.UtcNow,
            Status = "Running"
        };
        _backfillJobs[job.JobId] = job;
        
        _ = Task.Run(async () =>
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var handler = scope.ServiceProvider.GetRequiredService<Application.Search.Commands.BackfillSearchIndex.BackfillSearchIndexCommandHandler>();
                var request = new Application.Search.Commands.BackfillSearchIndex.BackfillSearchIndexCommand(
                    effectiveTenantId,
                    (apps, tables, indexed, failed, err) =>
                    {
                        job.TotalApps = apps;
                        job.TotalTables = tables;
                        job.TotalRecordsIndexed = indexed;
                        job.FailedRecordsCount = failed;
                        if (!string.IsNullOrEmpty(err)) job.ErrorMessage = err;
                    },
                    tableId);

                await handler.HandleAsync(request);
                job.Status = "Completed";
                job.CompletedAtUtc = DateTime.UtcNow;
            }
            catch (Exception ex)
            {
                job.Status = "Failed";
                job.ErrorMessage = ex.Message;
                job.CompletedAtUtc = DateTime.UtcNow;
                Console.WriteLine($"Error during search backfill (Job: {job.JobId}): {ex}");
            }
        });

        return Accepted(new ApiResponse<SearchBackfillStatus>(job));
    }

    /// <summary>Gets the status of a specific backfill job or recent backfill jobs for the current tenant.</summary>
    [HttpGet("backfill/status")]
    [RequireSuperAdmin]
    [ProducesResponseType(StatusCodes.Status200OK)]
    public IActionResult GetBackfillStatus([FromQuery] string? jobId = null)
    {
        var queryContext = HttpContext.RequestServices.GetRequiredService<PowerBase.Application.Common.Interfaces.IQueryContext>();
        var tenantId = queryContext.TenantId;

        if (!string.IsNullOrEmpty(jobId))
        {
            if (_backfillJobs.TryGetValue(jobId, out var job) && (job.TenantId == tenantId || queryContext.IsSuperAdmin))
            {
                return Ok(new ApiResponse<SearchBackfillStatus>(job));
            }
            return NotFound(new ApiResponse<string>("Backfill job not found."));
        }

        var tenantJobs = _backfillJobs.Values
            .Where(j => j.TenantId == tenantId)
            .OrderByDescending(j => j.StartedAtUtc)
            .Take(10)
            .ToList();

        return Ok(new ApiResponse<IReadOnlyList<SearchBackfillStatus>>(tenantJobs));
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

    /// <summary>Creates a new Azure AI Search index with the same configured name (or specified name) using the new typed slot schema. Safe for deployments: if the index already exists, returns 409 Conflict so your existing index is not overwritten unless force=true.</summary>
    [HttpPost("create-index")]
    [RequireSuperAdmin]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> CreateIndex([FromQuery] string? indexName = null, [FromQuery] bool force = false, [FromQuery] string? confirm = null)
    {
        var searchService = HttpContext.RequestServices.GetRequiredService<PowerBase.Application.Common.Interfaces.IAzureSearchService>();
        var configuration = HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        var targetName = !string.IsNullOrWhiteSpace(indexName) 
            ? indexName 
            : configuration["AzureAiSearch:IndexName"] ?? "powerbase-records-index";

        // force=true drops the existing index first: only when the deployment allows it and the caller repeats the index name.
        if (force)
        {
            var refused = RefuseDropping(configuration, targetName, confirm);
            if (refused != null) return refused;
        }

        var created = await searchService.CreateIndexAsync(indexName, force);
        if (!created)
        {
            return Conflict(new ApiResponse<string>($"Index '{targetName}' already exists. Please rename or backup your old index first, or pass force=true if you want to overwrite it."));
        }
        return Ok(new ApiResponse<string>($"Search index '{targetName}' created successfully with the new typed slot schema."));
    }

    /// <summary>Deletes and recreates the Azure AI Search index with the new typed slot schema. Everything in it is lost until it is reindexed, so it needs AzureAiSearch:AllowDestructiveAdmin=true and confirm=&lt;index name&gt;.</summary>
    [HttpPost("reset-index")]
    [RequireSuperAdmin]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> ResetIndex([FromQuery] string? indexName = null, [FromQuery] string? confirm = null)
    {
        var searchService = HttpContext.RequestServices.GetRequiredService<PowerBase.Application.Common.Interfaces.IAzureSearchService>();
        var configuration = HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();
        var targetName = !string.IsNullOrWhiteSpace(indexName)
            ? indexName
            : configuration["AzureAiSearch:IndexName"] ?? "powerbase-records-index";

        // Dropping the live index empties search until it is reindexed: needs AllowDestructiveAdmin and the index name repeated in "confirm".
        var refused = RefuseDropping(configuration, targetName, confirm);
        if (refused != null) return refused;

        await searchService.ResetIndexAsync(indexName);
        return Ok(new ApiResponse<string>("Search index reset and recreated successfully with the latest typed slot schema."));
    }

    /// <summary>
    /// Removes every search document of the current tenant (shared index and any table-owned index), for offboarding. The database is not touched.
    /// Needs AzureAiSearch:AllowDestructiveAdmin=true and confirm=&lt;the tenant id&gt;.
    /// </summary>
    [HttpPost("purge-tenant")]
    [RequireSuperAdmin]
    [ProducesResponseType(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status403Forbidden)]
    public async Task<IActionResult> PurgeTenant([FromQuery] string? confirm = null)
    {
        var services = HttpContext.RequestServices;
        var tenantId = services.GetRequiredService<PowerBase.Application.Common.Interfaces.IQueryContext>().TenantId;
        var configuration = services.GetRequiredService<Microsoft.Extensions.Configuration.IConfiguration>();

        var refused = RefuseDropping(configuration, tenantId.ToString(), confirm);
        if (refused != null) return refused;

        await services.GetRequiredService<PowerBase.Application.Common.Interfaces.IAzureSearchService>().PurgeTenantRecordsAsync(tenantId);
        return Ok(new ApiResponse<string>($"All search documents of tenant {tenantId} were removed. Run the backfill to rebuild them."));
    }

    /// <summary>
    /// Refuses to drop an index unless the deployment allows it and the caller named the index. Returns the response to send, or null to proceed.
    /// Logs every accepted request: dropping the live index empties search until it is reindexed.
    /// </summary>
    private IActionResult? RefuseDropping(Microsoft.Extensions.Configuration.IConfiguration configuration, string targetName, string? confirm)
    {
        var allowed = bool.TryParse(configuration["AzureAiSearch:AllowDestructiveAdmin"], out var a) && a;
        var refusal = PowerBase.Application.Search.SearchIndexSafety.RefuseDestructive(targetName, confirm, allowed);
        if (refusal != null)
            return allowed
                ? BadRequest(new ApiResponse<string>(refusal))
                : StatusCode(StatusCodes.Status403Forbidden, new ApiResponse<string>(refusal));

        HttpContext.RequestServices.GetRequiredService<Microsoft.Extensions.Logging.ILogger<AdminSearchController>>()
            .LogWarning("Search index {Index} is being dropped by an administrator request.", targetName);
        return null;
    }
}

public class SearchBackfillStatus
{
    public string JobId { get; set; } = Guid.NewGuid().ToString("N");
    public long TenantId { get; set; }
    public long? TableId { get; set; }
    public string Status { get; set; } = "Running";
    public DateTime StartedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? CompletedAtUtc { get; set; }
    public int TotalApps { get; set; }
    public int TotalTables { get; set; }
    public int TotalRecordsIndexed { get; set; }
    public int FailedRecordsCount { get; set; }
    public string? ErrorMessage { get; set; }
}
