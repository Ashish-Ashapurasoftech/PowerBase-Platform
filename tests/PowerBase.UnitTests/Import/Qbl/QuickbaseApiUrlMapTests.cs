using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>Quickbase API URL → PowerBase endpoint (Quickbase-compatible /v1 + XML /db, native fallback). Regression guard for
/// the QBL import: every Quickbase URL shape seen in real exports and every compatible endpoint is pinned here.</summary>
public class QuickbaseApiUrlMapTests
{
    private static string Path(IEnumerable<QuickbaseApiUrlMap.Part> parts) => string.Concat(parts.Select(p => p switch
    {
        QuickbaseApiUrlMap.Literal l => l.Text,
        QuickbaseApiUrlMap.TableId t => "{" + t.QbTableId + "}",
        QuickbaseApiUrlMap.AppId a => "{app:" + a.QbAppId + "}",
        QuickbaseApiUrlMap.XmlDbId d => "{db:" + d.QbId + "}",
        _ => "?",
    }));

    [Theory]
    // Compatible v1 API: same path, ids replaced.
    [InlineData("POST", "https://api.quickbase.com/v1/records/query", "{\"from\":\"bwcrtu9k5\"}", "POST", "/v1/records/query", false)]
    [InlineData("GET", "https://api.quickbase.com/v1/records/query", "{\"from\":\"bwcrtu9k5\"}", "POST", "/v1/records/query", false)]
    [InlineData("POST", "https://api.quickbase.com/v1/records", "{\"to\":\"bwcrtu9k5\",\"data\":[]}", "POST", "/v1/records", false)]
    [InlineData("DELETE", "https://api.quickbase.com/v1/records", "{\"from\":\"bwcrtu9k5\",\"where\":\"{3.EX.1}\"}", "DELETE", "/v1/records", false)]
    [InlineData("GET", "https://api.quickbase.com/v1/fields?tableId=bwcrtu9k5", null, "GET", "/v1/fields?tableId={bwcrtu9k5}", false)]
    [InlineData("GET", "https://api.quickbase.com/v1/fields?tableId=bwcrtu9k5&includeFieldPerms=true", null, "GET", "/v1/fields?tableId={bwcrtu9k5}&includeFieldPerms=true", false)]
    [InlineData("GET", "https://api.quickbase.com/v1/tables?appId=bt2kqxkqk", null, "GET", "/v1/tables?appId={app:bt2kqxkqk}", false)]
    [InlineData("GET", "https://api.quickbase.com/v1/reports/1?tableId=bwcrtu9k5", null, "GET", "/v1/reports/1?tableId={bwcrtu9k5}", false)]
    [InlineData("POST", "https://api.quickbase.com/v1/reports/5/run?tableId=bwcrtu9k5&skip=0&top=10", null, "POST", "/v1/reports/5/run?tableId={bwcrtu9k5}&skip=0&top=10", false)]
    [InlineData("GET", "https://api.quickbase.com/v1/files/bwcrtu9k5/12/9/0", null, "GET", "/v1/files/{bwcrtu9k5}/12/9/0", false)]
    [InlineData("DELETE", "https://api.quickbase.com/v1/files/bwcrtu9k5/12/9/2", null, "DELETE", "/v1/files/{bwcrtu9k5}/12/9/2", false)]
    // Not in the compatible API: native endpoint, response differs.
    [InlineData("GET", "https://api.quickbase.com/v1/tables/bwcrtu9k5?appId=bt2kqxkqk", null, "GET", "/tables/{bwcrtu9k5}", true)]
    [InlineData("GET", "https://api.quickbase.com/v1/apps/bt2kqxkqk", null, "GET", "/apps/{app:bt2kqxkqk}", true)]
    // XML API: same action, credentials dropped.
    [InlineData("GET", "https://ysl.quickbase.com/db/bwcrtu9k5?a=API_AddRecord&_fid_6=x&usertoken=abc", null, "GET", "/db/{db:bwcrtu9k5}?a=API_AddRecord&_fid_6=x", false)]
    [InlineData("POST", "https://ysl.quickbase.com/db/main?a=API_GetUserInfo&ticket=t", null, "POST", "/db/main?a=API_GetUserInfo", false)]
    public void Map_KnownEndpoints(string method, string url, string? body, string newMethod, string path, bool responseDiffers)
    {
        var mapped = QuickbaseApiUrlMap.Map(method, new Uri(url), body, out _);

        mapped.Should().NotBeNull();
        mapped!.Method.Should().Be(newMethod);
        Path(mapped.Path).Should().Be(path);
        mapped.ResponseDiffers.Should().Be(responseDiffers);
    }

    [Theory]
    [InlineData("POST", "https://api.quickbase.com/v1/formula/run", "{}", "no endpoint")]
    [InlineData("GET", "https://api.quickbase.com/v1/fields", null, "which table")]
    [InlineData("GET", "https://api.quickbase.com/v1/tables", null, "which app")]
    [InlineData("GET", "https://ysl.quickbase.com/db/bt2kqxkqk?a=API_DoQuery", null, "no XML API action 'API_DoQuery'")]
    [InlineData("GET", "https://api.quickbase.com/v2/records", null, "not a Quickbase JSON API")]
    public void Map_NoEquivalent_KeepsUrlWithReason(string method, string url, string? body, string reason)
    {
        QuickbaseApiUrlMap.Map(method, new Uri(url), body, out var why).Should().BeNull();
        why.Should().Contain(reason);
    }

    [Fact]
    public void Map_QueryWithoutBody_IsMappedButNeedsReview()
    {
        var mapped = QuickbaseApiUrlMap.Map("GET", new Uri("https://api.quickbase.com/v1/records/query"), null, out _);
        mapped!.Review.Should().Contain("no body");
        mapped.BodyTableIds.Should().BeEmpty();
    }

    [Fact]
    public void Map_ReportIds_NeedReview()
    {
        QuickbaseApiUrlMap.Map("GET", new Uri("https://api.quickbase.com/v1/reports/1?tableId=bwcrtu9k5"), null, out _)!
            .Review.Should().Contain("Quickbase report number");
    }

    [Fact]
    public void Map_XmlBodyCredentials_NeedReview()
    {
        QuickbaseApiUrlMap.Map("POST", new Uri("https://ysl.quickbase.com/db/bwcrtu9k5?a=API_EditRecord"),
            "<qdbapi><usertoken>x</usertoken><rid>1</rid></qdbapi>", out _)!.Review.Should().Contain("credentials");
    }

    [Theory]
    [InlineData("{\"from\":\"bwcrtu9k5\",\"where\":\"{6.EX.'a'}\"}", "bwcrtu9k5")]
    [InlineData("{ \"to\" : \"bwepvdwy6\", \"data\": [{\"6\":{\"value\":\"{{a.name}}\"}}] }", "bwepvdwy6")]
    [InlineData("{\"tableId\":\"bwcrtu9k5\"}", "bwcrtu9k5")]
    public void BodyTableIds_FindsFromToTableId_EvenWithJinja(string body, string id)
    {
        QuickbaseApiUrlMap.BodyTableIds(body).Should().Equal(id);
    }

    [Fact]
    public void BodyTableIds_IgnoresOtherKeysAndValues()
    {
        QuickbaseApiUrlMap.BodyTableIds("{\"name\":\"bwcrtu9k5\",\"from\":\"{{a.t}}\"}").Should().BeEmpty();
        QuickbaseApiUrlMap.BodyTableIds(null).Should().BeEmpty();
    }

    [Fact]
    public void SplitBody_ReplacesOnlyTheTableIdValue()
    {
        var parts = QuickbaseApiUrlMap.SplitBody("{\"from\":\"bwcrtu9k5\",\"where\":\"{6.EX.'bwcrtu9k5'}\"}", new[] { "bwcrtu9k5" });

        Path(parts!).Should().Be("{\"from\":\"{bwcrtu9k5}\",\"where\":\"{6.EX.'bwcrtu9k5'}\"}");
    }

    [Fact]
    public void SplitBody_NothingToReplace_IsNull()
    {
        QuickbaseApiUrlMap.SplitBody("{\"from\":\"bwcrtu9k5\"}", new[] { "zzzzzzzzz" }).Should().BeNull();
    }

    [Theory]
    [InlineData("https://api.quickbase.com/v1/records/query", true)]
    [InlineData("https://ysl.quickbase.com/db/bwcrtu9k5?a=API_AddRecord", true)]
    [InlineData("https://ysl.quickbase.com/v1/fields?tableId=x", true)]
    [InlineData("https://ysl.quickbase.com/nav/app/bt2kqxkqk", false)]
    [InlineData("https://httpbin.org/get", false)]
    public void IsQuickbaseApi(string url, bool expected) =>
        QuickbaseApiUrlMap.IsQuickbaseApi(new Uri(url)).Should().Be(expected);
}
