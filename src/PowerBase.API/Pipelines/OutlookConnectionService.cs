using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines.Outlook;
using PowerBase.Domain.Entities;

namespace PowerBase.API.Pipelines;

/// <summary>
/// Microsoft OAuth (authorization code + PKCE, "common" authority so work and personal accounts both work —
/// the same app registration and flow as the legacy QuickbaseSharePoint token page, but the code exchange
/// happens here, server-side, and tokens are encrypted at rest).
/// </summary>
public sealed class OutlookConnectionService : IOutlookConnectionService
{
    public const string HTTP_CLIENT_NAME = "MicrosoftIdentity";
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();

    private readonly IMicrosoftConnectionRepository _repository;
    private readonly IOutlookGraphMailSender _sender;
    private readonly IHttpClientFactory _clients;
    private readonly IConfiguration _config;
    private readonly IDataProtector _protector;
    private readonly ILogger<OutlookConnectionService> _logger;

    public OutlookConnectionService(IMicrosoftConnectionRepository repository, IOutlookGraphMailSender sender, IHttpClientFactory clients,
        IConfiguration config, IDataProtectionProvider protection, ILogger<OutlookConnectionService> logger)
    {
        _repository = repository; _sender = sender; _clients = clients; _config = config; _logger = logger;
        _protector = protection.CreateProtector("PowerBase.MicrosoftConnectionTokens.v1");
    }

    public sealed record TokenResult(string AccessToken, string? RefreshToken, DateTimeOffset ExpiresAt, string? Scope);
    public sealed record Profile(string Id, string DisplayName, string Email);

    public string ClientId => _config["Microsoft:ClientId"] ?? "";
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_config["Microsoft:ClientId"]) && !string.IsNullOrWhiteSpace(_config["Microsoft:ClientSecret"]);
    private string Authority => (_config["Microsoft:AuthorityUrl"] ?? "https://login.microsoftonline.com/common/oauth2/v2.0").TrimEnd('/');
    private string GraphBaseUrl => (_config["Microsoft:GraphBaseUrl"] ?? "https://graph.microsoft.com/v1.0").TrimEnd('/');

    public string BuildAuthorizationUrl(string redirectUri, string state, string codeChallenge, IEnumerable<string> scopes)
    {
        var parameters = new Dictionary<string, string>
        {
            ["client_id"] = ClientId, ["response_type"] = "code", ["redirect_uri"] = redirectUri, ["response_mode"] = "query",
            ["scope"] = string.Join(' ', scopes), ["state"] = state, ["code_challenge"] = codeChallenge,
            ["code_challenge_method"] = "S256", ["prompt"] = "select_account"
        };
        return Authority + "/authorize?" + string.Join("&", parameters.Select(p => Uri.EscapeDataString(p.Key) + "=" + Uri.EscapeDataString(p.Value)));
    }

    public string Protect(string value) => _protector.Protect(value);
    public string Unprotect(string value) => _protector.Unprotect(value);

    public Task<TokenResult> RedeemCodeAsync(string code, string verifier, string redirectUri, IEnumerable<string> scopes, CancellationToken ct) =>
        TokenAsync(new() { ["grant_type"] = "authorization_code", ["code"] = code, ["redirect_uri"] = redirectUri, ["code_verifier"] = verifier, ["scope"] = string.Join(' ', scopes) }, ct);

    public async Task<Profile> GetProfileAsync(string accessToken, CancellationToken ct)
    {
        using var client = _clients.CreateClient(HTTP_CLIENT_NAME);
        using var request = new HttpRequestMessage(HttpMethod.Get, GraphBaseUrl + "/me?$select=id,displayName,mail,userPrincipalName");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        using var response = await client.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new InvalidOperationException("Could not read the Microsoft account profile.");
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var root = json.RootElement;
        string? Read(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        var id = Read("id") ?? throw new InvalidOperationException("The Microsoft account profile has no id.");
        var email = Read("mail") ?? Read("userPrincipalName") ?? "";
        return new Profile(id, Read("displayName") ?? email, email);
    }

    /// <summary>Creates the account row, or updates it when the same Microsoft account is connected again.</summary>
    public async Task<MicrosoftConnection> UpsertAsync(long userId, Profile profile, TokenResult token, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(token.RefreshToken)) throw new InvalidOperationException("Microsoft did not return a refresh token. Make sure offline access is granted.");
        var existing = await _repository.GetByMicrosoftUserAsync(userId, MicrosoftConnectionProviders.Outlook, profile.Id, ct);
        var row = existing ?? new MicrosoftConnection { CreatedByUserId = userId, Provider = MicrosoftConnectionProviders.Outlook, MicrosoftUserId = profile.Id };
        row.AccountEmail = Truncate(profile.Email, 320);
        row.DisplayName = Truncate(profile.DisplayName, 200);
        row.Scopes = Truncate(token.Scope ?? "", 2000);
        row.EncryptedRefreshToken = Protect(token.RefreshToken);
        row.EncryptedAccessToken = Protect(token.AccessToken);
        row.AccessTokenExpiresAt = token.ExpiresAt;
        row.Status = MicrosoftConnectionStatuses.Connected;
        row.LastError = null;
        return existing == null ? await _repository.CreateAsync(row, ct) : await _repository.UpdateAsync(row, ct);
    }

    public async Task SendAsync(Guid connectionId, long ownerUserId, PipelineEmailMessage message, CancellationToken ct)
    {
        var accessToken = await GetAccessTokenAsync(connectionId, ownerUserId, ct);
        await _sender.SendAsync(accessToken, message, ct);
    }

    public async Task<string> GetAccessTokenAsync(Guid connectionId, long ownerUserId, CancellationToken ct)
    {
        var gate = Locks.GetOrAdd(connectionId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(ct);
        try
        {
            var row = await _repository.GetByPublicIdForUserAsync(connectionId, ownerUserId, ct)
                ?? throw new UnauthorizedAccessException("The selected Microsoft Outlook account is not available to this PowerFlow owner. Select or connect an account in the Send an Email step.");
            if (row.Status == MicrosoftConnectionStatuses.ReconnectRequired)
                throw new InvalidOperationException($"The Microsoft Outlook account {row.AccountEmail} must be reconnected.");
            if (row.EncryptedAccessToken != null && row.AccessTokenExpiresAt > DateTimeOffset.UtcNow.AddMinutes(2))
            {
                row.LastUsedAt = DateTime.UtcNow;
                return Unprotect(row.EncryptedAccessToken);
            }
            TokenResult token;
            try
            {
                token = await TokenAsync(new() { ["grant_type"] = "refresh_token", ["refresh_token"] = Unprotect(row.EncryptedRefreshToken) }, ct);
            }
            catch (MicrosoftAuthorizationException ex)
            {
                row.Status = MicrosoftConnectionStatuses.ReconnectRequired;
                row.LastError = Truncate(ex.Message, 1000);
                row.EncryptedAccessToken = null;
                await _repository.UpdateAsync(row, ct);
                throw new InvalidOperationException($"The Microsoft Outlook account {row.AccountEmail} must be reconnected: {ex.Message}");
            }
            // Microsoft rotates refresh tokens; always store the newest one.
            if (!string.IsNullOrEmpty(token.RefreshToken)) row.EncryptedRefreshToken = Protect(token.RefreshToken);
            row.EncryptedAccessToken = Protect(token.AccessToken);
            row.AccessTokenExpiresAt = token.ExpiresAt;
            row.LastUsedAt = DateTime.UtcNow;
            row.LastError = null;
            await _repository.UpdateAsync(row, ct);
            return token.AccessToken;
        }
        finally { gate.Release(); }
    }

    private async Task<TokenResult> TokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        if (!IsConfigured) throw new InvalidOperationException("Microsoft Outlook is not configured on this server (Microsoft:ClientId / Microsoft:ClientSecret).");
        form["client_id"] = ClientId;
        form["client_secret"] = _config["Microsoft:ClientSecret"]!;
        using var client = _clients.CreateClient(HTTP_CLIENT_NAME);
        using var response = await client.PostAsync(Authority + "/token", new FormUrlEncodedContent(form), ct);
        var text = await response.Content.ReadAsStringAsync(ct);
        JsonDocument json;
        try { json = JsonDocument.Parse(text); }
        catch (JsonException) { throw new InvalidOperationException($"Microsoft token endpoint returned HTTP {(int)response.StatusCode}."); }
        using (json)
        {
            var root = json.RootElement;
            string? Read(string name) => root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
            if (!response.IsSuccessStatusCode || Read("access_token") is not { } accessToken)
            {
                var error = Read("error") ?? "token_error";
                var description = (Read("error_description") ?? "").Split('\n')[0].Trim();
                _logger.LogWarning("Microsoft token request failed: {Error}", error);
                if (error is "invalid_grant" or "interaction_required" or "consent_required")
                    throw new MicrosoftAuthorizationException(string.IsNullOrEmpty(description) ? error : description);
                throw new InvalidOperationException("Microsoft sign-in failed: " + error + (description.Length > 0 ? " - " + description : ""));
            }
            var expiresIn = root.TryGetProperty("expires_in", out var e) && e.TryGetInt32(out var seconds) ? seconds : 3600;
            return new TokenResult(accessToken, Read("refresh_token"), DateTimeOffset.UtcNow.AddSeconds(expiresIn), Read("scope"));
        }
    }

    private static string Truncate(string value, int max) => value.Length <= max ? value : value[..max];
}

public sealed class MicrosoftAuthorizationException(string message) : Exception(message);
