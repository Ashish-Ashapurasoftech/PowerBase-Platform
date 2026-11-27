using System.Xml.Linq;
using FluentAssertions;
using PowerBase.API.QuickbaseApi.V1.Models;
using PowerBase.API.QuickbaseApi.V1.Services;
using PowerBase.API.QuickbaseApi.Xml;
using Xunit;

namespace PowerBase.UnitTests.QuickbaseApi;

/// <summary>XML answers byte for byte as a live Quickbase realm writes them (measured 2026-11-24 on ysl, GET calls only).</summary>
public class QbXmlLayoutTests
{
    [Fact]
    public void Generic_UsesCrlf_ExceptAfterQdbapi()
    {
        var xml = new QbXmlResponse("API_AddRecord").Add("rid", 15).Add("update_id", 1773583200000).ToXml(null);
        xml.Should().Be("<?xml version=\"1.0\" ?>\r\n<qdbapi>\n\t<action>API_AddRecord</action>\r\n\t<errcode>0</errcode>\r\n"
                        + "\t<errtext>No error</errtext>\r\n\t<rid>15</rid>\r\n\t<update_id>1773583200000</update_id>\r\n</qdbapi>\r\n");
    }

    [Fact]
    public void Error_UsesCrlf()
    {
        var xml = new QbXmlResponse("API_GetUserRole").ToXml(null, QbXmlErrors.UNKNOWN_USER, "Unknown user");
        xml.Should().Be("<?xml version=\"1.0\" ?>\r\n<qdbapi>\n\t<action>API_GetUserRole</action>\r\n\t<errcode>21</errcode>\r\n"
                        + "\t<errtext>Unknown user</errtext>\r\n</qdbapi>\r\n");
    }

    private static XElement Role(string id, string name, string accessId, string access) =>
        new("role", new XAttribute("id", id), new XElement("name", name), new XElement("access", new XAttribute("id", accessId), access));

    [Fact]
    public void GetUserInfo_QuickbaseLayout()
    {
        var xml = new QbXmlResponse("API_GetUserInfo").Add(new XElement("user", new XAttribute("id", "u1"),
            new XElement("firstName", "YSL"), new XElement("lastName", "Dev"), new XElement("login", "YslDevTeam"),
            new XElement("email", "dev@x.com"), new XElement("screenName", "YslDevTeam"), new XElement("isVerified", 1),
            new XElement("externalAuth", 0))).ToXml(null);
        xml.Should().Be("<?xml version=\"1.0\" ?>\r\n<qdbapi>\n\t<action>API_GetUserInfo</action>\r\n\t<errcode>0</errcode>\r\n"
                        + "\t<errtext>No error</errtext>\r\n\t<user id=\"u1\">\r\n\n"
                        + " <firstName>YSL</firstName> <lastName>Dev</lastName> <login>YslDevTeam</login> <email>dev@x.com</email> <screenName>YslDevTeam</screenName> <isVerified>1</isVerified>\n"
                        + " <externalAuth>0</externalAuth>\r\n\t</user>\r\n</qdbapi>\r\n");
    }

    [Fact]
    public void GetUserRole_QuickbaseLayout()
    {
        var xml = new QbXmlResponse("API_GetUserRole").Add(new XElement("user", new XAttribute("id", "u1"),
            new XElement("name", "YSL Developers"), new XElement("roles", Role("9", "none", "0", "None")))).ToXml(null);
        xml.Should().Be("<?xml version=\"1.0\" ?>\r\n<qdbapi>\n\t<action>API_GetUserRole</action>\r\n\t<errcode>0</errcode>\r\n"
                        + "\t<errtext>No error</errtext>\r\n\t<user id=\"u1\">\r\n\n <name>YSL Developers</name>\n <roles>\n"
                        + "\t <role id=\"9\">\n   <name>none</name>\n   <access id=\"0\">None</access>\n  </role>\n </roles>\n\r\n\t</user>\r\n</qdbapi>\r\n");
    }

    [Fact]
    public void UserRoles_QuickbaseLayout()
    {
        var users = new XElement("users",
            new XElement("user", new XAttribute("type", "user"), new XAttribute("id", "u1"), new XElement("name", "YSL Developers"),
                new XElement("lastAccess", ""), new XElement("lastAccessAppLocal", ""), new XElement("firstName", "YSL"),
                new XElement("lastName", "Developers"), new XElement("roles", Role("9", "none", "0", "None"))),
            new XElement("user", new XAttribute("type", "user"), new XAttribute("id", "u2"), new XElement("name", "Support"),
                new XElement("lastAccess", "1795032795390"), new XElement("lastAccessAppLocal", "11-18-2026 03:13 PM"),
                new XElement("firstName", "YSL"), new XElement("lastName", "Support"), new XElement("roles", Role("12", "Administrator", "1", "Administrator"))));
        var xml = new QbXmlResponse("API_UserRoles").Add(users).ToXml(null);
        xml.Should().Be("<?xml version=\"1.0\" ?>\r\n<qdbapi>\n\t<action>API_UserRoles</action>\r\n\t<errcode>0</errcode>\r\n"
                        + "\t<errtext>No error</errtext>\r\n\t<users>\r\n\n"
                        + " <user type=\"user\" id=\"u1\">\n  <name>YSL Developers</name>\n  <lastAccess></lastAccess>\n  <lastAccessAppLocal></lastAccessAppLocal>\n"
                        + "  <firstName>YSL</firstName>\n  <lastName>Developers</lastName>\n  <roles>\n\t  <role id=\"9\">\n    <name>none</name>\n"
                        + "    <access id=\"0\">None</access>\n   </role>\n  </roles>\n </user>\n"
                        + " <user type=\"user\" id=\"u2\">\n  <name>Support</name>\n  <lastAccess>1795032795390</lastAccess>\n"
                        + "  <lastAccessAppLocal>11-18-2026 03:13 PM</lastAccessAppLocal>\n  <firstName>YSL</firstName>\n  <lastName>Support</lastName>\n"
                        + "  <roles>\n\t  <role id=\"12\">\n    <name>Administrator</name>\n    <access id=\"1\">Administrator</access>\n   </role>\n  </roles>\n </user>\n"
                        + "\n\n\r\n\t</users>\r\n</qdbapi>\r\n");
    }

    [Fact]
    public void Udata_IsKept_WithCustomLayout()
    {
        var xml = new QbXmlResponse("API_GetUserRole").Add(new XElement("user", new XAttribute("id", "u1"),
            new XElement("name", "A & B"), new XElement("roles"))).ToXml("ref1");
        xml.Should().Contain("\t<udata>ref1</udata>\r\n\t<user id=\"u1\">\r\n\n <name>A &amp; B</name>\n");
    }

    [Theory]
    [InlineData(2, "DESC", true)]
    [InlineData(2, "ASC", false)]
    [InlineData(6, "DESC", false)]
    public void OnlyNewOrChanged_IsDateModifiedNewestFirst(long fid, string order, bool expected) =>
        QbReportService.OnlyNewOrChanged(new[] { new QbSortBy(fid, order) }).Should().Be(expected);
}
