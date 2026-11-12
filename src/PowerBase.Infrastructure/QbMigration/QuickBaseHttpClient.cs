using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using PowerBase.Application.QbMigration;

namespace PowerBase.Infrastructure.QbMigration;

public sealed record QuickBaseClientOptions
{
    public string RealmHostname { get; init; } = "";
    /// <summary>The QuickBase user token. Comes from Key Vault in Azure, user-secrets locally; never from appsettings.</summary>
    public string UserToken { get; init; } = "";
    public string BaseUrl { get; init; } = "https://api.quickbase.com/v1/";
    public double RequestsPerSecond { get; init; } = 8;
    /// <summary>QuickBase recommends 15–90 s for data integrations.</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(60);
    public int MaxRateLimitRetries { get; init; } = 10;
    public int MaxServerErrorRetries { get; init; } = 5;
}

/// <summary>QuickBase took too long twice in a row; the caller halves the range (QuickBase's own advice).</summary>
public sealed class QuickBaseTimeoutException(string message) : Exception(message);

/// <summary>QuickBase RESTful API client following QuickBase's rate-limit guidance: one shared rate limiter, honour
/// retry-after on 429, exponential backoff (1, 2, 4, 8, 16 s) on 5xx and connection errors, on a timeout wait 15 s
/// and retry once, never retry another 4xx. Every 429/5xx is logged with cf-ray, tid and qb-api-ray for support.</summary>
public sealed class QuickBaseHttpClient : IQuickBaseClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly HttpClient _http;
    private readonly QuickBaseClientOptions _options;
    private readonly QbRateLimiter _limiter;
    private readonly ILogger<QuickBaseHttpClient> _logger;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;

    public QuickBaseHttpClient(HttpClient http, QuickBaseClientOptions options, QbRateLimiter limiter, ILogger<QuickBaseHttpClient> logger,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        if (string.IsNullOrWhiteSpace(options.RealmHostname) || string.IsNullOrWhiteSpace(options.UserToken))
            throw new InvalidOperationException("QuickBase realm hostname and user token are required.");
        _http = http;
        _options = options;
        _limiter = limiter;
        _logger = logger;
        _delay = delay ?? Task.Delay;
    }

    public async Task<IReadOnlyList<QbTable>> GetTablesAsync(string appId, CancellationToken ct = default)
    {
        using var doc = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"tables?appId={Uri.EscapeDataString(appId)}"), ct);
        return doc.RootElement.EnumerateArray()
            .Select(t => new QbTable(t.GetProperty("id").GetString()!, t.GetProperty("name").GetString()!,
                t.TryGetProperty("alias", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : null))
            .ToList();
    }

    public async Task<IReadOnlyList<QbField>> GetFieldsAsync(string tableId, CancellationToken ct = default)
    {
        using var doc = await SendAsync(() => new HttpRequestMessage(HttpMethod.Get, $"fields?tableId={Uri.EscapeDataString(tableId)}"), ct);
        return ParseFields(doc.RootElement);
    }

    /// <summary>Reads the fields response. Address sub-fields are linked from their composite's <c>properties.compositeFields</c>
    /// (ids or objects with an id); a sub-field's own <c>properties.parentFieldId</c> is honoured too.</summary>
    public static IReadOnlyList<QbField> ParseFields(JsonElement array)
    {
        var raw = new List<(int Id, string Label, string Type, string? Mode, List<int> Composite, int? Parent)>();
        foreach (var f in array.EnumerateArray())
        {
            var composite = new List<int>();
            int? parent = null;
            if (f.TryGetProperty("properties", out var props) && props.ValueKind == JsonValueKind.Object)
            {
                if (props.TryGetProperty("compositeFields", out var cf) && cf.ValueKind == JsonValueKind.Array)
                    foreach (var c in cf.EnumerateArray())
                    {
                        if (c.ValueKind == JsonValueKind.Number) composite.Add(c.GetInt32());
                        else if (c.ValueKind == JsonValueKind.Object && c.TryGetProperty("id", out var cid)) composite.Add(cid.GetInt32());
                    }
                if (props.TryGetProperty("parentFieldId", out var p) && p.ValueKind == JsonValueKind.Number) parent = p.GetInt32();
            }
            raw.Add((f.GetProperty("id").GetInt32(), f.GetProperty("label").GetString() ?? "", f.GetProperty("fieldType").GetString() ?? "",
                f.TryGetProperty("mode", out var m) && m.ValueKind == JsonValueKind.String && m.GetString() is { Length: > 0 } mode ? mode : null,
                composite, parent));
        }
        var parentOf = raw.SelectMany(r => r.Composite.Select(c => (Child: c, Parent: r.Id))).GroupBy(x => x.Child).ToDictionary(g => g.Key, g => g.First().Parent);
        return raw.Select(r => new QbField(r.Id, r.Label, r.Type, r.Mode, r.Composite,
            r.Parent ?? (parentOf.TryGetValue(r.Id, out var p) ? p : null))).ToList();
    }

    public async Task<QbQueryPage> QueryAsync(string tableId, IReadOnlyList<int> select, string? where, int skip, int? top, CancellationToken ct = default)
    {
        var body = new Dictionary<string, object?>
        {
            ["from"] = tableId,
            ["select"] = select,
            ["sortBy"] = new[] { new { fieldId = QbSystemFids.RecordId, order = "ASC" } },
            ["options"] = top is null ? new { skip } : new { skip, top = top.Value },
        };
        if (!string.IsNullOrEmpty(where)) body["where"] = where;
        var payload = JsonSerializer.Serialize(body, Json);

        using var doc = await SendAsync(() => new HttpRequestMessage(HttpMethod.Post, "records/query")
        {
            Content = new StringContent(payload, Encoding.UTF8, "application/json"),
        }, ct);
        return ParseQuery(doc.RootElement);
    }

    public static QbQueryPage ParseQuery(JsonElement root)
    {
        var rows = new List<QbRow>();
        foreach (var record in root.GetProperty("data").EnumerateArray())
        {
            var values = new Dictionary<int, JsonElement>();
            foreach (var cell in record.EnumerateObject())
                if (int.TryParse(cell.Name, NumberStyles.None, CultureInfo.InvariantCulture, out var fid) && cell.Value.TryGetProperty("value", out var v))
                    values[fid] = v.Clone();
            rows.Add(new QbRow(values));
        }
        var meta = root.GetProperty("metadata");
        return new QbQueryPage(rows, meta.GetProperty("totalRecords").GetInt32(), meta.GetProperty("numRecords").GetInt32(), meta.GetProperty("skip").GetInt32());
    }

    private async Task<JsonDocument> SendAsync(Func<HttpRequestMessage> build, CancellationToken ct)
    {
        var rateLimited = 0;
        var serverErrors = 0;
        var timeouts = 0;
        while (true)
        {
            await _limiter.WaitAsync(ct);
            using var request = build();
            request.Headers.Add("QB-Realm-Hostname", _options.RealmHostname);
            request.Headers.Authorization = new AuthenticationHeaderValue("QB-USER-TOKEN", _options.UserToken);
            request.Headers.UserAgent.ParseAdd("PowerBase-QbMigration/1.0");

            HttpResponseMessage response;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(_options.RequestTimeout);
            try
            {
                response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, timeout.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                if (++timeouts > 1) throw new QuickBaseTimeoutException($"{request.RequestUri} timed out twice ({_options.RequestTimeout.TotalSeconds:0} s).");
                _logger.LogWarning("QuickBase timed out on {Uri}; retrying in 15 s", request.RequestUri);
                await _delay(TimeSpan.FromSeconds(15), ct);
                continue;
            }
            catch (HttpRequestException ex)
            {
                if (++serverErrors > _options.MaxServerErrorRetries) throw;
                _logger.LogWarning(ex, "QuickBase connection error on {Uri} (attempt {Attempt})", request.RequestUri, serverErrors);
                await _delay(Backoff(serverErrors), ct);
                continue;
            }

            using (response)
            {
                if (response.IsSuccessStatusCode)
                {
                    var stream = await response.Content.ReadAsStreamAsync(ct);
                    return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
                }

                var status = (int)response.StatusCode;
                var text = await response.Content.ReadAsStringAsync(ct);
                LogFailure(request, response, text);

                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    if (++rateLimited > _options.MaxRateLimitRetries) throw new QuickBaseRequestException(status, "QuickBase kept answering 429 Too Many Requests.");
                    var wait = RetryAfter(response) ?? Backoff(rateLimited);
                    _limiter.PauseUntil(DateTime.UtcNow + wait);
                    await _delay(wait, ct);
                    continue;
                }
                if (status >= 500)
                {
                    if (++serverErrors > _options.MaxServerErrorRetries) throw new HttpRequestException($"QuickBase answered {status} {Trim(text)}", null, response.StatusCode);
                    await _delay(Backoff(serverErrors), ct);
                    continue;
                }
                throw new QuickBaseRequestException(status, $"QuickBase answered {status}: {Trim(text)}");
            }
        }
    }

    /// <summary>1, 2, 4, 8, 16 s.</summary>
    private static TimeSpan Backoff(int attempt) => TimeSpan.FromSeconds(Math.Pow(2, Math.Min(attempt, 5) - 1));

    /// <summary>retry-after is either seconds or an HTTP date.</summary>
    public static TimeSpan? RetryAfter(HttpResponseMessage response)
    {
        var header = response.Headers.RetryAfter;
        if (header?.Delta is { } delta) return delta;
        if (header?.Date is { } date) { var wait = date - DateTimeOffset.UtcNow; return wait > TimeSpan.Zero ? wait : TimeSpan.Zero; }
        return null;
    }

    private void LogFailure(HttpRequestMessage request, HttpResponseMessage response, string body)
    {
        string? Header(string name) => response.Headers.TryGetValues(name, out var v) ? string.Join(",", v) : null;
        _logger.LogWarning("QuickBase {Status} on {Method} {Uri}; cf-ray={CfRay} tid={Tid} qb-api-ray={QbApiRay} body={Body}",
            (int)response.StatusCode, request.Method, request.RequestUri, Header("cf-ray"), Header("tid"), Header("qb-api-ray"), Trim(body));
    }

    private static string Trim(string text) => text.Length <= 500 ? text : text[..500];
}
