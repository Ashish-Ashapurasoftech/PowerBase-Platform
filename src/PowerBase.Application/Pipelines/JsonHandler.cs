using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using PowerBase.Domain.Exceptions;

namespace PowerBase.Application.Pipelines;

/// <summary>
/// JSON Handler channel, as Quickbase Pipelines has it: "Fetch JSON" (action, fetches a JSON resource from a URL) and
/// "Iterate over JSON records" (query, turns the fetched JSON into a list — the step a Loop runs over).
/// Behaviour measured on a live Quickbase realm (2026-11-16):
/// - no Records Path: a top-level array gives its items, any other value is one record;
/// - a Records Path (/data/records) that does not exist fails the step: "Validation error: Invalid JSON records location: '/x'";
/// - each record = { raw_record: the record, + the Schema Sample's keys the record has (nested objects cut to the sample's keys) };
/// - filters (OR of AND groups) run first, then the limit (0 / empty = unlimited).
/// </summary>
public static class JsonHandler
{
    public const string FetchSubtype = "fetch-json";
    public const string IterateSubtype = "iterate-json-records";
    public const int MaxBytes = 10 * 1024 * 1024;

    public static readonly string[] AuthenticationSchemas = { "no-auth", "http-basic", "http-digest", "http-qb-token" };
    public static readonly string[] Methods = { "GET", "POST", "PUT", "PATCH", "DELETE", "OPTIONS" };

    public static readonly string[] StringConditions = { "starts", "not_starts", "ends", "not_ends", "contains", "not_contains", "equals", "not_equals", "regex", "not_regex", "has_value", "has_no_value" };
    public static readonly string[] NumberConditions = { "gt", "lt", "equals", "not_equals", "between", "not_between", "has_value", "has_no_value" };
    public static readonly string[] BooleanConditions = { "equals", "not_equals", "has_value", "has_no_value" };
    public static readonly string[] ObjectConditions = { "contains", "not_contains", "has_value", "has_no_value" };

    public static bool IsJsonSubtype(string? subtype) => subtype is FetchSubtype or IterateSubtype;
}

public sealed class FetchJsonDefinition
{
    public string AuthenticationSchema { get; set; } = "no-auth";
    public bool DisableSslVerification { get; set; }
    public string? JsonUrl { get; set; }
    public string MethodType { get; set; } = "GET";
    public string? Headers { get; set; }
    public string? Body { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? Token { get; set; }
    public bool ProxyViaAgent { get; set; }
    public string? ForceContentEncoding { get; set; }

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };

    public static FetchJsonDefinition Read(string? json)
    {
        var result = new FetchJsonDefinition();
        if (string.IsNullOrWhiteSpace(json)) return result;
        try
        {
            var node = JsonNode.Parse(json) as JsonObject;
            if (node == null) return result;
            result.AuthenticationSchema = Text(node, "authenticationSchema") is { Length: > 0 } a ? a : "no-auth";
            result.DisableSslVerification = Bool(node, "disableSslVerification");
            result.JsonUrl = Text(node, "jsonUrl");
            result.MethodType = Text(node, "methodType") is { Length: > 0 } m ? m : "GET";
            result.Headers = Text(node, "headers");
            result.Body = Text(node, "body");
            result.Username = Text(node, "username");
            result.Password = Text(node, "password");
            result.Token = Text(node, "token");
            result.ProxyViaAgent = Bool(node, "proxyViaAgent");
            result.ForceContentEncoding = Text(node, "forceContentEncoding");
        }
        catch (JsonException) { }
        return result;
    }

    private static string? Text(JsonObject node, string key)
    {
        var value = node.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        return value is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : null;
    }

    private static bool Bool(JsonObject node, string key)
    {
        var value = node.FirstOrDefault(p => string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)).Value;
        if (value is not JsonValue v) return false;
        if (v.TryGetValue<bool>(out var b)) return b;
        return v.TryGetValue<string>(out var s) && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s.Equals("yes", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>Quickbase's "required field" checks: JSON URL always; Username + Password for HTTP Basic / Digest; Token for a user token.</summary>
    public List<string> Errors()
    {
        var errors = new List<string>();
        if (!JsonHandler.AuthenticationSchemas.Contains(AuthenticationSchema)) errors.Add("Authentication Schema: unallowed value " + AuthenticationSchema);
        if (!JsonHandler.Methods.Contains((MethodType ?? "GET").ToUpperInvariant()) && !(MethodType ?? "").Contains("{{")) errors.Add("Outgoing request's method type: unallowed value " + MethodType);
        if (AuthenticationSchema is "http-basic" or "http-digest")
        {
            if (string.IsNullOrWhiteSpace(Username)) errors.Add("Username: required field");
            if (string.IsNullOrWhiteSpace(Password)) errors.Add("Password: required field");
        }
        if (AuthenticationSchema == "http-qb-token" && string.IsNullOrWhiteSpace(Token)) errors.Add("Token: required field");
        if (string.IsNullOrWhiteSpace(JsonUrl)) errors.Add("JSON URL: required field");
        return errors;
    }
}

public sealed record FetchJsonResult(int Status, Dictionary<string, string> Headers, string Body, string Url);

/// <summary>Runs one Fetch JSON request. The HTTP send is injected so tests (and the SSL / redirect policy) stay outside.</summary>
public sealed class FetchJsonExecutor
{
    private readonly Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> _send;
    private static readonly HashSet<string> ForbiddenHeaders = new(StringComparer.OrdinalIgnoreCase) { "Transfer-Encoding", "Content-Length", "Host" };
    public FetchJsonExecutor(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) => _send = send;

    public async Task<FetchJsonResult> ExecuteAsync(FetchJsonDefinition config, Func<string?, string> resolve, CancellationToken ct, Action<Dictionary<string, object?>>? audit = null)
    {
        var errors = config.Errors();
        if (errors.Count > 0) throw new InvalidOperationException(errors[0]);
        var url = resolve(config.JsonUrl).Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            throw new InvalidOperationException($"Invalid JSON URL: '{url}'");
        var method = resolve(config.MethodType).Trim().ToUpperInvariant();
        if (string.IsNullOrEmpty(method)) method = "GET";
        if (!JsonHandler.Methods.Contains(method)) throw new InvalidOperationException("Outgoing request's method type: unallowed value " + method);

        var headers = ParseHeaders(resolve(config.Headers));
        HttpResponseMessage? response = null;
        var auditData = new Dictionary<string, object?> { ["Method"] = method, ["Url"] = uri.GetLeftPart(UriPartial.Path), ["AuthenticationSchema"] = config.AuthenticationSchema };
        try
        {
            response = await SendAsync(config, resolve, method, uri, headers, null, ct);
            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && config.AuthenticationSchema == "http-digest")
            {
                var challenge = response.Headers.WwwAuthenticate.FirstOrDefault(h => h.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase));
                if (challenge != null)
                {
                    var digest = DigestHeader(challenge.Parameter ?? "", method, uri, resolve(config.Username), resolve(config.Password));
                    response.Dispose();
                    response = await SendAsync(config, resolve, method, uri, headers, digest, ct);
                }
            }
            var status = (int)response.StatusCode;
            auditData["HTTPStatus"] = status;
            audit?.Invoke(auditData);
            if (response.Content.Headers.ContentLength > JsonHandler.MaxBytes) throw new InvalidOperationException("The JSON document exceeds 10 MB.");
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var output = new MemoryStream();
            var buffer = new byte[8192];
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) != 0)
            {
                if (output.Length + read > JsonHandler.MaxBytes) throw new InvalidOperationException("The JSON document exceeds 10 MB.");
                output.Write(buffer, 0, read);
            }
            if (status >= 400)
            {
                if (status < 500 && status != 429) throw new PipelineRequestRejectedException(status);
                throw new HttpRequestException($"HTTP request failed with status {status}.", null, response.StatusCode);
            }
            var encoding = ResolveEncoding(resolve(config.ForceContentEncoding), response.Content.Headers.ContentType?.CharSet);
            var body = encoding.GetString(output.ToArray());
            if (body.Length > 0 && body[0] == '﻿') body = body[1..];
            var responseHeaders = response.Headers.Concat(response.Content.Headers)
                .Where(h => !h.Key.Equals("Set-Cookie", StringComparison.OrdinalIgnoreCase))
                .GroupBy(h => h.Key, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => string.Join(", ", g.SelectMany(x => x.Value)), StringComparer.OrdinalIgnoreCase);
            return new FetchJsonResult(status, responseHeaders, body, uri.ToString());
        }
        finally { response?.Dispose(); }
    }

    private async Task<HttpResponseMessage> SendAsync(FetchJsonDefinition config, Func<string?, string> resolve, string method, Uri uri,
        List<KeyValuePair<string, string>> headers, string? digestAuthorization, CancellationToken ct)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), uri);
        var body = resolve(config.Body);
        if (!string.IsNullOrEmpty(body) && method is not ("GET" or "OPTIONS"))
        {
            request.Content = new StringContent(body, Encoding.UTF8);
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
        }
        foreach (var (name, value) in headers)
        {
            if (ForbiddenHeaders.Contains(name)) continue;
            if (!request.Headers.TryAddWithoutValidation(name, value))
            {
                request.Content ??= new ByteArrayContent(Array.Empty<byte>());
                request.Content.Headers.Remove(name);
                request.Content.Headers.TryAddWithoutValidation(name, value);
            }
        }
        switch (config.AuthenticationSchema)
        {
            case "http-basic":
                request.Headers.Authorization = new AuthenticationHeaderValue("Basic",
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(resolve(config.Username) + ":" + resolve(config.Password))));
                break;
            case "http-qb-token":
                // Quickbase sends its user token as "QB-USER-TOKEN <token>" — the same header PowerBase's API accepts.
                request.Headers.TryAddWithoutValidation("Authorization", "QB-USER-TOKEN " + resolve(config.Token));
                break;
            case "http-digest" when digestAuthorization != null:
                request.Headers.TryAddWithoutValidation("Authorization", digestAuthorization);
                break;
        }
        var response = await _send(request, ct);
        return response;
    }

    public static List<KeyValuePair<string, string>> ParseHeaders(string? text)
    {
        var result = new List<KeyValuePair<string, string>>();
        foreach (var raw in (text ?? "").Replace("\r\n", "\n").Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            var colon = line.IndexOf(':');
            if (colon <= 0) throw new InvalidOperationException($"Invalid header: '{line}'");
            var name = line[..colon].Trim();
            if (name.Any(c => c <= 32 || c >= 127)) throw new InvalidOperationException($"Invalid header: '{line}'");
            result.Add(new(name, line[(colon + 1)..].Trim()));
        }
        return result;
    }

    public static Encoding ResolveEncoding(string? forced, string? charset)
    {
        var name = !string.IsNullOrWhiteSpace(forced) ? forced.Trim() : charset?.Trim('"', ' ');
        if (string.IsNullOrWhiteSpace(name)) return new UTF8Encoding(false);
        var normalized = name.Replace('_', '-');
        try { return Encoding.GetEncoding(normalized); }
        catch (ArgumentException)
        {
            try { return Encoding.GetEncoding(name); }
            catch (ArgumentException)
            {
                if (!string.IsNullOrWhiteSpace(forced)) throw new InvalidOperationException($"Force Content Encoding: unknown encoding '{forced}'");
                return new UTF8Encoding(false);
            }
        }
    }

    private static string DigestHeader(string challenge, string method, Uri uri, string username, string password)
    {
        var parts = Regex.Matches(challenge, "(\\w+)=(?:\"([^\"]*)\"|([^,\\s]*))")
            .ToDictionary(m => m.Groups[1].Value.ToLowerInvariant(), m => m.Groups[2].Success && m.Groups[2].Length > 0 ? m.Groups[2].Value : m.Groups[3].Value);
        parts.TryGetValue("realm", out var realm);
        parts.TryGetValue("nonce", out var nonce);
        parts.TryGetValue("qop", out var qop);
        parts.TryGetValue("opaque", out var opaque);
        parts.TryGetValue("algorithm", out var algorithm);
        static string Md5(string value) => Convert.ToHexString(System.Security.Cryptography.MD5.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
        var path = uri.PathAndQuery;
        var cnonce = Guid.NewGuid().ToString("N")[..16];
        var ha1 = Md5($"{username}:{realm}:{password}");
        if (string.Equals(algorithm, "MD5-sess", StringComparison.OrdinalIgnoreCase)) ha1 = Md5($"{ha1}:{nonce}:{cnonce}");
        var ha2 = Md5($"{method}:{path}");
        var useQop = (qop ?? "").Split(',').Select(x => x.Trim()).Contains("auth");
        var response = useQop ? Md5($"{ha1}:{nonce}:00000001:{cnonce}:auth:{ha2}") : Md5($"{ha1}:{nonce}:{ha2}");
        var header = new StringBuilder($"Digest username=\"{username}\", realm=\"{realm}\", nonce=\"{nonce}\", uri=\"{path}\", response=\"{response}\"");
        if (!string.IsNullOrEmpty(algorithm)) header.Append($", algorithm={algorithm}");
        if (useQop) header.Append($", qop=auth, nc=00000001, cnonce=\"{cnonce}\"");
        if (!string.IsNullOrEmpty(opaque)) header.Append($", opaque=\"{opaque}\"");
        return header.ToString();
    }
}

public sealed class JsonRecordFilter
{
    public string? Field { get; set; }
    public string? Condition { get; set; }
    public JsonNode? Value { get; set; }
    public JsonNode? Value2 { get; set; }
}

public sealed class IterateJsonDefinition
{
    public string? JsonSourceStepId { get; set; }
    public string? JsonSchemaSample { get; set; }
    public string? JsonRecordsPath { get; set; }
    public long? Limit { get; set; }
    /// <summary>Quickbase's filter shape: a list of OR groups, each a list of AND-ed rules.</summary>
    public List<List<JsonRecordFilter>> Filters { get; set; } = new();

    private static readonly JsonSerializerOptions Options = new() { PropertyNameCaseInsensitive = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowReadingFromString };

    public static IterateJsonDefinition Read(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new IterateJsonDefinition();
        try
        {
            var node = JsonNode.Parse(json) as JsonObject;
            if (node == null) return new IterateJsonDefinition();
            var result = new IterateJsonDefinition
            {
                JsonSourceStepId = Str(node["jsonSourceStepId"]),
                JsonSchemaSample = Str(node["jsonSchemaSample"]),
                JsonRecordsPath = Str(node["jsonRecordsPath"]),
            };
            var limit = node["limit"];
            if (limit is JsonValue lv)
            {
                if (lv.TryGetValue<long>(out var l)) result.Limit = l;
                else if (lv.TryGetValue<string>(out var ls) && long.TryParse(ls, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)) result.Limit = parsed;
            }
            if (node["filters"] is JsonArray groups)
            {
                foreach (var group in groups.OfType<JsonArray>())
                {
                    var rules = new List<JsonRecordFilter>();
                    foreach (var rule in group.OfType<JsonObject>())
                        rules.Add(new JsonRecordFilter { Field = Str(rule["field"]), Condition = Str(rule["condition"]), Value = rule["value"]?.DeepClone(), Value2 = rule["value2"]?.DeepClone() });
                    result.Filters.Add(rules);
                }
            }
            return result;
        }
        catch (JsonException) { return new IterateJsonDefinition(); }
    }

    private static string? Str(JsonNode? node) => node is JsonValue v ? (v.TryGetValue<string>(out var s) ? s : v.ToJsonString()) : null;

    public List<string> Errors()
    {
        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(JsonSourceStepId)) errors.Add("JSON Source: required field");
        if (!string.IsNullOrWhiteSpace(JsonSchemaSample) && !JsonRecordsIterator.TryParseSample(JsonSchemaSample, out _))
            errors.Add("JSON Schema Sample: invalid JSON");
        if (Limit < 0) errors.Add("Limit: must be 0 or greater");
        return errors;
    }
}

public static class JsonRecordsIterator
{
    public static bool TryParseSample(string? sample, out JsonObject? schema)
    {
        schema = null;
        if (string.IsNullOrWhiteSpace(sample)) return true;
        try
        {
            var node = JsonNode.Parse(sample);
            if (node is JsonArray array) node = array.FirstOrDefault(n => n is JsonObject);
            schema = node as JsonObject;
            return node == null || node is JsonObject;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>Records of the fetched document, as Quickbase's Iterate over JSON records returns them.</summary>
    public static JsonArray Iterate(string document, IterateJsonDefinition config, Func<string?, string>? resolve = null)
    {
        resolve ??= value => value ?? "";
        JsonNode? root;
        try { root = JsonNode.Parse(string.IsNullOrWhiteSpace(document) ? "null" : document); }
        catch (JsonException ex) { throw new PipelineNonRetryableException("Validation error: Invalid JSON document: " + ex.Message); }

        var path = resolve(config.JsonRecordsPath).Trim();
        if (path.Length > 0) root = Locate(root, path);

        IEnumerable<JsonNode?> items = root is JsonArray array ? array : root == null ? Array.Empty<JsonNode?>() : new[] { root };
        TryParseSample(config.JsonSchemaSample, out var schema);

        var output = new JsonArray();
        var limit = config.Limit.GetValueOrDefault();
        foreach (var item in items)
        {
            if (!Matches(item, config.Filters, resolve)) continue;
            output.Add(Project(item, schema));
            if (limit > 0 && output.Count >= limit) break;
        }
        return output;
    }

    private static JsonNode? Locate(JsonNode? root, string path)
    {
        var current = root;
        foreach (var raw in path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            var segment = raw.Replace("~1", "/").Replace("~0", "~");
            if (current is JsonObject obj && obj.TryGetPropertyValue(segment, out var next)) current = next;
            else if (current is JsonArray arr && int.TryParse(segment, NumberStyles.None, CultureInfo.InvariantCulture, out var index) && index < arr.Count) current = arr[index];
            else throw new PipelineNonRetryableException($"Validation error: Invalid JSON records location: '{path}'");
        }
        return current;
    }

    private static JsonObject Project(JsonNode? item, JsonObject? schema)
    {
        var result = new JsonObject { ["raw_record"] = item?.DeepClone() };
        if (schema == null || item is not JsonObject record) return result;
        foreach (var (key, sampleValue) in schema)
        {
            if (key == "raw_record" || !record.TryGetPropertyValue(key, out var value)) continue;
            result[key] = ProjectValue(value, sampleValue);
        }
        return result;
    }

    private static JsonNode? ProjectValue(JsonNode? value, JsonNode? sample)
    {
        if (sample is JsonObject sampleObject && value is JsonObject valueObject)
        {
            var projected = new JsonObject();
            foreach (var (key, nestedSample) in sampleObject)
                if (valueObject.TryGetPropertyValue(key, out var nested)) projected[key] = ProjectValue(nested, nestedSample);
            return projected;
        }
        return value?.DeepClone();
    }

    public static bool Matches(JsonNode? item, List<List<JsonRecordFilter>> groups, Func<string?, string> resolve)
    {
        var active = groups.Select(g => g.Where(r => !string.IsNullOrWhiteSpace(r.Field) && !string.IsNullOrWhiteSpace(r.Condition)).ToList())
            .Where(g => g.Count > 0).ToList();
        if (active.Count == 0) return true;
        return active.Any(group => group.All(rule => MatchesRule(item, rule, resolve)));
    }

    private static string ResolveValue(JsonNode? node, Func<string?, string> resolve) => node switch
    {
        null => "",
        JsonValue v when v.TryGetValue<string>(out var s) => resolve(s),
        _ => node.ToJsonString()
    };

    private static bool MatchesRule(JsonNode? item, JsonRecordFilter rule, Func<string?, string> resolve)
    {
        var field = rule.Field!.Trim();
        if (field.StartsWith("{{") && field.EndsWith("}}"))
        {
            var inner = field[2..^2].Trim();
            var dot = inner.IndexOf('.');
            field = dot >= 0 ? inner[(dot + 1)..] : inner;
        }
        JsonNode? value = field == "raw_record" ? item : item is JsonObject obj && obj.TryGetPropertyValue(field, out var found) ? found : null;
        var present = field == "raw_record" ? item != null : item is JsonObject o && o.ContainsKey(field);
        var text = value switch { null => "", JsonValue v when v.TryGetValue<string>(out var s) => s, _ => value.ToJsonString() };
        var expected = ResolveValue(rule.Value, resolve);
        var condition = rule.Condition!.Trim().ToLowerInvariant();
        bool hasValue = present && value != null && text.Length > 0 && !(value is JsonArray a && a.Count == 0) && !(value is JsonObject jo && jo.Count == 0);
        switch (condition)
        {
            case "has_value": return hasValue;
            case "has_no_value": return !hasValue;
        }
        if (!present || value == null) return condition.StartsWith("not_");
        return condition switch
        {
            "starts" => text.StartsWith(expected, StringComparison.Ordinal),
            "not_starts" => !text.StartsWith(expected, StringComparison.Ordinal),
            "ends" => text.EndsWith(expected, StringComparison.Ordinal),
            "not_ends" => !text.EndsWith(expected, StringComparison.Ordinal),
            "contains" => text.Contains(expected, StringComparison.Ordinal),
            "not_contains" => !text.Contains(expected, StringComparison.Ordinal),
            "regex" => SafeRegex(text, expected),
            "not_regex" => !SafeRegex(text, expected),
            "equals" => AreEqual(value, text, expected),
            "not_equals" => !AreEqual(value, text, expected),
            "gt" => Compare(text, expected) is > 0,
            "lt" => Compare(text, expected) is < 0,
            "between" => Between(text, expected, ResolveValue(rule.Value2, resolve)),
            "not_between" => !Between(text, expected, ResolveValue(rule.Value2, resolve)),
            _ => throw new PipelineNonRetryableException($"Validation error: unsupported filter condition '{rule.Condition}'")
        };
    }

    private static bool SafeRegex(string text, string pattern)
    {
        try { return Regex.IsMatch(text, pattern, RegexOptions.None, TimeSpan.FromSeconds(1)); }
        catch (ArgumentException) { throw new PipelineNonRetryableException($"Validation error: invalid regular expression '{pattern}'"); }
    }

    private static bool AreEqual(JsonNode value, string text, string expected)
    {
        if (value is JsonValue v)
        {
            if (v.TryGetValue<bool>(out var b)) return bool.TryParse(expected.Trim(), out var eb) ? b == eb : string.Equals(text, expected, StringComparison.OrdinalIgnoreCase);
            if (v.GetValueKind() == JsonValueKind.Number && decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var n)
                && decimal.TryParse(expected.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var en)) return n == en;
        }
        return string.Equals(text, expected, StringComparison.Ordinal);
    }

    private static int? Compare(string text, string expected)
    {
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) &&
            decimal.TryParse(expected.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var b)) return a.CompareTo(b);
        if (DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var da) &&
            DateTime.TryParse(expected, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var db)) return da.CompareTo(db);
        return string.CompareOrdinal(text, expected);
    }

    private static bool Between(string text, string low, string high) => Compare(text, low) is >= 0 && Compare(text, high) is <= 0;
}
