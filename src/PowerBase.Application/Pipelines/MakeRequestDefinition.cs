using System.Text.Json;

namespace PowerBase.Application.Pipelines;

public sealed class MakeRequestDefinition
{
    public string? RequestMode { get; set; }
    public Guid? HttpConnectionId { get; set; }
    public string? AccountName { get; set; }
    public string? OAuthAuthEndpoint { get; set; }
    public string? Connection { get; set; }
    public string? ConnectionPublicId { get; set; }
    public string? Url { get; set; }
    public string? BaseUrl { get; set; }
    public string? Path { get; set; }
    public string Method { get; set; } = "GET";
    public List<RequestHeader> HeadersList { get; set; } = new();
    public List<RequestHeader> ConnectionHeaders { get; set; } = new();
    public List<RequestHeader> StepHeaders { get; set; } = new();
    public List<RequestHeader> QueryParams { get; set; } = new();
    public string? ContentType { get; set; }
    public string? Body { get; set; }
    public string Encoding { get; set; } = "utf-8";
    public string DisableSsl { get; set; } = "No";
    public string AuthType { get; set; } = "No Authentication";
    public string? Username { get; set; }
    public string? Password { get; set; }
    public string? BearerToken { get; set; }
    public string? ApiKeyName { get; set; }
    public string? ApiKeyValue { get; set; }
    public string ApiKeyPlacement { get; set; } = "Header";
    public string? JwtAlg { get; set; }
    public string? JwtSigningKey { get; set; }
    public string JwtHeaders { get; set; } = "{}";
    public string JwtClaims { get; set; } = "{}";
    public bool JwtUseIat { get; set; }
    public string JwtExp { get; set; } = "5m";
    public string OAuthGrantType { get; set; } = "Authorization code";
    public string? OAuthTokenEndpoint { get; set; }
    public string? OAuthClientId { get; set; }
    public string? OAuthClientSecret { get; set; }
    public string? OAuthScope { get; set; }
    public string? OAuthClientAuth { get; set; }
    public string? OAuthAccessToken { get; set; }
    public string? OAuthRefreshToken { get; set; }
    public string? ExpectedPayloadType { get; set; }
    public string SchemaSampleType { get; set; } = "JSON sample";
    public string? SchemaSample { get; set; }
    public string ValidateResponsePayload { get; set; } = "Yes";
    public string ErrorsOption { get; set; } = "automatic";
    public string? ExemptErrorStatuses { get; set; }
    public bool IsPowerBase => RequestMode == "quickbase";
    public void Validate()
    {
        if (RequestMode is not (null or "http" or "quickbase")) throw new InvalidOperationException("Invalid request mode.");
        var url = IsPowerBase ? Url : string.IsNullOrWhiteSpace(BaseUrl) ? Url : BaseUrl;
        if (string.IsNullOrWhiteSpace(url)) throw new InvalidOperationException("Request URL is required.");
        if (IsPowerBase && !Guid.TryParse(ConnectionPublicId ?? Connection, out _)) throw new InvalidOperationException("A PowerBase account is required.");
        if (!string.IsNullOrWhiteSpace(Method) && !Method.Contains("{{") && !new[] {"GET","POST","PUT","PATCH","DELETE","HEAD","OPTIONS"}.Contains(Method.ToUpperInvariant())) throw new InvalidOperationException("Invalid request method.");
        // HTTP-only options must not invalidate the account-based Make Request action.
        if (IsPowerBase) return;
        if (string.IsNullOrWhiteSpace(ExpectedPayloadType)) ExpectedPayloadType = "JSON";
        if (string.IsNullOrWhiteSpace(SchemaSampleType)) SchemaSampleType = "JSON sample";
        if (string.IsNullOrWhiteSpace(ErrorsOption)) ErrorsOption = "automatic";
        if (ErrorsOption is not ("automatic" or "custom" or "none")) throw new InvalidOperationException("Invalid error handling option.");
        if (ErrorsOption == "custom" && !System.Text.RegularExpressions.Regex.IsMatch(ExemptErrorStatuses ?? "", @"^\s*[45]\d{2}(\s*,\s*[45]\d{2})*\s*$")) throw new InvalidOperationException("Specify comma-separated error status codes between 400 and 599.");
        if (!IsPowerBase && ExpectedPayloadType is not (null or "JSON" or "YAML" or "TEXT" or "Raw" or "XML")) throw new InvalidOperationException("Invalid response payload type.");
        if (!string.IsNullOrWhiteSpace(SchemaSample) && ExpectedPayloadType is not ("TEXT" or "Raw"))
        {
            if (SchemaSampleType.StartsWith("YAML")) new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<object>(SchemaSample);
            else if (SchemaSampleType.StartsWith("XML")) System.Xml.Linq.XDocument.Parse(SchemaSample);
            else { using var sample = JsonDocument.Parse(SchemaSample); }
        }
    }
    public void ValidateConnection()
    {
        if (string.IsNullOrWhiteSpace(AccountName)) throw new InvalidOperationException("Account name is required.");
        string Need(string? value,string field) => string.IsNullOrWhiteSpace(value) ? throw new InvalidOperationException(field + " is required.") : value;
        switch (AuthType)
        {
            case "No Authentication": break;
            case "Basic Authentication": Need(Username,"Username"); Need(Password,"Password"); break;
            case "Bearer Token": Need(BearerToken,"Bearer token"); break;
            case "API Key": Need(ApiKeyName,"API key name"); Need(ApiKeyValue,"API key value"); break;
            case "JWT": MakeRequestExecutor.CreateJwt(this, value=>value??""); break;
            case "OAuth 2.0":
                Need(OAuthClientId,"OAuth client ID"); Need(OAuthTokenEndpoint,"Token endpoint");
                if (OAuthGrantType == "Authorization code") Need(OAuthAuthEndpoint,"Authorization endpoint");
                else if (OAuthGrantType != "Client credentials") throw new InvalidOperationException("Invalid OAuth grant type.");
                break;
            default: throw new InvalidOperationException("Unsupported authentication type.");
        }
    }
    public static MakeRequestDefinition Read(string json) => JsonSerializer.Deserialize<MakeRequestDefinition>(json,
        new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ?? throw new InvalidOperationException("Invalid Make Request configuration.");
}

public sealed class RequestHeader
{
    public string? Key { get; set; }
    public string? Name { get; set; }
    [System.Text.Json.Serialization.JsonConverter(typeof(RequestValueConverter))]
    public string? Value { get; set; }
    public string HeaderName => Key ?? Name ?? "";
}

public sealed class RequestValueConverter : System.Text.Json.Serialization.JsonConverter<string?>
{
    public override string? Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options)
    {
        using var value = JsonDocument.ParseValue(ref reader);
        return value.RootElement.ValueKind == JsonValueKind.String ? value.RootElement.GetString()
            : value.RootElement.ValueKind == JsonValueKind.Null ? null : value.RootElement.GetRawText();
    }
    public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) => writer.WriteStringValue(value);
}
