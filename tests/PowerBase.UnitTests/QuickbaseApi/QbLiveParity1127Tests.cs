using System.Text.Json.Nodes;
using System.Xml.Linq;
using FluentAssertions;
using PowerBase.API.QuickbaseApi.Common;
using PowerBase.API.QuickbaseApi.Xml;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

/// <summary>Byte-level layouts measured on the live ysl realm, PF All Types (2026-11-27).</summary>
public class QbLiveParity1127Tests
{
    [Fact]
    public void GetFieldProperties_LayoutMatchesQuickbase()
    {
        var field = new XElement("field", new XAttribute("id", 45), new XAttribute("field_type", "dblink"), new XAttribute("base_type", "text"),
            new XAttribute("mode", "virtual"),
            new XElement("label", "Row records"),
            new XElement("choices", new XElement("choice", "Alpha")),
            new XElement("comma_start", "4"),
            new XElement("fieldhelp", ""),
            new XElement("cover_text", "A \"b\" & c"));
        var xml = new QbXmlResponse("API_GetFieldProperties").Add(field).ToXml(null);
        xml.Should().Be("<?xml version=\"1.0\" ?>\r\n<qdbapi>\n\t<action>API_GetFieldProperties</action>\r\n\t<errcode>0</errcode>\r\n" +
            "\t<errtext>No error</errtext>\r\n<field id=\"45\" field_type=\"dblink\" base_type=\"text\" mode=\"virtual\">\r\n" +
            "          <label>Row records</label>\n          <choices>\n            <choice>Alpha</choice>\n          </choices>\n" +
            "\t\t  <comma_start>4</comma_start>\n          <fieldhelp></fieldhelp>\n          <cover_text>A &quot;b&quot; &amp; c</cover_text>\n" +
            "</field>\r\n</qdbapi>\r\n");
    }

    [Fact]
    public void GetFieldProperties_CompositeFieldsBlockAtColumnZero()
    {
        var field = new XElement("field", new XAttribute("id", 24),
            new XElement("compositeFields", new XElement("compositeField", new XAttribute("id", 25), new XAttribute("key", "street"))));
        var xml = new QbXmlResponse("API_GetFieldProperties").Add(field).ToXml(null);
        xml.Should().Contain("<compositeFields>\n<compositeField id=\"25\" key=\"street\"  />\n</compositeFields>\n</field>\r\n");
    }

    [Theory]
    [InlineData("10:30 AM", "10:30:00")]
    [InlineData("1:05:09 pm", "13:05:09")]
    [InlineData("13:45", "13:45:00")]
    [InlineData("12 AM", "00:00:00")]
    public void ParseTimeOfDay_ReadsQuickbaseText(string text, string expected) =>
        QbXmlRecordActions.ParseTimeOfDay(text)!.Value.ToString(@"hh\:mm\:ss").Should().Be(expected);

    [Fact]
    public void ParseTimeOfDay_Unreadable_IsNull() => QbXmlRecordActions.ParseTimeOfDay("notatime").Should().BeNull();

    [Fact]
    public void SortedJson_OrdersKeysOrdinalEverywhere()
    {
        var node = JsonNode.Parse("{\"metadata\":{\"totalRecords\":1,\"numRecords\":1,\"numFields\":2,\"skip\":0},\"data\":[{\"3\":{\"value\":1},\"11\":{\"value\":2},\"6\":{\"value\":3}}]}");
        QbSortedJsonAttribute.Sort(node)!.ToJsonString().Should().Be(
            "{\"data\":[{\"11\":{\"value\":2},\"3\":{\"value\":1},\"6\":{\"value\":3}}],\"metadata\":{\"numFields\":2,\"numRecords\":1,\"skip\":0,\"totalRecords\":1}}");
    }
}

/// <summary>Encoding and type rules measured on the Visitor Management parity tables (2026-11-27).</summary>
public class QbLiveParityEncodingTests
{
    [Fact]
    public void Xml_NonAscii_IsDecimalCharacterReference() =>
        PowerBase.API.QuickbaseApi.Xml.QbXmlController.AsciiEntities("<p_text>Ünïcødé ✓</p_text>")
            .Should().Be("<p_text>&#220;n&#239;c&#248;d&#233; &#10003;</p_text>");

    [Fact]
    public void Json_NonAscii_IsUpperCaseUnicodeEscape() =>
        QbSortedJsonAttribute.AsciiEscape("{\"6\":{\"value\":\"Ünï & <b>\"}}").Should().Be("{\"6\":{\"value\":\"\\u00DCn\\u00EF & <b>\"}}");

    [Fact]
    public void Json_AsciiOnly_IsUnchanged() => QbSortedJsonAttribute.AsciiEscape("{\"a\":\"x\"}").Should().Be("{\"a\":\"x\"}");

    [Fact]
    public void CombinedTextSummary_IsMultitext()
    {
        var f = new PowerBase.Domain.Entities.AppField { TypeCode = "Summary", Fid = 58, Settings = "{\"Function\":\"CombinedText\",\"ResultTypeCode\":\"Text\"}" };
        PowerBase.API.QuickbaseApi.V1.Services.QbFieldPropertiesBuilder.TypeOf(f).FieldType.Should().Be("multitext");
    }
}

/// <summary>Rules measured with every API on the parity tables (2026-11-27, second pass).</summary>
public class QbLiveParityAllApiTests
{
    [Theory]
    [InlineData("03-01-2026", true, "2026-01-03")]
    [InlineData("03-01-2026", false, "2026-03-01")]
    [InlineData("03-15-2026", true, "2026-03-15")]   // cannot be day-first: falls back to month-first
    [InlineData("15-03-26", true, "2026-03-15")]
    public void ParseDate_HonoursDayFirstApps(string text, bool dayFirst, string expected) =>
        QbXmlRecordActions.ParseDate(text, dayFirst)!.Value.ToString("yyyy-MM-dd").Should().Be(expected);

    [Fact]
    public void Options_AcceptQuickbaseSkpSpelling()
    {
        var o = QbXmlQueryActions.ParseOptions("num-2.skp-1");
        o.Num.Should().Be(2);
        o.Skip.Should().Be(1);
    }

    [Fact]
    public void ErrorStatus_InsufficientPermissionsIs403() =>
        QbXmlErrors.HttpStatus(QbXmlErrors.INSUFFICIENT_PERMISSIONS).Should().Be(403);

    [Fact]
    public void BlankText_IsStoredAsNoValue()
    {
        var f = new PowerBase.Domain.Entities.AppField { TypeCode = "Text", Fid = 6 };
        PowerBase.API.QuickbaseApi.V1.Services.QbRecordService.CheckValue(f, "").Should().BeNull();
    }
}

/// <summary>Sort and query rules measured on the parity tables (2026-11-27, third pass).</summary>
public class QbLiveParitySortQueryTests
{
    private static PowerBase.Domain.Entities.AppField F(string type, string? settings = null) =>
        new() { TypeCode = type, Fid = 9, Settings = settings };

    [Fact]
    public void Choice_SortsByItsListWithBlankFirst()
    {
        var f = F("SingleSelect", "{\"options\":[\"Red\",\"Green\",\"Blue\"]}");
        var vals = new[] { "Blue", "", "Red", "Green" }.OrderBy(v => (decimal)PowerBase.API.QuickbaseApi.V1.Services.QbRecordService.QbOrderValue(f, v)!).ToList();
        vals.Should().Equal("", "Red", "Green", "Blue");
    }

    [Fact]
    public void BlankNumber_SortsJustAboveZero()
    {
        var f = F("Number");
        var blank = (decimal)PowerBase.API.QuickbaseApi.V1.Services.QbRecordService.QbOrderValue(f, null)!;
        blank.Should().BeGreaterThan(0m).And.BeLessThan(0.001m);
    }

    [Fact]
    public void BlankCheckbox_SortsAsUnchecked() =>
        PowerBase.API.QuickbaseApi.V1.Services.QbRecordService.QbOrderValue(F("Boolean"), null).Should().Be(0m);

    [Fact]
    public void Where_DateMilliseconds_BecomeTheInstant()
    {
        var tree = QbWhereParser.Parse("{16.OAF.1773568800000}", new HashSet<int> { 16 }, _ => "timestamp", null);
        tree.Nodes[0].Condition!.Value.Should().Be("2026-03-15T10:00:00.000Z");
    }

    [Fact]
    public void Where_CheckboxZero_AlsoFindsBlank()
    {
        var tree = QbWhereParser.Parse("{19.EX.0}", new HashSet<int> { 19 }, _ => "checkbox", null);
        tree.Nodes[0].Group!.Nodes.Select(n => n.Condition!.Operator).Should().Equal("eq", "isEmpty");
    }
}
