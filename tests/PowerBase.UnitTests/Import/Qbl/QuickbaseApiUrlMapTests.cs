using FluentAssertions;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

public class QuickbaseApiUrlMapTests
{
    private static string Path(QuickbaseApiUrlMap.Mapped m) => string.Concat(m.Path.Select(p => p switch
    {
        QuickbaseApiUrlMap.Literal l => l.Text,
        QuickbaseApiUrlMap.TableId t => "{" + t.QbTableId + "}",
        QuickbaseApiUrlMap.AppId a => "{app:" + a.QbAppId + "}",
        _ => "?",
    }));

    [Theory]
    [InlineData("POST", "https://api.quickbase.com/v1/records/query", "{\"from\":\"bwcrtu9k5\"}", "GET", "/tables/{bwcrtu9k5}/records", true)]
    [InlineData("POST", "https://api.quickbase.com/v1/records", "{\"to\":\"bwcrtu9k5\",\"data\":[]}", "POST", "/tables/{bwcrtu9k5}/records/bulk-create", true)]
    [InlineData("DELETE", "https://api.quickbase.com/v1/records", "{\"from\":\"bwcrtu9k5\",\"where\":\"{3.EX.1}\"}", "POST", "/tables/{bwcrtu9k5}/records/bulk-delete", true)]
    [InlineData("GET", "https://api.quickbase.com/v1/fields?tableId=bwcrtu9k5", null, "GET", "/tables/{bwcrtu9k5}/fields", false)]
    [InlineData("GET", "https://api.quickbase.com/v1/reports?tableId=bwcrtu9k5", null, "GET", "/tables/{bwcrtu9k5}/reports", false)]
    [InlineData("GET", "https://api.quickbase.com/v1/tables/bwcrtu9k5?appId=bt2kqxkqk", null, "GET", "/tables/{bwcrtu9k5}", false)]
    [InlineData("GET", "https://api.quickbase.com/v1/tables?appId=bt2kqxkqk", null, "GET", "/apps/{app:bt2kqxkqk}/tables", false)]
    [InlineData("GET", "https://api.quickbase.com/v1/apps/bt2kqxkqk", null, "GET", "/apps/{app:bt2kqxkqk}", false)]
    public void Map_KnownEndpoints(string method, string url, string? body, string newMethod, string path, bool bodyDiffers)
    {
        var mapped = QuickbaseApiUrlMap.Map(method, new Uri(url), body, out _);

        mapped.Should().NotBeNull();
        mapped!.Method.Should().Be(newMethod);
        Path(mapped).Should().Be(path);
        mapped.BodyDiffers.Should().Be(bodyDiffers);
    }

    [Theory]
    [InlineData("GET", "https://api.quickbase.com/v1/records/query", null, "does not say which table")]
    [InlineData("POST", "https://api.quickbase.com/v1/records/query", "{\"from\":\"{{a.table}}\"}", "does not say which table")]
    [InlineData("POST", "https://api.quickbase.com/v1/formula/run", "{}", "no endpoint")]
    [InlineData("GET", "https://ysl.quickbase.com/db/bt2kqxkqk?a=API_DoQuery", null, "not a Quickbase JSON API")]
    public void Map_NoEquivalent_KeepsUrlWithReason(string method, string url, string? body, string reason)
    {
        QuickbaseApiUrlMap.Map(method, new Uri(url), body, out var why).Should().BeNull();
        why.Should().Contain(reason);
    }
}
