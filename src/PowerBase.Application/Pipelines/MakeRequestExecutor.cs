using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using PowerBase.Domain.Exceptions;
using YamlDotNet.Serialization;

namespace PowerBase.Application.Pipelines;

public sealed class MakeRequestExecutor
{
    public const int MaxBytes = 10 * 1024 * 1024;
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
    public MakeRequestExecutor(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;
    private static InvalidOperationException Invalid(string message) => new(message);
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase) { "Transfer-Encoding", "Content-Encoding", "Content-Length", "Host" };

    public static Uri BuildUrl(MakeRequestDefinition config, Func<string?, string> resolve)
    {
        if (config.IsPowerBase)
        {
            var value = resolve(config.Url);
            if (string.IsNullOrWhiteSpace(value) || value.StartsWith("//") || value.Contains('\\')) throw Invalid("A valid PowerBase URL is required.");
            return Uri.TryCreate(value, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https"
                ? absolute : new Uri(new Uri("https://powerbase.internal/"), value.TrimStart('/'));
        }
        var baseValue = resolve(string.IsNullOrWhiteSpace(config.BaseUrl) ? config.Url : config.BaseUrl);
        if (!Uri.TryCreate(baseValue, UriKind.Absolute, out var baseUri) || baseUri.Scheme is not ("http" or "https") || !string.IsNullOrEmpty(baseUri.UserInfo))
            throw Invalid("Base URL must be an HTTP or HTTPS URL without embedded credentials.");
        var path = resolve(config.Path);
        Uri target;
        if (string.IsNullOrWhiteSpace(path)) target = baseUri;
        else if (Uri.TryCreate(path, UriKind.Absolute, out var full) && full.Scheme is "http" or "https") target = full;
        else
        {
            if (path.StartsWith("//") || path.Contains('\\')) throw Invalid("Path must stay within the connection base URL.");
            var prefix = new UriBuilder(baseUri) { Query = "", Fragment = "", Path = baseUri.AbsolutePath.TrimEnd('/') + "/" }.Uri;
            target = new Uri(prefix, path.TrimStart('/'));
        }
        if (target.Scheme != baseUri.Scheme || target.Authority != baseUri.Authority || !target.AbsolutePath.StartsWith(baseUri.AbsolutePath, StringComparison.Ordinal))
            throw Invalid("Request URL must stay within the connection base URL.");
        var query = ParseQuery(baseUri.Query);
        foreach (var pair in ParseQuery(target.Query)) query[pair.Key] = pair.Value;
        foreach (var group in config.QueryParams.Where(x => !string.IsNullOrWhiteSpace(x.HeaderName)).GroupBy(x => resolve(x.HeaderName)))
            query[group.Key] = group.Select(x => resolve(x.Value)).ToList();
        if (config.AuthType == "API Key" && config.ApiKeyPlacement.Contains("Query", StringComparison.OrdinalIgnoreCase))
        {
            var key = resolve(config.ApiKeyName);
            if (string.IsNullOrWhiteSpace(key)) throw Invalid("API key name is required.");
            if (ParseQuery(target.Query).ContainsKey(key) || config.QueryParams.Any(x => resolve(x.HeaderName) == key))
                throw Invalid("The API key query parameter cannot be overridden.");
            query[key] = new() { resolve(config.ApiKeyValue) };
        }
        return new UriBuilder(target) { Query = string.Join("&", query.SelectMany(pair => pair.Value.Select(value => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(value)))), Fragment = "" }.Uri;
    }
    private static Dictionary<string, List<string>> ParseQuery(string query)
    {
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var part in query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var pair = part.Split('=', 2);
            var key = WebUtility.UrlDecode(pair[0]);
            if (!result.TryGetValue(key, out var values)) result[key] = values = new();
            values.Add(pair.Length > 1 ? WebUtility.UrlDecode(pair[1]) : "");
        }
        return result;
    }
    public async Task<MakeRequestResult> ExecuteAsync(MakeRequestDefinition config, Func<string?, string> resolve, CancellationToken ct, Action<Dictionary<string, object?>>? audit = null)
    {
        config.Validate();
        var method = resolve(config.Method).ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(method)) method = "GET";
        if (!new[] { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" }.Contains(method)) throw Invalid("Unsupported HTTP method.");
        using var request = new HttpRequestMessage(new HttpMethod(method), BuildUrl(config, resolve));
        if (new[] { "POST", "PUT", "PATCH", "DELETE" }.Contains(method))
        {
            var encoding = Encoding.GetEncoding(string.IsNullOrWhiteSpace(config.Encoding) ? "utf-8" : config.Encoding);
            var bytes = encoding.GetBytes(resolve(config.Body));
            if (bytes.Length > MaxBytes) throw Invalid("Request content exceeds 10 MB.");
            request.Content = new ByteArrayContent(bytes);
            request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(string.IsNullOrWhiteSpace(config.ContentType) ? "application/json" : resolve(config.ContentType));
            request.Content.Headers.ContentType.CharSet = encoding.WebName;
        }
        var connectionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void AddHeader(string name, string value)
        {
            if (string.IsNullOrWhiteSpace(name)) return;
            if (ForbiddenHeaders.Contains(name) || name.Any(c => c <= 32 || c >= 127) || value.Contains('\r') || value.Contains('\n'))
                throw Invalid("Invalid or reserved HTTP header: " + name);
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content ??= new ByteArrayContent(Array.Empty<byte>());
                if (name.Equals("Content-Type", StringComparison.OrdinalIgnoreCase)) request.Content.Headers.ContentType = MediaTypeHeaderValue.Parse(value);
                else if (!request.Content.Headers.TryAddWithoutValidation(name, value)) throw Invalid("Invalid HTTP header: " + name);
            }
        }
        foreach (var header in config.ConnectionHeaders)
        {
            var name = resolve(header.HeaderName);
            if (string.IsNullOrWhiteSpace(name)) continue;
            AddHeader(name, resolve(header.Value)); connectionNames.Add(name);
        }
        foreach (var header in config.IsPowerBase ? config.HeadersList : config.StepHeaders.Count > 0 ? config.StepHeaders : config.HeadersList)
        {
            var names = resolve(header.HeaderName).Replace("\r\n", "\n").Split('\n');
            var values = resolve(header.Value).Replace("\r\n", "\n").Split('\n');
            if (names.Length != values.Length) throw Invalid("Header name and value lists must contain the same number of lines.");
            for (var i = 0; i < names.Length; i++)
            {
                if (connectionNames.Contains(names[i])) continue;
                AddHeader(names[i], values[i]);
            }
        }
        if (!config.IsPowerBase) await AuthenticateAsync(config, request, resolve, ct);
        object? BodyValue(string value)
        {
            try { return JsonSerializer.Deserialize<JsonElement>(value); }
            catch (JsonException) { return value; }
        }
        Dictionary<string, string> AuditHeaders(IEnumerable<KeyValuePair<string, IEnumerable<string>>> headers) =>
            headers.ToDictionary(x => x.Key, x =>
                x.Key.Equals(config.ApiKeyName, StringComparison.OrdinalIgnoreCase) && config.AuthType == "API Key"
                    || x.Key.Contains("authorization", StringComparison.OrdinalIgnoreCase)
                    || x.Key.Contains("cookie", StringComparison.OrdinalIgnoreCase)
                    ? "[REDACTED]" : string.Join(", ", x.Value), StringComparer.OrdinalIgnoreCase);
        var query = ParseQuery(request.RequestUri!.Query);
        if (config.AuthType == "API Key" && !string.IsNullOrEmpty(config.ApiKeyName) && query.ContainsKey(config.ApiKeyName))
            query[config.ApiKeyName] = new() { "[REDACTED]" };
        var auditData = new Dictionary<string, object?>
        {
            ["RequestMode"] = config.RequestMode ?? "http", ["Method"] = method,
            ["Url"] = request.RequestUri.GetLeftPart(UriPartial.Path), ["QueryParameters"] = query,
            ["Headers"] = AuditHeaders(request.Headers.Concat(request.Content?.Headers.AsEnumerable() ?? Enumerable.Empty<KeyValuePair<string, IEnumerable<string>>>())),
            ["Body"] = request.Content == null ? null : BodyValue(await request.Content.ReadAsStringAsync(ct))
        };
        audit?.Invoke(auditData);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(5));
        using var response = await _send(request, timeout.Token);
        auditData["HTTPStatus"] = (int)response.StatusCode;
        auditData["StatusMessage"] = response.ReasonPhrase ?? "";
        auditData["ResponseHeaders"] = AuditHeaders(response.Headers.Concat(response.Content.Headers));
        auditData["ResponseBodyAvailable"] = false;
        audit?.Invoke(auditData);
        if (response.Content.Headers.ContentLength > MaxBytes) throw Invalid("Response content exceeds 10 MB.");
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await stream.ReadAsync(buffer, timeout.Token)) != 0)
        {
            if (output.Length + read > MaxBytes) throw Invalid("Response content exceeds 10 MB.");
            output.Write(buffer, 0, read);
        }
        var charset = response.Content.Headers.ContentType?.CharSet?.Trim('"');
        var body = (string.IsNullOrWhiteSpace(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset)).GetString(output.ToArray());
        auditData["ResponseBody"] = BodyValue(body);
        auditData["ResponseSize"] = output.Length;
        auditData["ResponseBodyAvailable"] = true;
        audit?.Invoke(auditData);
        var status = (int)response.StatusCode;
        var exempt = config.ErrorsOption == "none" || config.ErrorsOption == "custom" && (config.ExemptErrorStatuses ?? "").Split(',').Any(x => x.Trim() == status.ToString());
        if (status >= 400 && !exempt)
        {
            if (status < 500 && status != 429) throw new PipelineRequestRejectedException(status);
            throw new HttpRequestException($"HTTP request failed with status {status}.", null, response.StatusCode);
        }
        var payloadType = config.ExpectedPayloadType ?? (config.RequestMode == "http" || config.IsPowerBase && response.Content.Headers.ContentType?.MediaType?.Contains("json", StringComparison.OrdinalIgnoreCase) == true ? "JSON" : "TEXT");
        string result = body;
        if (!string.IsNullOrEmpty(body) && method != "HEAD" && status != 204)
        {
            try
            {
                if (payloadType == "JSON") { using var parsed = JsonDocument.Parse(body); result = parsed.RootElement.GetRawText(); }
                else if (payloadType == "YAML") result = JsonSerializer.Serialize(new DeserializerBuilder().Build().Deserialize<object>(body));
                else if (payloadType == "XML") System.Xml.Linq.XDocument.Parse(body);
            }
            catch (Exception ex) when (ex is JsonException or YamlDotNet.Core.YamlException or System.Xml.XmlException)
            {
                if (config.ValidateResponsePayload != "No") throw new PipelineNonRetryableException("Response does not match the expected payload format.");
                result = JsonSerializer.Serialize(body);
            }
        }
        if (payloadType is "TEXT" or "Raw" or "XML") result = JsonSerializer.Serialize(body);
        if (string.IsNullOrEmpty(result)) result = "null";
        var reqHeaders = auditData != null && auditData.TryGetValue("Headers", out var hObj) && hObj is Dictionary<string, string> h ? h : new Dictionary<string, string>();
        return new(result, status, response.ReasonPhrase ?? "", response.Headers.Concat(response.Content.Headers).Where(x => !x.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase) && !x.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)).ToDictionary(x => x.Key, x => string.Join(", ", x.Value), StringComparer.OrdinalIgnoreCase),
            request.RequestUri?.ToString() ?? "", reqHeaders);
    }
    private async Task AuthenticateAsync(MakeRequestDefinition config, HttpRequestMessage request, Func<string?, string> resolve, CancellationToken ct)
    {
        string Required(string? value, string label) { var result = resolve(value); return string.IsNullOrWhiteSpace(result) ? throw Invalid(label + " is required.") : result; }
        switch (config.AuthType)
        {
            case "No Authentication": case "": return;
            case "Basic Authentication": request.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Required(config.Username, "Username") + ":" + resolve(config.Password)))); break;
            case "Bearer Token": request.Headers.Authorization = new("Bearer", Required(config.BearerToken, "Bearer token")); break;
            case "API Key":
                if (!config.ApiKeyPlacement.Contains("Query", StringComparison.OrdinalIgnoreCase)) request.Headers.Add(Required(config.ApiKeyName, "API key name"), Required(config.ApiKeyValue, "API key value"));
                break;
            case "JWT": request.Headers.Authorization = new("Bearer", CreateJwt(config, resolve)); break;
            case "OAuth 2.0":
                if (config.OAuthGrantType != "Client credentials")
                {
                    if (string.IsNullOrWhiteSpace(config.OAuthAccessToken)) throw Invalid("Connect this OAuth account before running its authorization-code request.");
                    request.Headers.Authorization = new("Bearer", config.OAuthAccessToken); break;
                }
                using (var tokenRequest = new HttpRequestMessage(HttpMethod.Post, Required(config.OAuthTokenEndpoint, "OAuth token endpoint")))
                {
                    var form = new Dictionary<string, string> { ["grant_type"] = "client_credentials" };
                    if (!string.IsNullOrWhiteSpace(config.OAuthScope)) form["scope"] = resolve(config.OAuthScope);
                    if (config.OAuthClientAuth?.Contains("body", StringComparison.OrdinalIgnoreCase) == true)
                    { form["client_id"] = Required(config.OAuthClientId, "Client ID"); form["client_secret"] = resolve(config.OAuthClientSecret); }
                    else tokenRequest.Headers.Authorization = new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(Required(config.OAuthClientId, "Client ID") + ":" + resolve(config.OAuthClientSecret))));
                    tokenRequest.Content = new FormUrlEncodedContent(form);
                    using var tokenResponse = await _send(tokenRequest, ct);
                    if (!tokenResponse.IsSuccessStatusCode) throw Invalid("OAuth token exchange failed.");
                    using var tokenJson = JsonDocument.Parse(await tokenResponse.Content.ReadAsStringAsync(ct));
                    request.Headers.Authorization = new("Bearer", tokenJson.RootElement.GetProperty("access_token").GetString());
                }
                break;
            default: throw Invalid("Unsupported authentication type: " + config.AuthType);
        }
    }
    public static string CreateJwt(MakeRequestDefinition config, Func<string?, string> resolve)
    {
        static string Base64(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        var algorithm = string.IsNullOrWhiteSpace(config.JwtAlg) ? "none" : config.JwtAlg.Split(' ')[0];
        var headers = JsonNode.Parse(resolve(config.JwtHeaders)) as JsonObject ?? throw Invalid("JWT headers must be a JSON object.");
        var claims = JsonNode.Parse(resolve(config.JwtClaims)) as JsonObject ?? throw Invalid("JWT claims must be a JSON object.");
        headers["alg"] = algorithm; headers["typ"] = "JWT";
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (config.JwtUseIat) claims["iat"] = now;
        var duration = resolve(config.JwtExp);
        double seconds;
        if (!double.TryParse(duration, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out seconds))
        {
            if (!Regex.IsMatch(duration, @"^(\d+[dhms])+$")) throw Invalid("Invalid JWT expiration duration.");
            seconds = Regex.Matches(duration, @"(\d+)([dhms])").Sum(match => double.Parse(match.Groups[1].Value) * (match.Groups[2].Value switch { "d" => 86400, "h" => 3600, "m" => 60, _ => 1 }));
        }
        if (seconds > 0) claims["exp"] = now + (long)seconds;
        var data = Base64(Encoding.UTF8.GetBytes(headers.ToJsonString())) + "." + Base64(Encoding.UTF8.GetBytes(claims.ToJsonString()));
        var bytes = Encoding.UTF8.GetBytes(data);
        byte[] signature;
        var key = resolve(config.JwtSigningKey);
        if (algorithm != "none" && string.IsNullOrWhiteSpace(key)) throw Invalid("JWT signing key is required.");
        if (algorithm == "none") signature = Array.Empty<byte>();
        else if (algorithm.StartsWith("HS"))
        {
            using HMAC hmac = algorithm switch { "HS256" => new HMACSHA256(Encoding.UTF8.GetBytes(key)), "HS384" => new HMACSHA384(Encoding.UTF8.GetBytes(key)), "HS512" => new HMACSHA512(Encoding.UTF8.GetBytes(key)), _ => throw Invalid("Unsupported signing algorithm.") };
            signature = hmac.ComputeHash(bytes);
        }
        else if (algorithm is "RS256" or "RS384" or "RS512")
        {
            using var rsa = RSA.Create(); rsa.ImportFromPem(key);
            signature = rsa.SignData(bytes, algorithm == "RS256" ? HashAlgorithmName.SHA256 : algorithm == "RS384" ? HashAlgorithmName.SHA384 : HashAlgorithmName.SHA512, RSASignaturePadding.Pkcs1);
        }
        else throw Invalid("Unsupported signing algorithm.");
        return data + "." + Base64(signature);
    }
}
public sealed record MakeRequestResult(string OutputJson, int StatusCode, string StatusMessage, Dictionary<string, string> ResponseHeaders, string RequestUrl, Dictionary<string, string> RequestHeaders);
