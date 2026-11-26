using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Domain.Exceptions;

namespace PowerBase.API.Controllers;

/// <summary>
/// The Quickbase app list and user tokens the app import uses (meta.QuickbaseAppCatalog / meta.QuickbaseUserToken, migration 099).
/// Backs the hidden admin page <c>/admin/quickbase</c> — no menu links to it. Tenant admins (and super admins) only.
/// Token values are never listed in full; they are masked and only replaced when a new value is sent.
/// </summary>
[ApiController]
[Route("admin/quickbase")]
[RequireAuth]
[RequireTenantAdmin]
public class AdminQuickbaseController : ControllerBase
{
    private readonly IQuickbaseCatalogRepository _repo;

    public AdminQuickbaseController(IQuickbaseCatalogRepository repo) => _repo = repo;

    // ---- user tokens ----

    [HttpGet("tokens")]
    public async Task<IActionResult> ListTokens(CancellationToken ct)
    {
        var tokens = await _repo.ListTokensAsync(ct);
        var apps = await _repo.ListAppsAsync(ct);
        return Ok(new ApiResponse<List<QuickbaseTokenDto>>(tokens.Select(t => ToDto(t, apps.Count(a => a.UserTokenId == t.Id))).ToList()));
    }

    [HttpPost("tokens")]
    public async Task<IActionResult> CreateToken([FromBody] QuickbaseTokenRequest request, CancellationToken ct)
    {
        var name = Required(request.Name, "Name");
        var token = Required(NormalizeToken(request.Token), "Token");
        await EnsureUniqueTokenNameAsync(name, null, ct);
        var id = await _repo.CreateTokenAsync(name, token, request.SortOrder ?? 0, request.IsActive ?? true, ct);
        return Ok(new ApiResponse<QuickbaseTokenDto>(ToDto((await _repo.GetTokenAsync(id, ct))!, 0)));
    }

    [HttpPut("tokens/{id:long}")]
    public async Task<IActionResult> UpdateToken(long id, [FromBody] QuickbaseTokenRequest request, CancellationToken ct)
    {
        var existing = await _repo.GetTokenAsync(id, ct) ?? throw new NotFoundException("Quickbase user token", id);
        var name = Required(request.Name, "Name");
        await EnsureUniqueTokenNameAsync(name, id, ct);
        var token = NormalizeToken(request.Token); // empty = keep the stored token
        await _repo.UpdateTokenAsync(id, name, string.IsNullOrEmpty(token) ? null : token,
            request.SortOrder ?? existing.SortOrder, request.IsActive ?? existing.IsActive, ct);
        var apps = await _repo.ListAppsAsync(ct);
        return Ok(new ApiResponse<QuickbaseTokenDto>(ToDto((await _repo.GetTokenAsync(id, ct))!, apps.Count(a => a.UserTokenId == id))));
    }

    /// <summary>Apps that used this token keep their row with no token (every active token is then tried).</summary>
    [HttpDelete("tokens/{id:long}")]
    public async Task<IActionResult> DeleteToken(long id, CancellationToken ct)
    {
        if (!await _repo.DeleteTokenAsync(id, ct))
            throw new NotFoundException("Quickbase user token", id);
        return NoContent();
    }

    // ---- apps ----

    [HttpGet("apps")]
    public async Task<IActionResult> ListApps(CancellationToken ct) =>
        Ok(new ApiResponse<IReadOnlyList<QuickbaseCatalogAppRow>>(await _repo.ListAppsAsync(ct)));

    [HttpPost("apps")]
    public async Task<IActionResult> CreateApp([FromBody] QuickbaseAppRequest request, CancellationToken ct)
    {
        var (qbAppId, logicalId, solution, tokenId) = await ValidateAppAsync(request, null, ct);
        var id = await _repo.CreateAppAsync(qbAppId, logicalId, solution, tokenId, ct);
        return Ok(new ApiResponse<QuickbaseCatalogAppRow>((await _repo.GetAppAsync(id, ct))!));
    }

    [HttpPut("apps/{id:long}")]
    public async Task<IActionResult> UpdateApp(long id, [FromBody] QuickbaseAppRequest request, CancellationToken ct)
    {
        _ = await _repo.GetAppAsync(id, ct) ?? throw new NotFoundException("Quickbase app", id);
        var (qbAppId, logicalId, solution, tokenId) = await ValidateAppAsync(request, id, ct);
        await _repo.UpdateAppAsync(id, qbAppId, logicalId, solution, tokenId, ct);
        return Ok(new ApiResponse<QuickbaseCatalogAppRow>((await _repo.GetAppAsync(id, ct))!));
    }

    [HttpDelete("apps/{id:long}")]
    public async Task<IActionResult> DeleteApp(long id, CancellationToken ct)
    {
        if (!await _repo.DeleteAppAsync(id, ct))
            throw new NotFoundException("Quickbase app", id);
        return NoContent();
    }

    // ---- helpers ----

    private async Task<(string QbAppId, string LogicalId, string? Solution, long? TokenId)> ValidateAppAsync(
        QuickbaseAppRequest request, long? id, CancellationToken ct)
    {
        var qbAppId = Required(request.QbAppId, "QbAppId");
        var logicalId = Required(request.LogicalId, "LogicalId");
        if (qbAppId.Length > 20)
            throw new ValidationException(new Dictionary<string, string[]> { ["QbAppId"] = ["A Quickbase app id is at most 20 characters."] });
        if (qbAppId.Any(char.IsWhiteSpace))
            throw new ValidationException(new Dictionary<string, string[]> { ["QbAppId"] = ["A Quickbase app id has no spaces."] });
        if (!logicalId.StartsWith('$'))
            throw new ValidationException(new Dictionary<string, string[]> { ["LogicalId"] = ["The logical id is the app's key in the QBL file and starts with '$' (e.g. $App_HIH_Archive)."] });
        if (request.UserTokenId is long tokenId && await _repo.GetTokenAsync(tokenId, ct) is null)
            throw new ValidationException(new Dictionary<string, string[]> { ["UserTokenId"] = ["This user token does not exist."] });

        var apps = await _repo.ListAppsAsync(ct);
        if (apps.Any(a => a.Id != id && string.Equals(a.QbAppId, qbAppId, StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException($"The Quickbase app id '{qbAppId}' is already in the list.");
        if (apps.Any(a => a.Id != id && string.Equals(a.LogicalId, logicalId, StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException($"The logical id '{logicalId}' is already in the list.");

        var solution = string.IsNullOrWhiteSpace(request.Solution) ? null : request.Solution.Trim();
        return (qbAppId, logicalId, solution, request.UserTokenId);
    }

    private async Task EnsureUniqueTokenNameAsync(string name, long? id, CancellationToken ct)
    {
        if ((await _repo.ListTokensAsync(ct)).Any(t => t.Id != id && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            throw new ConflictException($"A user token named '{name}' already exists.");
    }

    private static string Required(string? value, string field)
    {
        var v = value?.Trim();
        if (string.IsNullOrEmpty(v))
            throw new ValidationException(new Dictionary<string, string[]> { [field] = [$"{field} is required."] });
        return v;
    }

    /// <summary>Accepts the token with or without the "QB-USER-TOKEN " prefix; stores the bare token.</summary>
    private static string NormalizeToken(string? token)
    {
        var t = token?.Trim() ?? "";
        const string prefix = "QB-USER-TOKEN";
        return t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? t[prefix.Length..].Trim() : t;
    }

    private static QuickbaseTokenDto ToDto(QuickbaseUserTokenRow t, int appCount) => new()
    {
        Id = t.Id,
        Name = t.Name,
        TokenMasked = Mask(t.Token),
        SortOrder = t.SortOrder,
        IsActive = t.IsActive,
        AppCount = appCount,
        CreatedOn = t.CreatedOn,
        ModifiedOn = t.ModifiedOn,
    };

    private static string Mask(string token) =>
        token.Length <= 10 ? new string('•', token.Length) : $"{token[..6]}…{token[^4..]}";
}

public sealed class QuickbaseTokenDto
{
    public long Id { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>First 6 and last 4 characters only; the full token never leaves the API.</summary>
    public string TokenMasked { get; set; } = string.Empty;
    public int SortOrder { get; set; }
    public bool IsActive { get; set; }
    /// <summary>Apps in the list assigned to this token.</summary>
    public int AppCount { get; set; }
    public DateTime CreatedOn { get; set; }
    public DateTime? ModifiedOn { get; set; }
}

public sealed class QuickbaseTokenRequest
{
    public string? Name { get; set; }
    /// <summary>Required on create; on update empty = keep the stored token.</summary>
    public string? Token { get; set; }
    public int? SortOrder { get; set; }
    public bool? IsActive { get; set; }
}

public sealed class QuickbaseAppRequest
{
    public string? QbAppId { get; set; }
    public string? LogicalId { get; set; }
    public string? Solution { get; set; }
    public long? UserTokenId { get; set; }
}
