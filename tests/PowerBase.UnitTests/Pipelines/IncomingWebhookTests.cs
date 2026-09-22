using System.Net;
using System.Security.Cryptography;
using System.IdentityModel.Tokens.Jwt;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using NSubstitute;
using PowerBase.API.Controllers;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Entities;

namespace PowerBase.UnitTests.Pipelines;

public class IncomingWebhookTests
{
    private readonly Guid tenant = Guid.NewGuid(), stepId = Guid.NewGuid();
    private readonly IPipelineRepository repo = Substitute.For<IPipelineRepository>();
    private readonly IPipelineExecutionQueue queue = Substitute.For<IPipelineExecutionQueue>();
    private readonly DefaultHttpContext http = new();
    private readonly PipelineStep step = new() { Id = 42, PipelineId = 7, RefId = "a", Type = "trigger", Subtype = "webhook", ConfigJson = "{}" };
    private readonly Pipeline pipeline = new() { Id = 7, CreatedBy = 19, IsActive = true };
    private readonly WebhookController controller;
    public IncomingWebhookTests()
    {
        var admin = Substitute.For<IAdminRepository>();
        admin.GetTenantIdByPublicIdAsync(tenant, Arg.Any<CancellationToken>()).Returns(1L);
        repo.GetStepByPublicIdAsync(stepId, Arg.Any<CancellationToken>()).Returns(step);
        repo.GetByIdAsync(7, Arg.Any<CancellationToken>()).Returns(pipeline);
        http.Request.Method = "POST";
        http.Connection.RemoteIpAddress = IPAddress.Parse("127.0.0.1");
        controller = new WebhookController(admin, repo, queue, Substitute.For<IQueryContext>(), Substitute.For<ILogger<WebhookController>>()) { ControllerContext = new ControllerContext { HttpContext = http } };
    }
    private Task<IActionResult> Send(string body = "")
    {
        http.Request.Body = new MemoryStream(Encoding.UTF8.GetBytes(body));
        return controller.ExecuteWebhook(tenant, stepId, CancellationToken.None);
    }
    private PipelineExecutionTask Queued() => (PipelineExecutionTask)queue.ReceivedCalls().Last().GetArguments()[0]!;

    [Theory]
    [InlineData("GET")][InlineData("POST")][InlineData("PUT")][InlineData("DELETE")][InlineData("HEAD")][InlineData("OPTIONS")]
    public async Task AcceptsSupportedMethods(string method)
    {
        http.Request.Method = method;
        Assert.IsType<OkObjectResult>(await Send());
        using var payload = JsonDocument.Parse(Queued().TriggerPayloadJson);
        Assert.Equal(method, payload.RootElement.GetProperty("method").GetString());
        Assert.Equal(19, Queued().TriggeredBy);
    }
    [Fact]
    public async Task RecordsNewQueryStringKeysForThePipelineBuilderToOfferLater()
    {
        http.Request.QueryString = new QueryString("?Test1=a&Test2=b");
        Assert.IsType<OkObjectResult>(await Send());
        await repo.Received(1).UpdateStepConfigJsonAsync(42,
            Arg.Is<string>(json => json.Contains("\"Test1\"") && json.Contains("\"Test2\"") && json.Contains("sampleUrlParamKeys")),
            Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task DoesNotRewriteConfigWhenNoNewQueryStringKeysAppear()
    {
        step.ConfigJson = JsonSerializer.Serialize(new { sampleUrlParamKeys = new[] { "Test1" } });
        http.Request.QueryString = new QueryString("?Test1=a");
        Assert.IsType<OkObjectResult>(await Send());
        await repo.DidNotReceive().UpdateStepConfigJsonAsync(Arg.Any<long>(), Arg.Any<string>(), Arg.Any<byte[]>(), Arg.Any<CancellationToken>());
    }
    [Fact]
    public async Task RejectsPatchOutsideQuickbaseMethodList()
    {
        http.Request.Method = "PATCH";
        var result = Assert.IsType<StatusCodeResult>(await Send());
        Assert.Equal(405, result.StatusCode);
        queue.DidNotReceive().QueueTask(Arg.Any<PipelineExecutionTask>());
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task AcceptsOnPremisesAgentParitySetting(bool enabled)
    {
        step.ConfigJson = JsonSerializer.Serialize(new { proxyViaOnPremisesAgent = enabled });
        Assert.IsType<OkObjectResult>(await Send("{}"));
    }
    [Theory]
    [InlineData("hello")][InlineData("<root>value</root>")][InlineData("[1,2]")][InlineData("null")][InlineData("")]
    public async Task PreservesNonObjectBodies(string body)
    {
        Assert.IsType<OkObjectResult>(await Send(body));
        using var payload = JsonDocument.Parse(Queued().TriggerPayloadJson);
        Assert.Equal(body, payload.RootElement.GetProperty("body").GetString());
        Assert.Equal(42, payload.RootElement.GetProperty("TriggerStepId").GetInt64());
    }
    [Fact]
    public async Task ExportsMetadataAndIsolatesUntrustedBodyKeys()
    {
        http.Request.QueryString = new QueryString("?tag=one&tag=two&name=hello%20world");
        http.Request.Headers["X-Test"] = "ok";
        Assert.IsType<OkObjectResult>(await Send("{\"steps\":{},\"TriggerStepId\":999,\"_CreatedBy\":1000}"));
        using var payload = JsonDocument.Parse(Queued().TriggerPayloadJson);
        var root = payload.RootElement;
        Assert.Equal(42, root.GetProperty("TriggerStepId").GetInt64());
        Assert.False(root.TryGetProperty("_CreatedBy", out _));
        Assert.Equal(999, root.GetProperty("json").GetProperty("TriggerStepId").GetInt64());
        Assert.Equal(2, root.GetProperty("url_params").GetProperty("tag").GetArrayLength());
        Assert.Equal("hello world", root.GetProperty("url_params").GetProperty("name").GetString());
        Assert.Equal("127.0.0.1", root.GetProperty("origin_ip").GetString());
        Assert.Contains(root.GetProperty("headers").EnumerateArray(), h => h.GetProperty("name").GetString() == "X-Test");
    }
    [Fact]
    public async Task IndependentIdenticalRequestsAreNotDeduplicated()
    {
        await Send("{}"); var first = Queued().MessageId;
        await Send("{}"); Assert.NotEqual(first, Queued().MessageId);
    }
    [Fact]
    public async Task ProviderRetryKeepsIdentityEvenWhenPayloadChanges()
    {
        http.Request.Headers["X-PowerBase-Webhook-Id"] = "event-42";
        await Send("{}"); var first = Queued().MessageId;
        await Send("{\"changed\":true}"); Assert.Equal(first, Queued().MessageId);
    }
    [Fact]
    public async Task WrongMethodAndDisabledPipelineNeverQueue()
    {
        step.ConfigJson = "{\"methodType\":\"PUT\"}";
        Assert.IsType<NoContentResult>(await Send());
        step.ConfigJson = "{}"; pipeline.IsActive = false;
        Assert.IsType<NoContentResult>(await Send());
        queue.DidNotReceive().QueueTask(Arg.Any<PipelineExecutionTask>());
    }
    [Theory]
    [InlineData("{bad")][InlineData("{\"authType\":\"unsupported\"}")][InlineData("{\"methodType\":\"TRACE\"}")]
    public async Task InvalidConfigurationFailsClosed(string config)
    {
        step.ConfigJson = config;
        Assert.IsType<BadRequestObjectResult>(await Send());
        queue.DidNotReceive().QueueTask(Arg.Any<PipelineExecutionTask>());
    }
    [Fact]
    public async Task OversizedChunkedBodyDoesNotQueue()
    {
        var result = Assert.IsType<StatusCodeResult>(await Send(new string('x', 1048577)));
        Assert.Equal(413, result.StatusCode);
        queue.DidNotReceive().QueueTask(Arg.Any<PipelineExecutionTask>());
    }
    [Theory]
    [InlineData("RS256")][InlineData("RS384")][InlineData("RS512")]
    public async Task ValidJwtExportsVerifiedClaims(string algorithm)
    {
        using var key = RSA.Create(2048);
        step.ConfigJson = JsonSerializer.Serialize(new { authType = "jwt", publicKey = key.ExportSubjectPublicKeyInfoPem(), jwtAlgorithm = algorithm });
        var token = new JwtSecurityToken(claims: new[] { new System.Security.Claims.Claim("customer", "42") }, expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(new RsaSecurityKey(key), algorithm));
        http.Request.Headers.Authorization = "Bearer " + new JwtSecurityTokenHandler().WriteToken(token);
        Assert.IsType<OkObjectResult>(await Send("{}"));
        using var payload = JsonDocument.Parse(Queued().TriggerPayloadJson);
        Assert.Equal("42", payload.RootElement.GetProperty("jwt_payload").GetProperty("customer").GetString());
    }
    [Theory]
    [InlineData("missing")][InlineData("malformed")][InlineData("wrong-key")][InlineData("expired")][InlineData("wrong-algorithm")]
    public async Task InvalidJwtDoesNotQueue(string mode)
    {
        using var key = RSA.Create(2048); using var other = RSA.Create(2048);
        step.ConfigJson = JsonSerializer.Serialize(new { authType = "jwt", publicKey = key.ExportSubjectPublicKeyInfoPem(), jwtAlgorithm = "RS256" });
        var token = new JwtSecurityToken(expires: mode == "expired" ? DateTime.UtcNow.AddMinutes(-5) : DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(new RsaSecurityKey(mode == "wrong-key" ? other : key), mode == "wrong-algorithm" ? "RS512" : "RS256"));
        if (mode != "missing") http.Request.Headers.Authorization = "Bearer " + (mode == "malformed" ? "bad" : new JwtSecurityTokenHandler().WriteToken(token));
        Assert.IsType<UnauthorizedObjectResult>(await Send());
        queue.DidNotReceive().QueueTask(Arg.Any<PipelineExecutionTask>());
    }
    [Fact]
    public async Task FiltersJsonWithAndOrAndHeaders()
    {
        step.ConfigJson = JsonSerializer.Serialize(new { conditions = new[] {
            new { rules = new[] { new { field = "json", path = "status", @operator = "is", value = "ready" }, new { field = "headers", path = "", @operator = "contains", value = "yes" } } },
            new { rules = new[] { new { field = "method", path = "", @operator = "is", value = "GET" } } }
        } });
        Assert.IsType<NoContentResult>(await Send("{\"status\":\"ready\"}"));
        http.Request.Headers["X-Test"] = "yes";
        Assert.IsType<OkObjectResult>(await Send("{\"status\":\"ready\"}"));
        http.Request.Method = "GET";
        Assert.IsType<OkObjectResult>(await Send());
    }
    [Fact]
    public async Task FiltersTheQuickbaseHeadersCollection()
    {
        step.ConfigJson = JsonSerializer.Serialize(new { conditions = new[] {
            new { rules = new[] { new { field = "headers", @operator = "contains", value = "X-Test" } } }
        } });
        Assert.IsType<NoContentResult>(await Send("{}"));
        http.Request.Headers["X-Test"] = "yes";
        Assert.IsType<OkObjectResult>(await Send("{}"));
    }
    [Fact]
    public async Task AdvancedExpressionFiltersOnParsedRequest()
    {
        step.ConfigJson = "{\"conditions\":[{\"rules\":[{\"field\":\"expression\",\"value\":\"a.json.count > 2\"}]}]}";
        Assert.IsType<NoContentResult>(await Send("{\"count\":1}"));
        Assert.IsType<OkObjectResult>(await Send("{\"count\":3}"));
    }

    [Theory]
    [InlineData("ES256", 256)]
    [InlineData("ES384", 384)]
    [InlineData("ES512", 521)]
    public async Task ValidEllipticCurveJwtExportsVerifiedClaims(string algorithm, int size)
    {
        using var key = ECDsa.Create(size == 256 ? ECCurve.NamedCurves.nistP256 : size == 384 ? ECCurve.NamedCurves.nistP384 : ECCurve.NamedCurves.nistP521);
        step.ConfigJson = JsonSerializer.Serialize(new { authType = "jwt", publicKey = key.ExportSubjectPublicKeyInfoPem(), jwtAlgorithm = algorithm });
        var token = new JwtSecurityToken(claims: new[] { new System.Security.Claims.Claim("customer", "42") }, expires: DateTime.UtcNow.AddMinutes(5), signingCredentials: new SigningCredentials(new ECDsaSecurityKey(key), algorithm));
        http.Request.Headers.Authorization = "Bearer " + new JwtSecurityTokenHandler().WriteToken(token);
        Assert.IsType<OkObjectResult>(await Send("{}"));
        using var payload = JsonDocument.Parse(Queued().TriggerPayloadJson);
        Assert.Equal("42", payload.RootElement.GetProperty("jwt_payload").GetProperty("customer").GetString());
    }

    [Fact]
    public async Task NullRuleReturnsBadRequestWithoutQueuing()
    {
        step.ConfigJson = "{\"conditions\":[{\"rules\":[null]}]}";
        Assert.IsType<BadRequestObjectResult>(await Send());
        queue.DidNotReceive().QueueTask(Arg.Any<PipelineExecutionTask>());
    }

    [Fact]
    public async Task SavedEditorFiltersControlActualRequestDispatch()
    {
        step.ConfigJson = """
            {"conditions":[],"filterGroups":[{"rules":[
              {"field":"json","path":"status","operator":"is","value":"ready"},
              {"type":"nested","groups":[]}
            ]}]}
            """;
        Assert.IsType<NoContentResult>(await Send("{\"status\":\"pending\"}"));
        queue.DidNotReceive().QueueTask(Arg.Any<PipelineExecutionTask>());
        Assert.IsType<OkObjectResult>(await Send("{\"status\":\"ready\"}"));
        queue.Received(1).QueueTask(Arg.Any<PipelineExecutionTask>());
    }

    [Fact]
    public async Task SuccessfulRequestReturnsQuickbaseCompatible200AndCompleteOutputShape()
    {
        http.Request.ContentType = "application/json; charset=utf-8";
        http.Request.QueryString = new QueryString("?single=one&multi=one&multi=two");
        http.Request.Headers.Append("X-Multi", new[] { "first", "second" });
        var result = Assert.IsType<OkObjectResult>(await Send("{\"ok\":true}"));
        Assert.Equal(StatusCodes.Status200OK, result.StatusCode);
        using var payload = JsonDocument.Parse(Queued().TriggerPayloadJson);
        var root = payload.RootElement;
        Assert.Equal("application/json; charset=utf-8", root.GetProperty("content_type").GetString());
        Assert.Equal("one", root.GetProperty("url_params").GetProperty("single").GetString());
        Assert.Equal(2, root.GetProperty("url_params").GetProperty("multi").GetArrayLength());
        Assert.Equal(2, root.GetProperty("headers").EnumerateArray().Count(h => h.GetProperty("name").GetString() == "X-Multi"));
        Assert.True(root.GetProperty("json").GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Null, root.GetProperty("jwt_payload").ValueKind);
    }

    [Fact]
    public async Task InvalidRegexReturnsBadRequestAndNeverQueues()
    {
        step.ConfigJson = """{"conditions":[{"rules":[{"field":"body","operator":"matches-regex","value":"["}]}]}""";
        var result = Assert.IsType<BadRequestObjectResult>(await Send("anything"));
        Assert.Equal(StatusCodes.Status400BadRequest, result.StatusCode);
        queue.DidNotReceive().QueueTask(Arg.Any<PipelineExecutionTask>());
    }

    [Theory]
    [InlineData("starts-with", "ord", true)]
    [InlineData("not-starts-with", "fail", true)]
    [InlineData("ends-with", "123", true)]
    [InlineData("not-ends-with", "999", true)]
    [InlineData("matches-regex", "^order-[0-9]+$", true)]
    [InlineData("not-matches-regex", "^fail", true)]
    public async Task NewQuickbaseOperatorsControlRealDispatch(string op, string value, bool shouldQueue)
    {
        step.ConfigJson = JsonSerializer.Serialize(new { conditions = new[] { new { rules = new[] {
            new { field = "body", @operator = op, value }
        } } } });
        var result = await Send("order-123");
        Assert.Equal(shouldQueue, result is OkObjectResult);
        if (shouldQueue) queue.Received(1).QueueTask(Arg.Any<PipelineExecutionTask>());
        else queue.DidNotReceive().QueueTask(Arg.Any<PipelineExecutionTask>());
    }

    [Fact]
    public async Task FailedAndBranchDoesNotFallThroughToLaterRulesOrQueue()
    {
        step.ConfigJson = JsonSerializer.Serialize(new { conditions = new[] { new { rules = new[] {
            new { field = "method", path = "", @operator = "is", value = "POST" },
            new { field = "json", path = "status", @operator = "is", value = "ready" },
            new { field = "origin_ip", path = "", @operator = "starts-with", value = "127." }
        } } } });
        Assert.IsType<NoContentResult>(await Send("{\"status\":\"pending\"}"));
        queue.DidNotReceive().QueueTask(Arg.Any<PipelineExecutionTask>());
    }
}
