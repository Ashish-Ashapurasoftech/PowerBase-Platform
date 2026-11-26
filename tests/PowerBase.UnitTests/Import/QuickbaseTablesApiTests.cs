using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSubstitute;
using PowerBase.Application.Common.Interfaces;
using PowerBase.Application.Import.Quickbase;
using Xunit;

namespace PowerBase.UnitTests.Import;

public class QuickbaseTablesApiTests
{
    /// <summary>Answers per token (read from the Authorization header); a null answer throws like an unreachable host.</summary>
    private sealed class FakeHandler(Dictionary<string, HttpStatusCode?> byToken) : HttpMessageHandler
    {
        public List<string> Calls { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            var token = request.Headers.GetValues("Authorization").Single().Replace("QB-USER-TOKEN ", "");
            Calls.Add(token);
            var status = byToken[token] ?? throw new HttpRequestException("No such host is known.");
            var body = status == HttpStatusCode.OK
                ? """[{"id":"bteqnetx1","alias":"_DBID_LEA","name":"Lea"}]"""
                : """{"message":"Access denied","description":"User token is not valid for this app"}""";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(body) });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static QuickbaseTablesApi Api(FakeHandler handler, long ownTokenId)
    {
        var catalog = Substitute.For<IQuickbaseCatalogRepository>();
        catalog.ListTokensAsync(Arg.Any<CancellationToken>()).Returns(new List<QuickbaseUserTokenRow>
        {
            new() { Id = 1, Name = "First", Token = "tok1", SortOrder = 1 },
            new() { Id = 2, Name = "Second", Token = "tok2", SortOrder = 2 },
            new() { Id = 3, Name = "Third", Token = "tok3", SortOrder = 3 },
        });
        catalog.FindAppByQbAppIdAsync("bteqnetwv", Arg.Any<CancellationToken>())
            .Returns(new QuickbaseCatalogAppRow { QbAppId = "bteqnetwv", LogicalId = "$App_CARES_Database", UserTokenId = ownTokenId });
        return new QuickbaseTablesApi(new Factory(handler), Options.Create(new QuickbaseImportOptions()), NullLogger<QuickbaseTablesApi>.Instance, catalog);
    }

    [Fact]
    public async Task Own_token_is_tried_first_and_wins()
    {
        var handler = new FakeHandler(new() { ["tok1"] = HttpStatusCode.Unauthorized, ["tok2"] = HttpStatusCode.OK, ["tok3"] = HttpStatusCode.Unauthorized });
        var result = await Api(handler, ownTokenId: 2).GetTablesAsync("bteqnetwv", null);

        Assert.Equal(QuickbaseLookupStatus.Ok, result.Status);
        Assert.Equal(["tok2"], handler.Calls);
    }

    [Fact]
    public async Task A_failed_call_is_reported_instead_of_hiding_behind_other_tokens_no_access()
    {
        var handler = new FakeHandler(new() { ["tok1"] = HttpStatusCode.Unauthorized, ["tok2"] = null, ["tok3"] = HttpStatusCode.Unauthorized });
        var result = await Api(handler, ownTokenId: 2).GetTablesAsync("bteqnetwv", null);

        Assert.Equal(QuickbaseLookupStatus.Failed, result.Status);
        Assert.Contains("could not be reached with the app's own token", result.Message);
        Assert.Contains("No such host is known", result.Message);
    }

    [Fact]
    public async Task Server_error_is_reported_with_its_status()
    {
        var handler = new FakeHandler(new() { ["tok1"] = HttpStatusCode.Unauthorized, ["tok2"] = HttpStatusCode.InternalServerError, ["tok3"] = HttpStatusCode.Unauthorized });
        var result = await Api(handler, ownTokenId: 2).GetTablesAsync("bteqnetwv", null);

        Assert.Equal(QuickbaseLookupStatus.Failed, result.Status);
        Assert.Contains("500", result.Message);
    }

    [Fact]
    public async Task Only_401s_still_mean_no_access()
    {
        var handler = new FakeHandler(new() { ["tok1"] = HttpStatusCode.Unauthorized, ["tok2"] = HttpStatusCode.Forbidden, ["tok3"] = HttpStatusCode.Unauthorized });
        var result = await Api(handler, ownTokenId: 2).GetTablesAsync("bteqnetwv", null);

        Assert.Equal(QuickbaseLookupStatus.NoAccess, result.Status);
        Assert.Equal(3, handler.Calls.Count);
    }
}
