using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using PowerBase.Application.Pipelines;
using PowerBase.Domain.Exceptions;

namespace PowerBase.UnitTests.Pipelines;

/// <summary>Fetch JSON / Iterate over JSON records, checked against what a live Quickbase realm returned (2026-11-16).</summary>
public class JsonHandlerTests
{
    private const string Users = """
        [{"id":1,"name":"Leanne Graham","email":"Sincere@april.biz","address":{"street":"Kulas Light","city":"Gwenborough"}},
         {"id":2,"name":"Ervin Howell","email":"Shanna@melissa.tv","address":{"street":"Victor Plains","city":"Wisokyburgh"}},
         {"id":3,"name":"Clementine Bauch","email":"Nathan@yesenia.net","address":{"street":"Douglas Extension","city":"McKenziehaven"}},
         {"id":7,"name":"Kurtis Weissnat","email":"Telly.Hoeger@billy.biz","address":{"street":"Rex Trail","city":"Howemouth"}}]
        """;
    private const string Sample = """{"id": 1, "name": "Leanne Graham", "email": "a@b.com", "active": true, "score": 1.5, "address": {"city": "Gwenborough"}, "tags": ["x"]}""";

    private static IterateJsonDefinition Config(string? path = null, long? limit = null, params JsonRecordFilter[] rules) => new()
    {
        JsonSourceStepId = "aa", JsonSchemaSample = Sample, JsonRecordsPath = path, Limit = limit,
        Filters = rules.Length == 0 ? new() : new() { rules.ToList() }
    };
    private static JsonRecordFilter Rule(string field, string condition, string value = "") => new() { Field = field, Condition = condition, Value = JsonValue.Create(value) };

    [Fact]
    public void Iterate_TopLevelArray_ProjectsSampleKeysBesideRawRecord()
    {
        var records = JsonRecordsIterator.Iterate(Users, Config(rules: Rule("id", "equals", "3")));
        Assert.Single(records);
        var item = records[0]!.AsObject();
        // Quickbase: raw_record + id, name, email, address{city}; active/score/tags are not in the record so they are left out.
        Assert.Equal(new[] { "raw_record", "id", "name", "email", "address" }, item.Select(p => p.Key).ToArray());
        Assert.Equal("""{"city":"McKenziehaven"}""", item["address"]!.ToJsonString());
        Assert.Equal("Douglas Extension", item["raw_record"]!["address"]!["street"]!.GetValue<string>());
    }

    [Fact]
    public void Iterate_FiltersBeforeLimit()
    {
        var records = JsonRecordsIterator.Iterate(Users, Config(limit: 1, rules: Rule("email", "ends", ".biz")));
        Assert.Single(records);
        Assert.Equal(1, records[0]!["id"]!.GetValue<int>());
        Assert.Equal(2, JsonRecordsIterator.Iterate(Users, Config(rules: Rule("email", "ends", ".biz"))).Count);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(0L)]
    public void Iterate_ZeroOrEmptyLimitIsUnlimited(long? limit) =>
        Assert.Equal(4, JsonRecordsIterator.Iterate(Users, Config(limit: limit)).Count);

    [Fact]
    public void Iterate_ObjectRootWithoutPath_IsOneRecord()
    {
        const string doc = """{"users":[{"id":1},{"id":2}],"total":208}""";
        var records = JsonRecordsIterator.Iterate(doc, Config());
        Assert.Single(records);
        Assert.Equal("""{"raw_record":{"users":[{"id":1},{"id":2}],"total":208}}""", records[0]!.ToJsonString());
    }

    [Fact]
    public void Iterate_RecordsPath_ReadsNestedArray()
    {
        const string doc = """{"data":{"type":"Orders","records":[{"id":1,"email":"x"},{"id":2,"email":"y"},{"id":3}]}}""";
        var records = JsonRecordsIterator.Iterate(doc, Config("/data/records"));
        Assert.Equal(3, records.Count);
        Assert.Equal("""{"raw_record":{"id":3},"id":3}""", records[2]!.ToJsonString());
    }

    [Fact]
    public void Iterate_MissingRecordsPath_FailsLikeQuickbase()
    {
        var ex = Assert.Throws<PipelineNonRetryableException>(() => JsonRecordsIterator.Iterate("""{"users":[]}""", Config("/usersx")));
        Assert.Equal("Validation error: Invalid JSON records location: '/usersx'", ex.Message);
    }

    [Fact]
    public void Iterate_InvalidDocument_Fails() =>
        Assert.Throws<PipelineNonRetryableException>(() => JsonRecordsIterator.Iterate("<html>", Config()));

    [Fact]
    public void Iterate_NoSample_GivesRawRecordOnly()
    {
        var c = Config(); c.JsonSchemaSample = null;
        Assert.Equal("""{"raw_record":{"id":1}}""", JsonRecordsIterator.Iterate("""[{"id":1}]""", c)[0]!.ToJsonString());
    }

    [Theory]
    [InlineData("email", "starts", "Sincere", 1)]
    [InlineData("email", "not_starts", "Sincere", 3)]
    [InlineData("email", "contains", "@", 4)]
    [InlineData("email", "not_contains", "biz", 2)]
    [InlineData("name", "equals", "Ervin Howell", 1)]
    [InlineData("name", "not_equals", "Ervin Howell", 3)]
    [InlineData("email", "regex", "^[A-Z].*\\.biz$", 2)]
    [InlineData("email", "not_regex", "\\.biz$", 2)]
    [InlineData("id", "gt", "2", 2)]
    [InlineData("id", "lt", "3", 2)]
    [InlineData("id", "has_value", "", 4)]
    [InlineData("phone", "has_no_value", "", 4)]
    [InlineData("id", "equals", "3", 1)]
    public void Iterate_Conditions(string field, string condition, string value, int expected) =>
        Assert.Equal(expected, JsonRecordsIterator.Iterate(Users, Config(rules: Rule(field, condition, value))).Count);

    [Fact]
    public void Iterate_Between_UsesBothValues()
    {
        var rule = Rule("id", "between", "2"); rule.Value2 = JsonValue.Create("3");
        Assert.Equal(2, JsonRecordsIterator.Iterate(Users, Config(rules: rule)).Count);
        rule.Condition = "not_between";
        Assert.Equal(2, JsonRecordsIterator.Iterate(Users, Config(rules: rule)).Count);
    }

    [Fact]
    public void Iterate_OrGroups()
    {
        var c = Config();
        c.Filters = new() { new() { Rule("id", "equals", "1") }, new() { Rule("id", "equals", "7") } };
        Assert.Equal(2, JsonRecordsIterator.Iterate(Users, c).Count);
    }

    [Fact]
    public void Iterate_ResolvesReferencesInFilterValue() =>
        Assert.Single(JsonRecordsIterator.Iterate(Users, Config(rules: Rule("id", "equals", "{{a.id}}")), v => v == "{{a.id}}" ? "2" : v ?? ""));

    [Fact]
    public void FetchDefinition_RequiredFieldsPerAuthenticationSchema()
    {
        Assert.Equal(new[] { "JSON URL: required field" }, new FetchJsonDefinition().Errors());
        Assert.Equal(new[] { "Username: required field", "Password: required field", "JSON URL: required field" },
            new FetchJsonDefinition { AuthenticationSchema = "http-basic" }.Errors());
        Assert.Equal(new[] { "Username: required field", "Password: required field" },
            new FetchJsonDefinition { AuthenticationSchema = "http-digest", JsonUrl = "https://x" }.Errors());
        Assert.Equal(new[] { "Token: required field" }, new FetchJsonDefinition { AuthenticationSchema = "http-qb-token", JsonUrl = "https://x" }.Errors());
        Assert.Contains("Authentication Schema: unallowed value jwt", new FetchJsonDefinition { AuthenticationSchema = "jwt", JsonUrl = "https://x" }.Errors());
    }

    [Fact]
    public void FetchDefinition_ReadsEditorConfig()
    {
        var c = FetchJsonDefinition.Read("""{"authenticationSchema":"http-basic","disableSslVerification":true,"jsonUrl":"https://a","methodType":"POST","username":"u","password":"p","forceContentEncoding":"latin1"}""");
        Assert.Equal("http-basic", c.AuthenticationSchema);
        Assert.True(c.DisableSslVerification);
        Assert.Equal("POST", c.MethodType);
        Assert.Equal("latin1", c.ForceContentEncoding);
        Assert.Empty(c.Errors());
    }

    private static FetchJsonExecutor Executor(Action<HttpRequestMessage>? inspect = null, int status = 200, string body = "[]", string? charset = "utf-8") => new((request, ct) =>
    {
        inspect?.Invoke(request);
        var content = new ByteArrayContent(Encoding.UTF8.GetBytes(body));
        content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = charset };
        var response = new HttpResponseMessage((HttpStatusCode)status) { Content = content };
        response.Headers.TryAddWithoutValidation("X-Test", "1");
        return Task.FromResult(response);
    });

    [Fact]
    public async Task Fetch_SendsHeadersLinesAndBasicAuth_ReturnsStatusAndHeaders()
    {
        HttpRequestMessage? sent = null;
        var c = new FetchJsonDefinition { AuthenticationSchema = "http-basic", Username = "u", Password = "p", JsonUrl = "https://example.com/data.json",
            Headers = "Accept-Language: da, en-gb;q=0.8\nX-Custom: {{a.v}}" };
        var result = await Executor(r => sent = r, body: "[{\"id\":1}]").ExecuteAsync(c, v => (v ?? "").Replace("{{a.v}}", "42"), default);
        Assert.Equal(200, result.Status);
        Assert.Equal("1", result.Headers["X-Test"]);
        Assert.Equal("[{\"id\":1}]", result.Body);
        Assert.Equal("Basic", sent!.Headers.Authorization!.Scheme);
        Assert.Equal(Convert.ToBase64String(Encoding.UTF8.GetBytes("u:p")), sent.Headers.Authorization.Parameter);
        Assert.Equal("42", sent.Headers.GetValues("X-Custom").Single());
        Assert.Equal(HttpMethod.Get, sent.Method);
    }

    [Fact]
    public async Task Fetch_UserToken_UsesQuickbaseHeader()
    {
        HttpRequestMessage? sent = null;
        await Executor(r => sent = r).ExecuteAsync(new FetchJsonDefinition { AuthenticationSchema = "http-qb-token", Token = "pb_ut_x", JsonUrl = "https://e.com" }, v => v ?? "", default);
        Assert.Equal("QB-USER-TOKEN pb_ut_x", sent!.Headers.GetValues("Authorization").Single());
    }

    [Fact]
    public async Task Fetch_PostSendsBody()
    {
        string? body = null;
        await Executor(r => body = r.Content!.ReadAsStringAsync().Result).ExecuteAsync(
            new FetchJsonDefinition { JsonUrl = "https://e.com", MethodType = "POST", Body = "{\"q\":1}" }, v => v ?? "", default);
        Assert.Equal("{\"q\":1}", body);
    }

    [Fact]
    public async Task Fetch_ClientErrorIsNotRetried() =>
        await Assert.ThrowsAsync<PipelineRequestRejectedException>(() =>
            Executor(status: 404).ExecuteAsync(new FetchJsonDefinition { JsonUrl = "https://e.com" }, v => v ?? "", default));

    [Fact]
    public async Task Fetch_InvalidUrl() =>
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            Executor().ExecuteAsync(new FetchJsonDefinition { JsonUrl = "ftp://e.com" }, v => v ?? "", default));

    [Fact]
    public void Fetch_HeaderWithoutColonIsRejected() =>
        Assert.Throws<InvalidOperationException>(() => FetchJsonExecutor.ParseHeaders("not a header"));

    [Fact]
    public void Fetch_ForceContentEncoding_Latin1()
    {
        var encoding = FetchJsonExecutor.ResolveEncoding("latin1", "utf-8");
        Assert.Equal("é", encoding.GetString(new byte[] { 0xE9 }));
        Assert.Throws<InvalidOperationException>(() => FetchJsonExecutor.ResolveEncoding("nope-enc", null));
    }

    [Fact]
    public void IterateDefinition_ReadsQuickbaseFilterShape()
    {
        var c = IterateJsonDefinition.Read("""{"jsonSourceStepId":"aa","jsonRecordsPath":"/users","limit":"2","filters":[[{"field":"email","condition":"ends","value":".biz"}]]}""");
        Assert.Equal("aa", c.JsonSourceStepId);
        Assert.Equal(2, c.Limit);
        Assert.Equal("ends", c.Filters[0][0].Condition);
        Assert.Empty(c.Errors());
        Assert.Contains("JSON Source: required field", new IterateJsonDefinition().Errors());
        Assert.Contains("JSON Schema Sample: invalid JSON", new IterateJsonDefinition { JsonSourceStepId = "aa", JsonSchemaSample = "{bad" }.Errors());
    }
}
