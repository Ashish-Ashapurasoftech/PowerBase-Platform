using Microsoft.AspNetCore.DataProtection;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;
using System.Text.Json;
using System.Net.Http.Headers;
using System.Text;
using System.Collections.Concurrent;

namespace PowerBase.API.Pipelines;

public sealed class RequestConnectionService : IRequestConnectionService
{
    private readonly IPipelineRepository _repository;
    private readonly IDataProtector _protector;
    private readonly IHttpClientFactory _clients;
    private static readonly ConcurrentDictionary<Guid, SemaphoreSlim> Locks = new();
    public RequestConnectionService(IPipelineRepository repository, IDataProtectionProvider protection, IHttpClientFactory clients)
    { _repository=repository; _protector=protection.CreateProtector("PowerBase.PipelineHttpCredentials.v1"); _clients=clients; }
    public sealed class Credentials
    {
        public MakeRequestDefinition Config { get; set; } = new();
        public string? Nonce { get; set; }
        public string? Verifier { get; set; }
        public string? RedirectUri { get; set; }
        public DateTimeOffset ExpiresAt { get; set; }
        public bool Connected { get; set; }
    }
    public string Protect(Credentials value) => JsonSerializer.Serialize(new { encrypted = _protector.Protect(JsonSerializer.Serialize(value)) });
    public Credentials Read(PipelineConnection connection)
    {
        using var wrapper=JsonDocument.Parse(connection.CredentialsJson);
        return JsonSerializer.Deserialize<Credentials>(_protector.Unprotect(wrapper.RootElement.GetProperty("encrypted").GetString()!))!;
    }
    public async Task<MakeRequestDefinition> ResolveAsync(Guid connectionId, long pipelineId, long ownerId, CancellationToken ct)
    {
        var gate=Locks.GetOrAdd(connectionId,_=>new SemaphoreSlim(1,1)); await gate.WaitAsync(ct);
        try
        {
            var row=await _repository.GetConnectionByPublicIdAsync(connectionId,ct);
            if(row==null || row.IsDeleted || row.Type!="http-request" || row.CreatedBy!=ownerId || row.PipelineId!=pipelineId)
                throw new UnauthorizedAccessException("HTTP connection is not available to this pipeline owner.");
            var credentials=Read(row);
            if(!credentials.Connected) throw new InvalidOperationException("Connect the HTTP account before running this step.");
            if(credentials.Config.AuthType=="OAuth 2.0" && credentials.Config.OAuthGrantType!="Client credentials" && credentials.ExpiresAt<=DateTimeOffset.UtcNow.AddSeconds(30))
            {
                if(string.IsNullOrEmpty(credentials.Config.OAuthRefreshToken)) throw new InvalidOperationException("OAuth authorization has expired. Reconnect the account.");
                await ExchangeAsync(credentials,new(){["grant_type"]="refresh_token",["refresh_token"]=credentials.Config.OAuthRefreshToken},ct);
                row.CredentialsJson=Protect(credentials); await _repository.UpdateConnectionAsync(row,ct:ct);
            }
            return credentials.Config;
        }
        finally { gate.Release(); }
    }
    public async Task ExchangeAsync(Credentials credentials, Dictionary<string,string> form, CancellationToken ct)
    {
        var c=credentials.Config;
        if(!Uri.TryCreate(c.OAuthTokenEndpoint,UriKind.Absolute,out var endpoint) || endpoint.Scheme is not ("https" or "http")) throw new InvalidOperationException("Invalid OAuth token endpoint.");
        using var client=_clients.CreateClient("PipelineMakeRequest");
        using var request=new HttpRequestMessage(HttpMethod.Post,endpoint);
        if(c.OAuthClientAuth?.Contains("body",StringComparison.OrdinalIgnoreCase)==true)
        { form["client_id"]=c.OAuthClientId ?? ""; if(!string.IsNullOrEmpty(c.OAuthClientSecret)) form["client_secret"]=c.OAuthClientSecret; }
        else request.Headers.Authorization=new AuthenticationHeaderValue("Basic",Convert.ToBase64String(Encoding.UTF8.GetBytes(c.OAuthClientId+":"+c.OAuthClientSecret)));
        request.Content=new FormUrlEncodedContent(form);
        using var response=await client.SendAsync(request,ct);
        if(!response.IsSuccessStatusCode) throw new InvalidOperationException("OAuth token exchange failed. Check the account configuration.");
        using var json=JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        c.OAuthAccessToken=json.RootElement.GetProperty("access_token").GetString();
        if(json.RootElement.TryGetProperty("refresh_token",out var refresh)) c.OAuthRefreshToken=refresh.GetString();
        credentials.ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(json.RootElement.TryGetProperty("expires_in",out var expires) && double.TryParse(expires.ToString(),out var seconds) ? seconds : 3600);
        credentials.Connected=true;
    }
}
