using FluentAssertions;
using PowerBase.Application.Import.Pbl;
using PowerBase.Application.Import.Qbl;
using Xunit;

namespace PowerBase.UnitTests.Import.Qbl;

/// <summary>Regression: no imported PowerFlow keeps a Quickbase URL. Every Quickbase address (API, XML API, pages, computed with
/// Jinja) becomes its PowerBase address, or is removed and reported.</summary>
public class QuickbaseUrlRewriterTests
{
    private static readonly QuickbaseUrlRewriter.Context Ctx = new(id => id is "bwcrtu9k5" or "bwepvdwy6", id => id == "bt2kqxkqk");

    /// <summary>Markers shown readably: {T:id}, {AT:id}, {U}, {P}.</summary>
    private static string Show(string text) => string.Concat(QuickbaseUrlRewriter.Split(text)
        .Select(p => p.Literal ?? "{" + p.Kind + (p.Id is null ? "" : ":" + p.Id) + "}"));

    private static QuickbaseUrlRewriter.Result Run(string text, QuickbaseUrlRewriter.Context? ctx = null) => QuickbaseUrlRewriter.Rewrite(text, ctx ?? Ctx);

    [Theory]
    // Record pages: rid from a step's Record ID# becomes that record's PowerBase id.
    [InlineData("https://ysl.quickbase.com/db/bwcrtu9k5?a=dr&rid={{aa.id}}", "{U}/app/{AT:bwcrtu9k5}/tables/{T:bwcrtu9k5}/records/{{aa.__pb_record}}/view")]
    [InlineData("https://ysl.quickbase.com/db/bwcrtu9k5?a=er&rid={{ ab.record_id_ }}", "{U}/app/{AT:bwcrtu9k5}/tables/{T:bwcrtu9k5}/records/{{ab.__pb_record}}/edit")]
    [InlineData("https://ysl.quickbase.com/db/bwcrtu9k5/form?a=dr&rid={{aa.id}}&rl=yrf&page=2", "{U}/app/{AT:bwcrtu9k5}/tables/{T:bwcrtu9k5}/records/{{aa.__pb_record}}/view")]
    [InlineData("https://ysl.quickbase.com/nav/app/bt2kqxkqk/table/bwcrtu9k5/action/dr?rid={{aa.id}}", "{U}/app/{AT:bwcrtu9k5}/tables/{T:bwcrtu9k5}/records/{{aa.__pb_record}}/view")]
    // Add record, table, app.
    [InlineData("https://ysl.quickbase.com/db/bwcrtu9k5?a=nwr", "{U}/app/{AT:bwcrtu9k5}/tables/{T:bwcrtu9k5}/records/new")]
    [InlineData("https://ysl.quickbase.com/db/bwepvdwy6", "{U}/app/{AT:bwepvdwy6}/tables/{T:bwepvdwy6}")]
    [InlineData("https://ysl.quickbase.com/nav/app/bt2kqxkqk/table/bwcrtu9k5", "{U}/app/{AT:bwcrtu9k5}/tables/{T:bwcrtu9k5}")]
    [InlineData("https://ysl.quickbase.com/db/bt2kqxkqk", "{U}/app/{A:bt2kqxkqk}")]
    [InlineData("https://ysl.quickbase.com/nav/app/bt2kqxkqk", "{U}/app/{A:bt2kqxkqk}")]
    [InlineData("https://ysl.quickbase.com", "{U}/app/apps")]
    // APIs outside a request: on the PowerBase API address.
    [InlineData("https://api.quickbase.com/v1/fields?tableId=bwcrtu9k5", "{P}/v1/fields?tableId={T:bwcrtu9k5}")]
    [InlineData("https://ysl.quickbase.com/db/bwcrtu9k5?a=API_EditRecord&rid={{aa.id}}&_fid_6={{aa.name}}&usertoken=secret", "{P}/db/{T:bwcrtu9k5}?a=API_EditRecord&rid={{aa.id}}&_fid_6={{aa.name}}")]
    [InlineData("https://ysl.quickbase.com/db/bwcrtu9k5?a=API_DoQuery&ticket=auth_ticket&apptoken=abc&qid=1&fmt=structured", "{P}/db/{T:bwcrtu9k5}?a=API_DoQuery&qid=1&fmt=structured")]
    [InlineData("https://ysl.quickbase.com/db/bwcrtu9k5?a=API_DoQueryCount&query={3.GT.0}&usertoken=secret", "{P}/db/{T:bwcrtu9k5}?a=API_DoQueryCount&query=%7B3.GT.0%7D")]
    public void Rewrite_KnownAddresses(string url, string expected)
    {
        var result = Run(url);
        Show(result.Text).Should().Be(expected);
        result.Text.Should().NotContainEquivalentOf("quickbase.com");
    }

    [Fact]
    public void Rewrite_InsideText_KeepsTheTextAround()
    {
        var result = Run("Hi {{aa.name}},\nopen <a href=\"https://ysl.quickbase.com/db/bwcrtu9k5?a=dr&rid={{aa.id}}\">your record</a>. Thanks");

        Show(result.Text).Should().Be("Hi {{aa.name}},\nopen <a href=\"{U}/app/{AT:bwcrtu9k5}/tables/{T:bwcrtu9k5}/records/{{aa.__pb_record}}/view\">your record</a>. Thanks");
        result.Problems.Should().BeEmpty();
        result.Notes.Should().ContainSingle();
    }

    [Fact]
    public void Rewrite_InRequestUrl_ApiIsRelative()
    {
        var result = Run("https://ysl.quickbase.com/db/bwcrtu9k5?a=API_AddRecord&_fid_6=x", Ctx with { InRequestUrl = true });
        Show(result.Text).Should().Be("/db/{T:bwcrtu9k5}?a=API_AddRecord&_fid_6=x");
    }

    [Fact]
    public void Rewrite_ComputedDbid_XmlCallMovesToPowerBase_AndIsFlagged()
    {
        // Quickbase formula style: "https://ysl.quickbase.com/db/" & [Related App] & "?a=API_EditRecord&rid=" & [rid] …
        var result = Run("https://ysl.quickbase.com/db/{{aa.related_app}}?a=API_EditRecord&rid={{aa.triggered_by_rid}}&_fid_{{aa.status_fid}}={{aa.status}}&usertoken={{aa.token}}",
            Ctx with { InRequestUrl = true });

        result.Text.Should().Be("/db/{{aa.related_app}}?a=API_EditRecord&rid={{aa.triggered_by_rid}}&_fid_{{aa.status_fid}}={{aa.status}}");
        result.Problems.Should().ContainSingle().Which.Should().Contain("dbid at run time");
    }

    [Theory]
    [InlineData("https://ysl.quickbase.com/db/bwcrtu9k5?a=dr&rid=12", "Quickbase number")]
    [InlineData("https://ysl.quickbase.com/db/zzzzzzzzz?a=dr&rid={{aa.id}}", "not imported")]
    [InlineData("https://ysl.quickbase.com/db/{{aa.dbid}}?a=dr&rid={{aa.id}}", "run time")]
    [InlineData("https://ysl.quickbase.com/db/bwcrtu9k5?a=API_GetSchema", "API_GetSchema")]
    [InlineData("https://api.quickbase.com/v1/docTemplates/1/generate?tableId=bwcrtu9k5", "no endpoint")]
    [InlineData("https://ysl.quickbase.com/up/bwcrtu9k5/a/r1/e9/v0/file.pdf", "no equivalent")]
    [InlineData("https://ysl.quickbase.com/db/bwcrtu9k5?a=dbpage&pageID=3", "no equivalent")]
    public void Rewrite_NoEquivalent_IsRemovedAndReported(string url, string why)
    {
        var result = Run("see " + url + " now");

        result.Text.Should().Be("see  now");
        result.Problems.Should().ContainSingle().Which.Should().Contain(why).And.Contain("removed");
    }

    [Fact]
    public void Rewrite_Report_OpensTheTable_AndAsksForTheReport()
    {
        var result = Run("https://ysl.quickbase.com/nav/app/bt2kqxkqk/table/bwcrtu9k5/action/q?qid=7&nv=1&v0={{aa.name}}");

        Show(result.Text).Should().Be("{U}/app/{AT:bwcrtu9k5}/tables/{T:bwcrtu9k5}");
        result.Problems.Should().ContainSingle().Which.Should().Contain("report 7");
    }

    [Theory]
    [InlineData("https://www.quickbase.com")]
    [InlineData("http://ysl.quickbase.com/db/main?a=signin")]
    [InlineData("ysl.quickbase.com/db/bwcrtu9k5")]
    [InlineData("HTTPS://YSL.QUICKBASE.COM/db/bwcrtu9k5")]
    public void Rewrite_NothingQuickbaseRemains(string url) =>
        Run(url).Text.Should().NotContainEquivalentOf("quickbase.com");

    [Theory]
    [InlineData("https://notquickbase.example.com/db/x")]
    [InlineData("mail me at quickbase@example.com")]
    [InlineData("from support@quickbase.com and help@ysl.quickbase.com")]
    [InlineData("https://httpbin.org/get")]
    public void Rewrite_OtherAddresses_Untouched(string text)
    {
        var result = Run(text);
        result.Changed.Should().BeFalse();
        result.Text.Should().Be(text);
    }
}
