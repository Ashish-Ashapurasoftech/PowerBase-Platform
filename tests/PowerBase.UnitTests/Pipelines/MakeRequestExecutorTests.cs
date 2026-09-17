using System.Net;
using System.Text;
using System.Text.Json;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Pipelines;

public class MakeRequestExecutorTests
{
    private static string Resolve(string? value) => value ?? "";
    private static MakeRequestDefinition Config() => new() { RequestMode = "http", BaseUrl = "https://example.com/api", ExpectedPayloadType = "JSON" };
    private static MakeRequestExecutor Executor(Action<HttpRequestMessage>? inspect = null, int status = 200, string body = "{}") => new((request, ct) => {
        inspect?.Invoke(request);
        return Task.FromResult(new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) });
    });
    [Fact] public async Task EmptyOptionalHttpFieldsUseQuickbaseDefaults()
    {
        var c = Config(); c.Method = ""; c.ExpectedPayloadType = ""; c.SchemaSampleType = null!; c.ErrorsOption = "";
        c.SchemaSample = "{\"example\": 1}";
        var result = await Executor(r => Assert.Equal(HttpMethod.Get, r.Method), body: "{\"different\":true}").ExecuteAsync(c, Resolve, default);
        Assert.Equal("{\"different\":true}", result.OutputJson);
        await Assert.ThrowsAsync<PipelineNonRetryableException>(() => Executor(body: "invalid").ExecuteAsync(c, Resolve, default));
    }
    [Fact] public void PowerBaseValidationIgnoresHttpOnlyOptions()
    {
        var c = new MakeRequestDefinition { RequestMode = "quickbase", Connection = Guid.NewGuid().ToString(), Url = "/apps", Method = "", ErrorsOption = "custom", SchemaSample = "invalid" };
        c.Validate();
    }
    [Theory]
    [InlineData(200, "{\"items\":[1,2]}")]
    [InlineData(400, "{\"error\":\"bad input\"}")]
    [InlineData(503, "service unavailable")]
    [InlineData(200, "invalid json")]
    public async Task AuditCapturesResponseBeforeStatusAndFormatErrors(int status, string body)
    {
        var c = Config(); c.Method = "POST"; c.Body = "{\"name\":\"example\"}";
        c.AuthType = "API Key"; c.ApiKeyName = "Custom-Credential"; c.ApiKeyValue = "private-value";
        c.QueryParams.Add(new() { Key = "page", Value = "2" });
        Dictionary<string, object?>? captured = null;
        try { await Executor(status: status, body: body).ExecuteAsync(c, Resolve, default, data => captured = data); }
        catch (Exception ex) when (ex is HttpRequestException or PipelineNonRetryableException) { }
        Assert.NotNull(captured);
        Assert.Equal(status, captured["HTTPStatus"]);
        Assert.Equal(Encoding.UTF8.GetByteCount(body), Convert.ToInt32(captured["ResponseSize"]));
        Assert.True((bool)captured["ResponseBodyAvailable"]!);
        Assert.Contains("ResponseBody", captured.Keys);
        var headers = Assert.IsType<Dictionary<string, string>>(captured["Headers"]);
        Assert.Equal("[REDACTED]", headers["Custom-Credential"]);
        Assert.Equal("POST", captured["Method"]);
        Assert.Equal("2", Assert.IsType<Dictionary<string, List<string>>>(captured["QueryParameters"])["page"].Single());
    }

    [Fact] public void QueryPrecedencePreservesUnchangedValues()
    {
        var c = Config(); c.BaseUrl += "?keep=yes&override=base"; c.Path = "records?override=path";
        c.QueryParams.Add(new() { Key = "override", Value = "step value" });
        Assert.Equal("https://example.com/api/records?keep=yes&override=step%20value", MakeRequestExecutor.BuildUrl(c, Resolve).AbsoluteUri);
    }
    [Theory] [InlineData("https://evil.com/api")] [InlineData("../outside")] [InlineData("//evil.com")] public void PreventsCredentialHostEscape(string path)
    { var c = Config(); c.Path = path; Assert.Throws<InvalidOperationException>(() => MakeRequestExecutor.BuildUrl(c, Resolve)); }
    [Fact] public async Task DeleteBodyAndConnectionHeadersAreApplied()
    {
        var c = Config(); c.Method = "DELETE"; c.Body = "{\"id\":1}";
        c.ConnectionHeaders.Add(new() { Key = "X-Account", Value = "fixed" });
        c.StepHeaders.Add(new() { Key = "X-Account", Value = "override" });
        c.StepHeaders.Add(new() { Key = "X-Step", Value = "value" });
        await Executor(request => {
            Assert.Equal("fixed", request.Headers.GetValues("X-Account").Single());
            Assert.Equal("value", request.Headers.GetValues("X-Step").Single());
            Assert.Equal(c.Body, request.Content!.ReadAsStringAsync().Result);
        }).ExecuteAsync(c, Resolve, default);
    }
    [Theory] [InlineData(400)] [InlineData(401)] [InlineData(404)] public async Task ClientErrorsAreNotRetried(int status)
    { await Assert.ThrowsAsync<PipelineNonRetryableException>(() => Executor(status: status).ExecuteAsync(Config(), Resolve, default)); }
    [Theory] [InlineData(429)] [InlineData(500)] [InlineData(503)] public async Task TransientErrorsAreRetryable(int status)
    { await Assert.ThrowsAsync<HttpRequestException>(() => Executor(status: status).ExecuteAsync(Config(), Resolve, default)); }
    [Theory] [InlineData("custom")] [InlineData("none")] public async Task ExemptStatusesContinue(string option)
    { var c = Config(); c.ErrorsOption = option; c.ExemptErrorStatuses = "404"; Assert.Equal(404, (await Executor(status:404).ExecuteAsync(c, Resolve, default)).StatusCode); }
    [Fact] public async Task FormatValidationDoesNotValidateSchema()
    { var c = Config(); c.SchemaSample = "{\"missing\":0}"; Assert.Equal("{\"other\":1}", (await Executor(body:"{\"other\":1}").ExecuteAsync(c, Resolve, default)).OutputJson); }
    [Fact] public async Task TextOutputStaysAString()
    { var c = Config(); c.ExpectedPayloadType = "TEXT"; Assert.Equal("\"123\"", (await Executor(body:"123").ExecuteAsync(c, Resolve, default)).OutputJson); }
    [Fact] public async Task InvalidJsonFailsUnlessValidationDisabled()
    { var c = Config(); await Assert.ThrowsAsync<PipelineNonRetryableException>(() => Executor(body:"invalid").ExecuteAsync(c,Resolve,default)); c.ValidateResponsePayload="No"; Assert.Equal("\"invalid\"", (await Executor(body:"invalid").ExecuteAsync(c,Resolve,default)).OutputJson); }
    [Fact] public async Task YamlIsAvailableAsStructuredOutput()
    { var c=Config(); c.ExpectedPayloadType="YAML"; var result=await Executor(body:"name: hello").ExecuteAsync(c,Resolve,default); Assert.Equal("hello",JsonDocument.Parse(result.OutputJson).RootElement.GetProperty("name").GetString()); }
    [Fact] public async Task HeadDoesNotRequireJsonBody()
    { var c=Config(); c.Method="HEAD"; Assert.Equal("null",(await Executor(body:"").ExecuteAsync(c,Resolve,default)).OutputJson); }
    [Fact] public void ApiKeyQueryCannotBeOverridden()
    { var c=Config(); c.AuthType="API Key"; c.ApiKeyPlacement="Query parameter"; c.ApiKeyName="key"; c.ApiKeyValue="secret"; c.Path="records?key=override"; Assert.Throws<InvalidOperationException>(()=>MakeRequestExecutor.BuildUrl(c,Resolve)); }
    [Fact] public async Task BasicAuthenticationIsApplied()
    { var c=Config(); c.AuthType="Basic Authentication"; c.Username="user"; c.Password="password"; await Executor(r=>Assert.Equal("Basic dXNlcjpwYXNzd29yZA==",r.Headers.Authorization!.ToString())).ExecuteAsync(c,Resolve,default); }
    [Fact] public void JwtClaimsAndSignatureAreGenerated()
    { var c=Config(); c.JwtAlg="HS256 (symmetric)"; c.JwtSigningKey="test-key-with-enough-characters"; c.JwtUseIat=true; c.JwtClaims="{\"sub\":\"user\"}"; var jwt=MakeRequestExecutor.CreateJwt(c,Resolve); Assert.Equal(3,jwt.Split('.').Length); Assert.NotEmpty(jwt.Split('.')[2]); }
    [Fact] public async Task RequestSizeIsBounded()
    { var c=Config(); c.Method="POST"; c.Body=new string('x',MakeRequestExecutor.MaxBytes+1); await Assert.ThrowsAsync<InvalidOperationException>(()=>Executor().ExecuteAsync(c,Resolve,default)); }
    [Fact] public async Task ResponseSizeIsBounded()
    { await Assert.ThrowsAsync<InvalidOperationException>(()=>Executor(body:new string('x',MakeRequestExecutor.MaxBytes+1)).ExecuteAsync(Config(),Resolve,default)); }
}
