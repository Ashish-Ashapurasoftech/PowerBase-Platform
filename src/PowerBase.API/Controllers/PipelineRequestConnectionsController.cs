using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.DataProtection;
using PowerBase.API.Attributes;
using PowerBase.API.Pipelines;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Constants;
using PowerBase.Domain.Entities;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PowerBase.API.Controllers;

[ApiController]
[Route("pipelines/request-connections")]
public sealed class PipelineRequestConnectionsController : ControllerBase
{
    private readonly IPipelineRepository _repository;
    private readonly IQueryContext _identity;
    private readonly RequestConnectionService _credentials;
    private readonly IDataProtector _state;
    private readonly IAppAccessService _appAccess;
    public PipelineRequestConnectionsController(IPipelineRepository repository,IQueryContext identity,RequestConnectionService credentials,IDataProtectionProvider protection,IAppAccessService appAccess)
    { _repository=repository; _identity=identity; _credentials=credentials; _state=protection.CreateProtector("PowerBase.HttpOAuthState.v1"); _appAccess=appAccess; }
    public sealed record ConnectRequest(Guid PipelineId, MakeRequestDefinition Config);
    private sealed record State(Guid ConnectionId,long TenantId,long UserId,string Nonce,DateTimeOffset ExpiresAt);
    [HttpPost] [RequireAuth]
    public async Task<IActionResult> Connect(ConnectRequest input,CancellationToken ct)
    {
        // The pipeline ID is in the body; resolve its app permission just as pipeline editing does.
        await _appAccess.RequirePermissionByPipelinePublicIdAsync(input.PipelineId, PermissionCodes.PowerFlowsUpdate, ct);
        var pipeline=await _repository.GetByPublicIdAsync(input.PipelineId,ct);
        if(pipeline==null || pipeline.IsDeleted || pipeline.CreatedBy!=_identity.UserId) return StatusCode(403);
        input.Config.RequestMode="http";
        try
        {
            input.Config.ValidateConnection();
            // Step paths and query parameters may depend on earlier pipeline outputs.
            MakeRequestExecutor.BuildUrl(new MakeRequestDefinition { BaseUrl = input.Config.BaseUrl, Url = input.Config.Url }, value => value ?? "");
        }
        catch (Exception) { return BadRequest(new {error="Invalid connection configuration. Check the URL and required authentication fields."}); }
        var credentials=new RequestConnectionService.Credentials { Config=input.Config,Connected=true };
        var oauth=input.Config.AuthType=="OAuth 2.0" && input.Config.OAuthGrantType!="Client credentials";
        if(oauth)
        {
            if(!Uri.TryCreate(input.Config.OAuthAuthEndpoint,UriKind.Absolute,out var endpoint) || endpoint.Scheme is not ("https" or "http")) return BadRequest(new {error="A valid OAuth authorization endpoint is required."});
            credentials.Connected=false;
            credentials.Nonce=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            credentials.Verifier=Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            credentials.RedirectUri=$"{Request.Scheme}://{Request.Host}{Request.PathBase}/pipelines/request-connections/oauth/callback";
        }
        var row=new PipelineConnection {PipelineId=pipeline.Id,Name=input.Config.AccountName ?? "HTTP account",Type="http-request",CredentialsJson=_credentials.Protect(credentials),CreatedBy=_identity.UserId};
        var existing = (await _repository.GetConnectionsByPipelineIdAsync(pipeline.Id, ct)).FirstOrDefault(item =>
            item.Type == "http-request" && item.CreatedBy == _identity.UserId && !item.IsDeleted && item.Name == row.Name &&
            _credentials.Read(item).Config.AuthType == input.Config.AuthType && _credentials.Read(item).Config.BaseUrl == input.Config.BaseUrl);
        (Guid PublicId, long Id) created;
        if (existing == null) created = await _repository.CreateConnectionAsync(row, ct: ct);
        else
        {
            existing.CredentialsJson = row.CredentialsJson;
            await _repository.UpdateConnectionAsync(existing, ct: ct);
            created = (existing.PublicId, existing.Id);
        }
        string? authorizationUrl=null;
        if(oauth)
        {
            var state=_state.Protect(JsonSerializer.Serialize(new State(created.PublicId,_identity.TenantId,_identity.UserId,credentials.Nonce!,DateTimeOffset.UtcNow.AddMinutes(10))));
            var challenge=Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(credentials.Verifier!))).TrimEnd('=').Replace('+','-').Replace('/','_');
            var parameters=new Dictionary<string,string> { ["client_id"]=input.Config.OAuthClientId??"",["redirect_uri"]=credentials.RedirectUri!,["response_type"]="code",["state"]=state,["code_challenge"]=challenge,["code_challenge_method"]="S256",["scope"]=input.Config.OAuthScope??"" };
            authorizationUrl=input.Config.OAuthAuthEndpoint+(input.Config.OAuthAuthEndpoint!.Contains('?')?"&":"?")+string.Join("&",parameters.Select(x=>Uri.EscapeDataString(x.Key)+"="+Uri.EscapeDataString(x.Value)));
        }
        return Ok(new {connectionId=created.PublicId,connected=credentials.Connected,authorizationUrl,redirectUri=credentials.RedirectUri});
    }
    [HttpPost("schema")] [RequireAuth]
    public IActionResult Schema(MakeRequestDefinition config)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(config.SchemaSample)) return Ok(new { sample = new { } });
            if (config.SchemaSample.Length > MakeRequestExecutor.MaxBytes) return BadRequest(new {error="Schema sample exceeds 10 MB."});
            var json = (config.SchemaSampleType ?? "JSON sample").StartsWith("YAML")
                ? JsonSerializer.Serialize(new YamlDotNet.Serialization.DeserializerBuilder().Build().Deserialize<object>(config.SchemaSample)) : config.SchemaSample;
            using var parsed = JsonDocument.Parse(json);
            return Ok(new { sample = parsed.RootElement.Clone() });
        }
        catch { return BadRequest(new { error="Invalid schema or sample." }); }
    }
    [HttpGet("{id:guid}")] [RequireAuth]
    public async Task<IActionResult> Status(Guid id,CancellationToken ct)
    {
        var row=await _repository.GetConnectionByPublicIdAsync(id,ct);
        if(row==null || row.CreatedBy!=_identity.UserId || row.IsDeleted || row.Type!="http-request") return NotFound();
        return Ok(new {connected=_credentials.Read(row).Connected});
    }
    [HttpGet("oauth/callback")]
    public async Task<IActionResult> Callback(string state,string? code,string? error,CancellationToken ct)
    {
        State ticket;
        try { ticket=JsonSerializer.Deserialize<State>(_state.Unprotect(state))!; }
        catch { return BadRequest("Invalid OAuth state."); }
        if(ticket.ExpiresAt<DateTimeOffset.UtcNow || string.IsNullOrEmpty(code) || error!=null) return BadRequest("OAuth authorization expired or was denied. Reconnect from the PowerFlow.");
        _identity.SetTenantId(ticket.TenantId);
        var row=await _repository.GetConnectionByPublicIdAsync(ticket.ConnectionId,ct);
        if(row==null || row.IsDeleted || row.CreatedBy!=ticket.UserId || row.Type!="http-request") return BadRequest("Invalid OAuth connection.");
        var credentials=_credentials.Read(row);
        if(credentials.Nonce!=ticket.Nonce || credentials.Connected) return BadRequest("OAuth state has already been used.");
        await _credentials.ExchangeAsync(credentials,new(){["grant_type"]="authorization_code",["code"]=code,["redirect_uri"]=credentials.RedirectUri!,["code_verifier"]=credentials.Verifier!},ct);
        credentials.Nonce=null; credentials.Verifier=null;
        _identity.SetUserIdentity(ticket.UserId, false, "", "", new HashSet<string>(), "");
        row.CredentialsJson=_credentials.Protect(credentials);
        await _repository.UpdateConnectionAsync(row,ct:ct);
        return Content("Account connected. You can close this window and return to the PowerFlow.","text/plain");
    }
}
