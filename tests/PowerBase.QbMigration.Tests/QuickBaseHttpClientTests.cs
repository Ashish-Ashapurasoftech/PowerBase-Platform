using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using PowerBase.Application.QbMigration;
using PowerBase.Infrastructure.QbMigration;
using static PowerBase.QbMigration.Tests.TestData;

namespace PowerBase.QbMigration.Tests;

public class QuickBaseHttpClientTests
{
    private sealed class ScriptedHandler(params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];
        public List<string> Bodies { get; } = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            Bodies.Add(request.Content is null ? "" : await request.Content.ReadAsStringAsync(ct));
            return await steps[Math.Min(Requests.Count - 1, steps.Length - 1)](request, ct);
        }
    }

    private const string OkQuery = """{"data":[{"3":{"value":1},"6":{"value":"a"}}],"fields":[],"metadata":{"totalRecords":1,"numRecords":1,"numFields":2,"skip":0}}""";

    private static Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> Respond(HttpStatusCode status, string body = "{}", Action<HttpResponseMessage>? edit = null) => (_, _) =>
    {
        var r = new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        edit?.Invoke(r);
        return Task.FromResult(r);
    };

    private static (QuickBaseHttpClient Client, ScriptedHandler Handler, List<TimeSpan> Delays) Build(TimeSpan? timeout = null, params Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>[] steps)
    {
        var handler = new ScriptedHandler(steps);
        var delays = new List<TimeSpan>();
        Task Delay(TimeSpan t, CancellationToken _) { delays.Add(t); return Task.CompletedTask; }
        var options = new QuickBaseClientOptions { RealmHostname = "demo.quickbase.com", UserToken = "tok", RequestTimeout = timeout ?? TimeSpan.FromSeconds(60) };
        var http = new HttpClient(handler) { BaseAddress = new Uri(options.BaseUrl), Timeout = Timeout.InfiniteTimeSpan };
        var client = new QuickBaseHttpClient(http, options, new QbRateLimiter(15, Delay), NullLogger<QuickBaseHttpClient>.Instance, Delay);
        return (client, handler, delays);
    }

    [Fact]
    public async Task Sends_the_realm_and_user_token_and_asks_in_record_id_order()
    {
        var (client, handler, _) = Build(null, Respond(HttpStatusCode.OK, OkQuery));
        var page = await client.QueryAsync("bq123", [3, 6], "{3.GTE.1}AND{3.LT.10}", 0, null);
        page.Rows.Single().RecordId.Should().Be(1);
        var req = handler.Requests.Single();
        req.Headers.GetValues("QB-Realm-Hostname").Should().Equal("demo.quickbase.com");
        req.Headers.Authorization!.ToString().Should().Be("QB-USER-TOKEN tok");
        handler.Bodies[0].Should().Contain("\"from\":\"bq123\"").And.Contain("\"sortBy\":[{\"fieldId\":3,\"order\":\"ASC\"}]").And.Contain("{3.GTE.1}AND{3.LT.10}");
    }

    [Fact]
    public async Task A_429_waits_exactly_the_retry_after_time_then_retries()
    {
        var (client, handler, delays) = Build(null,
            Respond(HttpStatusCode.TooManyRequests, "{}", r => r.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromSeconds(7))),
            Respond(HttpStatusCode.OK, OkQuery));
        await client.QueryAsync("t", [3], null, 0, null);
        handler.Requests.Should().HaveCount(2);
        delays.Should().Contain(TimeSpan.FromSeconds(7));
    }

    [Fact]
    public async Task Server_errors_back_off_1_2_4_seconds_then_succeed()
    {
        var (client, handler, delays) = Build(null,
            Respond(HttpStatusCode.InternalServerError), Respond(HttpStatusCode.BadGateway), Respond(HttpStatusCode.ServiceUnavailable),
            Respond(HttpStatusCode.OK, OkQuery));
        await client.QueryAsync("t", [3], null, 0, null);
        handler.Requests.Should().HaveCount(4);
        delays.Where(d => d >= TimeSpan.FromSeconds(1)).Should().Equal(TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(4));
    }

    [Fact]
    public async Task A_400_is_never_retried()
    {
        var (client, handler, _) = Build(null, Respond(HttpStatusCode.BadRequest, """{"message":"Bad request"}"""));
        var act = () => client.QueryAsync("t", [3], null, 0, null);
        (await act.Should().ThrowAsync<QuickBaseRequestException>()).Which.Status.Should().Be(400);
        handler.Requests.Should().HaveCount(1);
    }

    [Fact]
    public async Task Two_timeouts_in_a_row_ask_the_caller_to_halve_the_query()
    {
        Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> hang = async (_, ct) => { await Task.Delay(Timeout.Infinite, ct); return null!; };
        var (client, handler, delays) = Build(TimeSpan.FromMilliseconds(50), hang);
        var act = () => client.QueryAsync("t", [3], null, 0, null);
        await act.Should().ThrowAsync<QuickBaseTimeoutException>();
        handler.Requests.Should().HaveCount(2);
        delays.Should().Contain(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public void Query_pages_report_quickbase_metadata()
    {
        var page = QuickBaseHttpClient.ParseQuery(J("""{"data":[{"3":{"value":4}},{"3":{"value":9}}],"metadata":{"totalRecords":10,"numRecords":2,"numFields":1,"skip":4}}"""));
        page.Should().Match<QbQueryPage>(p => p.TotalRecords == 10 && p.NumRecords == 2 && p.Skip == 4);
        page.Rows.Select(r => r.RecordId).Should().Equal(4, 9);
    }

    [Fact]
    public void Address_sub_fields_are_linked_to_their_composite_field()
    {
        var fields = QuickBaseHttpClient.ParseFields(J("""
            [{"id":20,"label":"Address","fieldType":"address","mode":"","properties":{"compositeFields":[{"id":21},22]}},
             {"id":21,"label":"Address: Street 1","fieldType":"text","properties":{}},
             {"id":22,"label":"Address: City","fieldType":"text"},
             {"id":30,"label":"Total","fieldType":"numeric","mode":"formula"}]
            """));
        fields.Single(f => f.Id == 20).CompositeFids.Should().Equal(21, 22);
        fields.Where(f => f.ParentFid == 20).Select(f => f.Id).Should().Equal(21, 22);
        fields.Single(f => f.Id == 20).Mode.Should().BeNull();
        fields.Single(f => f.Id == 30).IsComputed.Should().BeTrue();
    }
}

public class QbRateLimiterTests
{
    [Fact]
    public async Task Requests_are_spaced_evenly_at_the_configured_rate()
    {
        var now = new DateTime(2026, 1, 1);
        var waits = new List<TimeSpan>();
        var limiter = new QbRateLimiter(8, (t, _) => { waits.Add(t); return Task.CompletedTask; }, () => now);
        for (var i = 0; i < 4; i++) await limiter.WaitAsync(default);
        waits.Should().Equal(TimeSpan.FromSeconds(0.125), TimeSpan.FromSeconds(0.25), TimeSpan.FromSeconds(0.375));
    }

    [Fact]
    public async Task A_pause_after_a_429_holds_every_request()
    {
        var now = new DateTime(2026, 1, 1);
        var waits = new List<TimeSpan>();
        var limiter = new QbRateLimiter(8, (t, _) => { waits.Add(t); return Task.CompletedTask; }, () => now);
        limiter.PauseUntil(now.AddSeconds(5));
        await limiter.WaitAsync(default);
        waits.Should().Equal(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void More_than_quickbase_allows_is_refused()
    {
        var act = () => new QbRateLimiter(20);
        act.Should().Throw<ArgumentOutOfRangeException>();
    }
}
