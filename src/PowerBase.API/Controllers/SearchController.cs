using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.Application.Records.Queries.SearchGlobalRecords;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("search")]
public class SearchController : ControllerBase
{
    private readonly SearchGlobalRecordsQueryHandler _searchHandler;

    public SearchController(SearchGlobalRecordsQueryHandler searchHandler)
    {
        _searchHandler = searchHandler;
    }

    /// <summary>Search records globally across the tenant.</summary>
    [HttpGet]
    [RequireAuth]
    [ProducesResponseType(typeof(ApiResponse<SearchGlobalRecordsResult>), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status401Unauthorized)]
    public async Task<IActionResult> SearchGlobal([FromQuery] string query, [FromServices] IAppRepository appRepo, [FromQuery] Guid? appId = null, CancellationToken ct = default)
    {
        long? internalAppId = null;
        if (appId.HasValue)
        {
            var app = await appRepo.GetByPublicIdAsync(appId.Value, ct);
            internalAppId = app.Id;
        }
        var result = await _searchHandler.HandleAsync(new SearchGlobalRecordsQuery(query, internalAppId), ct);
        return Ok(new ApiResponse<SearchGlobalRecordsResult>(result));
    }
}
