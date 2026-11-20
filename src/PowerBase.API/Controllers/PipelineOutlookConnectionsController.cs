using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Mvc;
using PowerBase.API.Attributes;
using PowerBase.API.Models;
using PowerBase.API.Pipelines;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines.Outlook;
using PowerBase.Domain.Entities;

namespace PowerBase.API.Controllers;

/// <summary>
/// Microsoft Outlook accounts for the PowerFlows "Send an Email" step. Accounts belong to the signed-in user and
/// are listed in every pipeline (Quickbase parity). Tokens are never returned to the browser.
/// </summary>
[ApiController]
[Route("pipelines/outlook-connections")]
public sealed class PipelineOutlookConnectionsController : ControllerBase
{
    private const string CALLBACK_PATH = "/pipelines/outlook-connections/oauth/callback";
    private readonly IMicrosoftConnectionRepository _repository;
    private readonly OutlookConnectionService _outlook;
    private readonly IQueryContext _identity;
    private readonly IConfiguration _config;
    private readonly IDataProtector _state;
    private readonly ILogger<PipelineOutlookConnectionsController> _logger;

    public PipelineOutlookConnectionsController(IMicrosoftConnectionRepository repository, OutlookConnectionService outlook, IQueryContext identity,
        IConfiguration config, IDataProtectionProvider protection, ILogger<PipelineOutlookConnectionsController> logger)
    {
        _repository = repository; _outlook = outlook; _identity = identity; _config = config; _logger = logger;
        _state = protection.CreateProtector("PowerBase.MicrosoftOAuthState.v1");
    }

    public sealed record OutlookConnectionDto(Guid ConnectionId, string DisplayName, string Email, string Status);
    public sealed record OutlookPermissionDto(string Scope, string Title, string Description, bool Required);
    public sealed record AuthorizeRequest(List<string>? OptionalScopes);
    internal sealed record State(long TenantId, long UserId, string Verifier, List<string> Scopes, string RedirectUri, DateTimeOffset ExpiresAt);

    /// <summary>Connected Microsoft Outlook accounts of the signed-in user.</summary>
    [HttpGet] [RequireAuth]
    public async Task<IActionResult> List(CancellationToken ct)
    {
        var items = (await _repository.ListForUserAsync(_identity.UserId, MicrosoftConnectionProviders.Outlook, ct))
            .Select(row => new OutlookConnectionDto(row.PublicId, row.DisplayName, row.AccountEmail, row.Status)).ToList();
        return Ok(new ApiListResponse<OutlookConnectionDto>(items, items.Count, 1, items.Count));
    }

    /// <summary>Permissions shown in the "Connect new account" panel.</summary>
    [HttpGet("permissions")] [RequireAuth]
    public IActionResult Permissions() => Ok(new
    {
        configured = _outlook.IsConfigured,
        permissions = OutlookPermissionCatalog.All.Select(p => new OutlookPermissionDto(p.Scope, p.Title, p.Description, p.Required))
    });

    /// <summary>Starts Microsoft sign-in. Open the returned URL in a popup.</summary>
    [HttpPost("authorize")] [RequireAuth]
    public IActionResult Authorize(AuthorizeRequest input)
    {
        if (!_outlook.IsConfigured) return BadRequest(new { error = "Microsoft Outlook is not configured on this server." });
        IReadOnlyList<string> scopes;
        try { scopes = OutlookPermissionCatalog.Resolve(input.OptionalScopes); }
        catch (ArgumentException ex) { return BadRequest(new { error = ex.Message }); }
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(32));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        var redirectUri = _config["Microsoft:RedirectUri"] is { Length: > 0 } configured ? configured : $"{Request.Scheme}://{Request.Host}{Request.PathBase}{CALLBACK_PATH}";
        var state = _state.Protect(JsonSerializer.Serialize(new State(_identity.TenantId, _identity.UserId, verifier, scopes.ToList(), redirectUri, DateTimeOffset.UtcNow.AddMinutes(10))));
        return Ok(new { authorizationUrl = _outlook.BuildAuthorizationUrl(redirectUri, state, challenge, scopes) });
    }

    /// <summary>Microsoft redirects here after sign-in. Stores the account and closes the popup.</summary>
    [HttpGet("oauth/callback")]
    public async Task<IActionResult> Callback(string? state, string? code, string? error, [FromQuery(Name = "error_description")] string? errorDescription, CancellationToken ct)
    {
        State ticket;
        try { ticket = JsonSerializer.Deserialize<State>(_state.Unprotect(state ?? ""))!; }
        catch { return Page(false, "Invalid sign-in state. Close this window and connect again.", null); }
        if (ticket.ExpiresAt < DateTimeOffset.UtcNow) return Page(false, "Sign-in expired. Close this window and connect again.", null);
        if (!string.IsNullOrEmpty(error) || string.IsNullOrEmpty(code))
            return Page(false, "Microsoft sign-in was cancelled or denied" + (string.IsNullOrEmpty(errorDescription) ? "." : ": " + errorDescription.Split('\n')[0]), null);
        try
        {
            _identity.SetTenantId(ticket.TenantId);
            _identity.SetUserIdentity(ticket.UserId, false, "", "", new HashSet<string>(), "");
            var token = await _outlook.RedeemCodeAsync(code, ticket.Verifier, ticket.RedirectUri, ticket.Scopes, ct);
            var profile = await _outlook.GetProfileAsync(token.AccessToken, ct);
            var row = await _outlook.UpsertAsync(ticket.UserId, profile, token, ct);
            return Page(true, $"Connected {row.DisplayName} ({row.AccountEmail}). You can close this window.", row.PublicId);
        }
        catch (Exception ex) when (ex is InvalidOperationException or MicrosoftAuthorizationException or HttpRequestException)
        {
            _logger.LogWarning(ex, "Microsoft Outlook connection failed.");
            return Page(false, ex.Message, null);
        }
    }

    /// <summary>Disconnects (soft-deletes) an account and erases its tokens.</summary>
    [HttpDelete("{id:guid}")] [RequireAuth]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        var row = await _repository.GetByPublicIdForUserAsync(id, _identity.UserId, ct);
        if (row == null) return NotFound();
        await _repository.SoftDeleteAsync(row, ct);
        return NoContent();
    }

    private ContentResult Page(bool ok, string message, Guid? connectionId)
    {
        var payload = JsonSerializer.Serialize(new { source = "powerbase-outlook", status = ok ? "connected" : "error", connectionId, message });
        // JSON inside <script>: neutralise "</script>" and HTML-significant characters.
        var script = payload.Replace("<", "\\u003c").Replace(">", "\\u003e").Replace("&", "\\u0026");
        var html = $"<!doctype html><html><head><meta charset=\"utf-8\"><title>Microsoft Outlook</title></head>" +
                   $"<body style=\"font-family:sans-serif;padding:24px\"><p>{WebUtility.HtmlEncode(message)}</p>" +
                   $"<script>try{{if(window.opener){{window.opener.postMessage({script},'*');}}}}catch(e){{}}" +
                   (ok ? "setTimeout(function(){window.close();},800);" : "") + "</script></body></html>";
        return new ContentResult { Content = html, ContentType = "text/html; charset=utf-8", StatusCode = ok ? 200 : 400 };
    }

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
