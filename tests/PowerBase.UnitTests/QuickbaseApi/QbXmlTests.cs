using System.Xml.Linq;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Primitives;
using PowerBase.API.QuickbaseApi.Xml;
using PowerBase.Domain.Entities;
using PowerBase.Domain.Exceptions;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

public class QbXmlTests
{
    private static IQueryCollection Query(params (string, string)[] items) =>
        new QueryCollection(items.ToDictionary(i => i.Item1, i => new StringValues(i.Item2)));

    [Fact]
    public void Parse_OfficialAddRecordBody_ReadsParamsAndFields()
    {
        var body = "<qdbapi><udata>mydata</udata><ticket>auth_ticket</ticket><apptoken>app_token</apptoken>" +
                   "<field name=\"event_name\">party at Lindisfarne</field><field fid=\"22\" filename=\"Model_T.jpg\">AAAA</field></qdbapi>";
        var req = QbXmlRequest.Parse("db1", "API_AddRecord", Query(), body);
        req.Action.Should().Be("API_AddRecord");
        req.Udata.Should().Be("mydata");
        req.Get("apptoken").Should().Be("app_token");
        req.Fields.Should().HaveCount(2);
        req.Fields[0].Should().Be(new QbXmlField(null, "event_name", null, "party at Lindisfarne"));
        req.Fields[1].Should().Be(new QbXmlField(22, null, "Model_T.jpg", "AAAA"));
    }

    [Fact]
    public void Parse_UrlForm_ReadsActionFidAndFnmParams()
    {
        var req = QbXmlRequest.Parse("db1", null, Query(("a", "API_EditRecord"), ("rid", "154"), ("_fnm_second_year", "1776"), ("_fid_8", "changed")), null);
        req.Action.Should().Be("API_EditRecord");
        req.Get("rid").Should().Be("154");
        req.Fields.Should().Contain(new QbXmlField(8, null, null, "changed"));
        req.Fields.Should().Contain(new QbXmlField(null, "second_year", null, "1776"));
    }

    [Fact]
    public void Parse_BadXml_Throws11()
    {
        var act = () => QbXmlRequest.Parse("db1", "API_AddRecord", Query(), "<qdbapi><x></qdbapi>");
        act.Should().Throw<QbXmlException>().Which.Code.Should().Be(QbXmlErrors.XML_PARSE);
    }

    [Fact]
    public void Response_Success_HasActionErrcodeErrtextUdataAndBody()
    {
        var xml = new QbXmlResponse("API_AddRecord").Add("rid", 21).Add("update_id", 1206177014451L).ToXml("mydata");
        var root = XDocument.Parse(xml).Root!;
        root.Name.LocalName.Should().Be("qdbapi");
        root.Elements().Select(e => e.Name.LocalName).Should().Equal("action", "errcode", "errtext", "udata", "rid", "update_id");
        root.Element("errcode")!.Value.Should().Be("0");
        root.Element("errtext")!.Value.Should().Be("No error");
        root.Element("rid")!.Value.Should().Be("21");
    }

    [Fact]
    public void Response_Error_OmitsBody()
    {
        var xml = new QbXmlResponse("API_EditRecord").Add("rid", 1).ToXml(null, 60, "Update conflict detected", "stale");
        var root = XDocument.Parse(xml).Root!;
        root.Element("errcode")!.Value.Should().Be("60");
        root.Element("errdetail")!.Value.Should().Be("stale");
        root.Element("rid").Should().BeNull();
    }

    [Fact]
    public void From_NotFoundRecord_Is30_AndUnauthorized_Is3()
    {
        QbXmlErrors.From(new NotFoundException("Record", 5)).Code.Should().Be(QbXmlErrors.NO_SUCH_RECORD);
        QbXmlErrors.From(new NotFoundException("Table", 5)).Code.Should().Be(QbXmlErrors.BAD_DBID);
        QbXmlErrors.From(new UnauthorizedActionException("x")).Code.Should().Be(QbXmlErrors.INSUFFICIENT_PERMISSIONS);
    }

    [Theory]
    [InlineData("06-08-2008", 2008, 6, 8)]
    [InlineData("1206177014451", 2008, 3, 22)]
    [InlineData("2024-01-31", 2024, 1, 31)]
    public void ParseDate_QuickbaseFormats(string text, int y, int m, int d)
    {
        var date = QbXmlRecordActions.ParseDate(text)!.Value;
        date.Year.Should().Be(y);
        date.Month.Should().Be(m);
        date.Day.Should().Be(d);
    }

    [Theory]
    [InlineData("Event Name", "event_name")]
    [InlineData("  Vehicle Make ", "vehicle_make")]
    [InlineData("Record ID#", "record_id")]
    public void NormalizeName_MatchesQuickbaseFieldNames(string label, string expected)
    {
        QbXmlRecordActions.NormalizeName(label).Should().Be(expected);
    }

    [Fact]
    public void FieldElement_HasOfficialAttributesAndChildren()
    {
        var el = QbXmlRecordActions.FieldElement(new AppField { Fid = 35, TypeCode = "Url", Label = "Add URL Action", IsSearchable = true });
        el.Attribute("id")!.Value.Should().Be("35");
        el.Attribute("field_type")!.Value.Should().Be("url");
        el.Attribute("base_type")!.Value.Should().Be("text");
        // Element list and order measured on a live Quickbase realm for a plain URL field (no mode attribute, no formula).
        el.Attribute("mode").Should().BeNull();
        el.Elements().Select(e => e.Name.LocalName).Should().Equal(
            "label", "nowrap", "bold", "required", "appears_by_default", "find_enabled", "allow_new_choices", "sort_as_given",
            "carrychoices", "foreignkey", "unique", "doesdatacopy", "fieldhelp", "audited", "abbreviate", "use_new_window",
            "auto_save", "embed_in_forms");
        el.Element("find_enabled")!.Value.Should().Be("1");
    }

    [Fact]
    public void ReadToken_PrefersUsertokenThenTicket()
    {
        QbXmlTokenMiddleware.ReadToken("<qdbapi><usertoken>pb_ut_x</usertoken><ticket>t</ticket></qdbapi>").Should().Be("pb_ut_x");
        QbXmlTokenMiddleware.ReadToken("<qdbapi><ticket>t</ticket></qdbapi>").Should().Be("t");
        QbXmlTokenMiddleware.ReadToken("not xml").Should().BeNull();
    }
}
