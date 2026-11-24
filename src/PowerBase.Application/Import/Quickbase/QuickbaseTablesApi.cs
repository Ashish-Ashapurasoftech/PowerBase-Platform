using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PowerBase.Application.Common.Interfaces;

namespace PowerBase.Application.Import.Quickbase;

/// <summary>The <c>Quickbase</c> section of appsettings: the realm and API address used to read an app's table ids during import.
/// The user tokens are in meta.QuickbaseUserToken (managed on the hidden admin page), never in config.</summary>
public sealed class QuickbaseImportOptions
{
    public const string SectionName = "Quickbase";

    public string RealmHostname { get; set; } = "ysl.quickbase.com";
    public string ApiBaseUrl { get; set; } = "https://api.quickbase.com/v1";
    public string UserAgent { get; set; } = "PowerBase_Import";
}

/// <summary>A table of a Quickbase app as <c>GET /v1/tables?appId=…</c> returns it.</summary>
public sealed record QuickbaseTable(string Id, string? Alias, string? Name);

public enum QuickbaseLookupStatus
{
    /// <summary>The tables were read.</summary>
    Ok,
    /// <summary>No token (configured or typed) has access to the app (401/403).</summary>
    NoAccess,
    /// <summary>Quickbase does not know the app id (404 with every token).</summary>
    NotFound,
    /// <summary>Quickbase could not be reached, timed out, rate-limited (429) or answered something unexpected - access unknown.</summary>
    Failed,
    /// <summary>No app id to look up, or no token configured.</summary>
    NotTried,
}

public sealed record QuickbaseTablesResult(QuickbaseLookupStatus Status, IReadOnlyList<QuickbaseTable> Tables, string? Message);

/// <summary>Reads the tables (id + alias) of a Quickbase app.</summary>
public interface IQuickbaseTablesApi
{
    /// <summary>With <paramref name="userToken"/> only that token is used. Without it the app's own token (meta.QuickbaseAppCatalog)
    /// is tried first, then every other active token in meta.QuickbaseUserToken, until one has access.</summary>
    Task<QuickbaseTablesResult> GetTablesAsync(string qbAppId, string? userToken, CancellationToken ct = default);
}

public sealed class QuickbaseTablesApi : IQuickbaseTablesApi
{
    private readonly IHttpClientFactory _clients;
    private readonly QuickbaseImportOptions _options;
    private readonly ILogger<QuickbaseTablesApi> _logger;
    private readonly IQuickbaseCatalogRepository _catalog;

    public QuickbaseTablesApi(IHttpClientFactory clients, IOptions<QuickbaseImportOptions> options, ILogger<QuickbaseTablesApi> logger,
        IQuickbaseCatalogRepository catalog)
    {
        _clients = clients;
        _catalog = catalog;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<QuickbaseTablesResult> GetTablesAsync(string qbAppId, string? userToken, CancellationToken ct = default)
    {
        qbAppId = qbAppId?.Trim() ?? "";
        if (qbAppId.Length == 0)
            return new(QuickbaseLookupStatus.NotTried, [], "No Quickbase app id.");

        var tokens = await TokensToTryAsync(qbAppId, userToken, ct);
        if (tokens.Count == 0)
            return new(QuickbaseLookupStatus.NoAccess, [], "No Quickbase user token is set up. Enter a user token that has access to this app.");

        // A token that answers 401/403 has no access; anything else (network error, timeout, 429, 5xx) means we could not tell,
        // so that error is reported instead of "no access" - otherwise one failed call hides behind the other tokens' 401s.
        var sawNoAccess = false;
        string? firstError = null;
        for (var i = 0; i < tokens.Count; i++)
        {
            var label = userToken is not null ? "the typed token" : i == 0 ? "the app's own token" : $"token #{i + 1}";
            var (status, tables, error) = await CallAsync(qbAppId, tokens[i], label, ct);
            if (status == QuickbaseLookupStatus.Ok)
                return new(QuickbaseLookupStatus.Ok, tables, null);
            if (status == QuickbaseLookupStatus.NoAccess)
                sawNoAccess = true;
            else if (status == QuickbaseLookupStatus.Failed)
                firstError ??= error;
            // NotFound: Quickbase also answers 404 to a token that cannot see the app - try the next one
        }

        if (firstError is not null)
            return new(QuickbaseLookupStatus.Failed, [], sawNoAccess
                ? $"{firstError} The other user tokens have no access to this app. Try again, or enter a user token."
                : $"{firstError} Try again, or enter a user token.");
        return sawNoAccess
            ? new(QuickbaseLookupStatus.NoAccess, [], userToken is null
                ? "None of the configured user tokens has access to this Quickbase app. Enter a user token that does."
                : "This user token has no access to this Quickbase app.")
            : new(QuickbaseLookupStatus.NotFound, [], "Quickbase did not find this app id, or no token can see it. Check the app id or enter a user token.");
    }

    /// <summary>One "get tables" call with one token. A 429 (rate limit) is retried once after Quickbase's Retry-After (at most 5 s).</summary>
    private async Task<(QuickbaseLookupStatus Status, List<QuickbaseTable> Tables, string? Error)> CallAsync(
        string qbAppId, string token, string label, CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, $"{_options.ApiBaseUrl.TrimEnd('/')}/tables?appId={Uri.EscapeDataString(qbAppId)}");
                request.Headers.TryAddWithoutValidation("QB-Realm-Hostname", _options.RealmHostname);
                request.Headers.TryAddWithoutValidation("User-Agent", _options.UserAgent);
                request.Headers.TryAddWithoutValidation("Authorization", $"QB-USER-TOKEN {token}");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var client = _clients.CreateClient();
                client.Timeout = TimeSpan.FromSeconds(30);
                using var response = await client.SendAsync(request, ct);

                if (response.IsSuccessStatusCode)
                {
                    await using var body = await response.Content.ReadAsStreamAsync(ct);
                    return (QuickbaseLookupStatus.Ok, await ParseAsync(body, ct), null);
                }

                var code = (int)response.StatusCode;
                var detail = await ReadErrorAsync(response, ct);
                _logger.LogWarning("Quickbase table lookup for app {QbAppId} with {TokenLabel} answered {StatusCode}: {Detail}",
                    qbAppId, label, code, detail);

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    return (QuickbaseLookupStatus.NoAccess, [], null);
                if (response.StatusCode == HttpStatusCode.NotFound)
                    return (QuickbaseLookupStatus.NotFound, [], null);
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt == 1)
                {
                    var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(2);
                    await Task.Delay(wait > TimeSpan.FromSeconds(5) ? TimeSpan.FromSeconds(5) : wait, ct);
                    continue;
                }
                return (QuickbaseLookupStatus.Failed, [], response.StatusCode == HttpStatusCode.TooManyRequests
                    ? $"Quickbase is rate-limiting {label} (429 Too Many Requests)."
                    : $"Quickbase answered {code} {response.ReasonPhrase} for {label}{(detail is null ? "" : $": {detail}")}.");
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
            {
                _logger.LogWarning(ex, "Quickbase table lookup failed for app {QbAppId} with {TokenLabel}", qbAppId, label);
                var reason = ex is TaskCanceledException ? "the request timed out" : (ex.InnerException?.Message ?? ex.Message);
                return (QuickbaseLookupStatus.Failed, [], $"Quickbase could not be reached with {label} ({reason}).");
            }
        }
    }

    /// <summary>Quickbase error bodies look like {"message": "...", "description": "..."}; returns that text, or null.</summary>
    private static async Task<string?> ReadErrorAsync(HttpResponseMessage response, CancellationToken ct)
    {
        try
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            if (string.IsNullOrWhiteSpace(text)) return null;
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return null;
            var msg = doc.RootElement.TryGetProperty("message", out var m) ? m.GetString() : null;
            var desc = doc.RootElement.TryGetProperty("description", out var d) ? d.GetString() : null;
            var joined = string.Join(" - ", new[] { msg, desc }.Where(x => !string.IsNullOrWhiteSpace(x)));
            return joined.Length == 0 ? null : joined.Length > 300 ? joined[..300] : joined;
        }
        catch (Exception ex) when (ex is JsonException or HttpRequestException or InvalidOperationException)
        {
            return null;
        }
    }

    private async Task<List<string>> TokensToTryAsync(string qbAppId, string? userToken, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(userToken))
            return [StripPrefix(userToken)];

        var tokens = (await _catalog.ListTokensAsync(ct)).Where(t => t.IsActive && !string.IsNullOrWhiteSpace(t.Token)).ToList();
        var ownTokenId = (await _catalog.FindAppByQbAppIdAsync(qbAppId, ct))?.UserTokenId;
        return tokens
            .OrderBy(t => t.Id == ownTokenId ? 0 : 1).ThenBy(t => t.SortOrder)
            .Select(t => StripPrefix(t.Token))
            .Distinct(StringComparer.Ordinal).ToList();
    }

    /// <summary>A user may paste "QB-USER-TOKEN b8vz…" — the prefix is added by the request.</summary>
    private static string StripPrefix(string token)
    {
        token = token.Trim();
        const string prefix = "QB-USER-TOKEN";
        return token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ? token[prefix.Length..].Trim() : token;
    }

    private static async Task<List<QuickbaseTable>> ParseAsync(Stream body, CancellationToken ct)
    {
        using var doc = await JsonDocument.ParseAsync(body, cancellationToken: ct);
        var tables = new List<QuickbaseTable>();
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return tables;
        foreach (var t in doc.RootElement.EnumerateArray())
        {
            var id = t.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id)) continue;
            tables.Add(new QuickbaseTable(
                id,
                t.TryGetProperty("alias", out var a) ? a.GetString() : null,
                t.TryGetProperty("name", out var n) ? n.GetString() : null));
        }
        return tables;
    }
}
