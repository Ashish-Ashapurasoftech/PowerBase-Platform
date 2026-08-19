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
    public async Task<IActionResult> SearchGlobal([FromQuery] string query, [FromQuery] long? appId = null, CancellationToken ct = default)
    {
        var result = await _searchHandler.HandleAsync(new SearchGlobalRecordsQuery(query, appId), ct);
        return Ok(new ApiResponse<SearchGlobalRecordsResult>(result));
    }
}
