using FluentAssertions;
using PowerBase.API.QuickbaseApi.Xml;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

/// <summary>API_DoQuery / API_DoQueryCount shapes, measured against a live Quickbase realm (PF All Types tables).</summary>
public class QbXmlDoQueryTests
{
    [Theory]
    [InlineData("Record ID#", "record_id_")]
    [InlineData("T Text", "t_text")]
    [InlineData("Parent - T Text", "parent___t_text")]
    [InlineData("Date Created", "date_created")]
    [InlineData("1201", "_1201")]
    [InlineData("Tax", "tax")]
    public void ElementName_IsQuickbaseLabelTag(string label, string expected) =>
        QbXmlQueryActions.ElementName(label).Should().Be(expected);

    [Fact]
    public void ParseOptions_ReadsEveryOption()
    {
        var o = QbXmlQueryActions.ParseOptions("num-2.skip-1.sortorder-D.onlynew.nosort");
        o.Num.Should().Be(2);
        o.Skip.Should().Be(1);
        o.SortOrder.Should().Be("D");
        o.OnlyNew.Should().BeTrue();
        o.NoSort.Should().BeTrue();
    }

    [Fact]
    public void ParseOptions_Empty_Defaults()
    {
        var o = QbXmlQueryActions.ParseOptions(null);
        o.Num.Should().BeNull();
        o.Skip.Should().Be(0);
        o.SortOrder.Should().BeEmpty();
        o.OnlyNew.Should().BeFalse();
    }

    [Fact]
    public void ParseOptions_MultiFieldSortOrder() =>
        QbXmlQueryActions.ParseOptions("sortorder-D-A").SortOrder.Should().Be("DA");

    [Fact]
    public void ParseFidList_KeepsOrder_DropsJunk() =>
        QbXmlQueryActions.ParseFidList("15.6.x.3.").Should().Equal(15, 6, 3);

    [Theory]
    [InlineData(50.0, "numeric", 2, "50.00")]
    [InlineData(7.25, "numeric", 2, "7.25")]
    [InlineData(14.5, "numeric", null, "14.5")]
    [InlineData(100.0, "numeric", null, "100")]
    [InlineData(4.0, "rating", 1, "4.0")]
    [InlineData(0.5, "percent", 1, "0.500")]
    [InlineData(0.125, "percent", 1, "0.125")]
    [InlineData(4001.0, "currency", 2, "4001.00")]
    [InlineData(12.0, "recordid", null, "12")]
    [InlineData(14400000.0, "duration", null, "14400000")]
    public void XmlValue_Numbers(double value, string type, int? decimals, string expected) =>
        QbXmlQueryActions.XmlValue((decimal)value, type, decimals).Should().Be(expected);

    [Theory]
    [InlineData("2026-04-14", "date", "1776124800000")]
    [InlineData("2026-04-14T04:30:00Z", "timestamp", "1776141000000")]
    [InlineData("04:45:00", "timeofday", "17100000")]
    public void XmlValue_DatesAreMilliseconds(string value, string type, string expected) =>
        QbXmlQueryActions.XmlValue(value, type, null).Should().Be(expected);

    [Fact]
    public void XmlValue_Checkbox_And_Empty()
    {
        QbXmlQueryActions.XmlValue(true, "checkbox", null).Should().Be("1");
        QbXmlQueryActions.XmlValue(false, "checkbox", null).Should().Be("0");
        QbXmlQueryActions.XmlValue(null, "checkbox", null).Should().Be("0");
        QbXmlQueryActions.XmlValue(null, "text", null).Should().Be("");
    }

    [Fact]
    public void XmlValue_MultilineUsesBr_AndRichTextIsEscaped()
    {
        QbXmlQueryActions.XmlValue("line one 4\nline two", "text-multi-line", null).Should().Be("line one 4<BR/>\nline two");
        QbXmlQueryActions.XmlValue("<b>rich 4</b>", "rich-text", null).Should().Be("&lt;b&gt;rich 4&lt;/b&gt;");
    }

    [Fact]
    public void XmlValue_MultiSelectJoinedBySemicolon() =>
        QbXmlQueryActions.XmlValue(new List<string> { "Red", "Green" }, "multitext", null).Should().Be("Red;Green");

    [Fact]
    public void Header_MatchesQuickbaseBytes()
    {
        QbXmlQueryActions.Header("API_DoQueryCount", null).Should().Be(
            "<?xml version=\"1.0\" ?>\r\n<qdbapi>\n\t<action>API_DoQueryCount</action>\r\n\t<errcode>0</errcode>\r\n\t<errtext>No error</errtext>\r\n");
        QbXmlQueryActions.Header("API_DoQuery", "hi").Should().EndWith("\t<udata>hi</udata>\r\n");
    }

    [Fact]
    public void ErrorResponse_UsesQuickbaseLineEnds_And404ForNoSuchQuery()
    {
        var xml = new QbXmlResponse("API_DoQuery").ToXml(null, QbXmlErrors.NO_SUCH_QUERY, "No such query", "x");
        var crlf = QbXmlQueryActions.ToCrlf(xml);
        crlf.Should().StartWith("<?xml version=\"1.0\" ?>\r\n<qdbapi>\n\t<action>API_DoQuery</action>\r\n");
        crlf.Should().EndWith("</qdbapi>\r\n");
        QbXmlErrors.HttpStatus(QbXmlErrors.NO_SUCH_QUERY).Should().Be(404);
        QbXmlQueryActions.UsesCrlf("api_doquery").Should().BeTrue();
        QbXmlQueryActions.UsesCrlf("API_AddRecord").Should().BeFalse();
    }

    [Fact]
    public void Qyopts_MatchesQuickbase()
    {
        QbXmlQueryActions.Qyopts(Array.Empty<PowerBase.API.QuickbaseApi.V1.Models.QbSortBy>()).Should().Be("nos.qws.");
        QbXmlQueryActions.Qyopts(new[] { new PowerBase.API.QuickbaseApi.V1.Models.QbSortBy(2, "DESC") }).Should().Be("so-D.onlynew.nos.qws.");
        QbXmlQueryActions.Qyopts(new[] { new PowerBase.API.QuickbaseApi.V1.Models.QbSortBy(6, "ASC"), new PowerBase.API.QuickbaseApi.V1.Models.QbSortBy(3, "DESC") })
            .Should().Be("so-AD.nos.qws.");
        QbXmlQueryActions.Qyopts(new[] { new PowerBase.API.QuickbaseApi.V1.Models.QbSortBy(2, "ASC") }).Should().Be("so-A.nos.qws.");
    }

    private static PowerBase.Domain.Entities.Report R(long id, string name, bool deleted = false, bool settings = false) =>
        new() { Id = id, PublicId = Guid.NewGuid(), Name = name, IsDeleted = deleted, IsDefaultSettingsRecord = settings };

    [Fact]
    public void QueryNumbers_FollowQuickbase()
    {
        var listAll = R(10, "List All"); var changes = R(11, "List Changes"); var settings = R(12, "Default Report Settings", settings: true);
        var a = R(13, "A"); var gone = R(14, "Gone", deleted: true); var b = R(15, "B");
        var n = QbXmlQueryActions.QueryNumbers(new[] { b, gone, a, settings, changes, listAll });
        n[listAll.PublicId].Should().Be(1);
        n[changes.PublicId].Should().Be(2);
        n[a.PublicId].Should().Be(5);
        n[gone.PublicId].Should().Be(6);   // a deleted report keeps its number
        n[b.PublicId].Should().Be(7);
        n.ContainsKey(settings.PublicId).Should().BeFalse();
    }

    [Fact]
    public void QueryNumbers_WithoutDefaultReports_StartAtFive()
    {
        var x = R(1, "Admin"); var y = R(2, "Closed");
        var n = QbXmlQueryActions.QueryNumbers(new[] { x, y });
        n[x.PublicId].Should().Be(5);
        n[y.PublicId].Should().Be(6);
    }
}
